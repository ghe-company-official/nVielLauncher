using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;

namespace nVielLauncher.Services;

public enum UpdateStatus
{
    Available,
    UpToDate,
    NoReleases,
    Error
}

public sealed record UpdateInfo(
    string Version,
    string Tag,
    string Name,
    string Notes,
    string DownloadUrl,
    long Size,
    string? Sha256,
    string ReleaseUrl,
    string ApiUrl = "",
    string? SignatureUrl = null,
    string? SignatureApiUrl = null);

public sealed record UpdateCheckResult(UpdateStatus Status, string Message, UpdateInfo? Update = null);

/// <summary>
/// Checks the private GitHub Releases repository (or an optional zero-touch relay),
/// downloads the new single-file launcher, verifies its detached signature and
/// launches a detached helper with rollback if the new build does not become healthy.
/// </summary>
public sealed class UpdateService : IDisposable
{
    private const string ApiBase = "https://api.github.com";
    private const string GitHubApiAccept = "application/vnd.github+json";
    private const string GitHubAssetAccept = "application/octet-stream";
    private const int MaxSignatureTextBytes = 16 * 1024;

    private readonly HttpClient _github;
    private readonly HttpClient _proxy;
    private UpdateCheckResult? _cached;
    private DateTime _cachedAt;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);

    public UpdateService()
    {
        var handler = new SocketsHttpHandler
        {
            // Redirects are followed manually: the bearer token must only go to GitHub,
            // never to the signed storage URL that GitHub redirects to.
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false,
            MaxConnectionsPerServer = 8,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(20)
        };

        _github = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(15)
        };
        _github.DefaultRequestHeaders.UserAgent.ParseAdd("nVielLauncher/" + AppInfo.VersionLabel);

        _proxy = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(20)
        })
        {
            Timeout = TimeSpan.FromMinutes(2)
        };
        _proxy.DefaultRequestHeaders.UserAgent.ParseAdd("nVielLauncher/" + AppInfo.VersionLabel);
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct, bool force = false)
    {
        if (!force && _cached is not null && DateTime.UtcNow - _cachedAt < CacheLifetime)
            return _cached;

        var result = await CheckCoreAsync(ct);
        _cached = result;
        _cachedAt = DateTime.UtcNow;
        return result;
    }

    private async Task<UpdateCheckResult> CheckCoreAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(AppInfo.UpdateProxyUrl))
            return await CheckProxyAsync(ct);

        var source = await GetSourceAsync(ct);
        var releasesUrl = AppInfo.UpdateChannel == "beta"
            ? $"{ApiBase}/repos/{source.Owner}/{source.Repository}/releases?per_page=30"
            : $"{ApiBase}/repos/{source.Owner}/{source.Repository}/releases/latest";

        GitHubRelease? release;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, releasesUrl);
            request.Headers.Accept.ParseAdd(GitHubApiAccept);
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            if (!string.IsNullOrEmpty(source.Token))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", source.Token);

            using var response = await _github.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new UpdateCheckResult(UpdateStatus.NoReleases,
                    string.IsNullOrEmpty(source.Token)
                        ? "Нет доступа к репозиторию обновлений: не настроен токен или relay."
                        : "Релизов нет или нет доступа к репозиторию. Проверь токен и право Contents: Read.");
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return new UpdateCheckResult(UpdateStatus.Error, "Токен GitHub недействителен или истёк.");

            if (response.StatusCode == HttpStatusCode.Forbidden)
                return new UpdateCheckResult(UpdateStatus.Error,
                    "GitHub ограничил доступ. Проверь токен, права и лимит запросов.");

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                return new UpdateCheckResult(UpdateStatus.Error,
                    "GitHub временно ограничил проверку. Попробуй позже.");

            if (!response.IsSuccessStatusCode)
                return new UpdateCheckResult(UpdateStatus.Error,
                    $"GitHub вернул ошибку {(int)response.StatusCode}.");

            var body = await response.Content.ReadAsStringAsync(ct);

            if (AppInfo.UpdateChannel == "beta")
            {
                var list = JsonSerializer.Deserialize<List<GitHubRelease>>(body, HttpService.JsonOptions)
                           ?? new List<GitHubRelease>();
                release = list.FirstOrDefault(r => !r.Draft && r.Prerelease)
                          ?? list.FirstOrDefault(r => !r.Draft);
            }
            else
            {
                release = JsonSerializer.Deserialize<GitHubRelease>(body, HttpService.JsonOptions);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Write("UPDATE: проверка не удалась: " + ex.Message);
            return new UpdateCheckResult(UpdateStatus.Error, "Не удалось проверить обновления.");
        }

        if (release is null)
            return new UpdateCheckResult(UpdateStatus.Error, "GitHub вернул пустой ответ.");

        var parsedVersion = VersionUtil.TryParse(release.TagName);
        if (parsedVersion is null)
            return new UpdateCheckResult(UpdateStatus.Error, "Не удалось прочитать версию релиза.");

        var remoteVersion = VersionUtil.Normalize(parsedVersion);
        var current = VersionUtil.Normalize(AppInfo.Version);
        if (remoteVersion <= current)
            return new UpdateCheckResult(UpdateStatus.UpToDate, "Установлена актуальная версия.", null);

        var asset = release.Assets?
            .FirstOrDefault(a => string.Equals(a.Name, "nVielLauncher.exe", StringComparison.OrdinalIgnoreCase))
            ?? release.Assets?.FirstOrDefault(a =>
                a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                !a.Name.Contains("sha", StringComparison.OrdinalIgnoreCase) &&
                !a.Name.EndsWith(".sig", StringComparison.OrdinalIgnoreCase));

        if (asset is null ||
            (string.IsNullOrWhiteSpace(asset.ApiUrl) && string.IsNullOrWhiteSpace(asset.DownloadUrl)))
        {
            return new UpdateCheckResult(UpdateStatus.NoReleases,
                $"В релизе {release.TagName} нет файла nVielLauncher.exe.");
        }

        var signatureAsset = release.Assets?.FirstOrDefault(a =>
            string.Equals(a.Name, "nVielLauncher.exe.sig", StringComparison.OrdinalIgnoreCase));

        if (signatureAsset is null)
        {
            return new UpdateCheckResult(UpdateStatus.Error,
                "Релиз не подписан. Обновление отклонено — выпусти файл nVielLauncher.exe.sig.");
        }

        var displayVersion = VersionUtil.Format(remoteVersion);
        var info = new UpdateInfo(
            displayVersion,
            release.TagName,
            string.IsNullOrWhiteSpace(release.Name)
                ? AppInfo.ProductName + " " + displayVersion + " " + AppInfo.Edition
                : release.Name,
            release.Body ?? "",
            asset.DownloadUrl,
            asset.Size,
            ParseSha256(asset.Digest),
            release.HtmlUrl,
            asset.ApiUrl,
            signatureAsset.DownloadUrl,
            signatureAsset.ApiUrl);

        return new UpdateCheckResult(UpdateStatus.Available,
            $"Доступно обновление {info.Version}.", info);
    }

    /// <summary>
    /// The relay protocol is intentionally tiny and needs no GitHub credential:
    /// GET {relay}/latest?channel=stable returns a signed manifest.
    /// </summary>
    private async Task<UpdateCheckResult> CheckProxyAsync(CancellationToken ct)
    {
        try
        {
            var url = $"{AppInfo.UpdateProxyUrl}/latest?channel={Uri.EscapeDataString(AppInfo.UpdateChannel)}";
            using var response = await _proxy.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return new UpdateCheckResult(UpdateStatus.NoReleases, "Relay не нашёл релиз.");

            if (!response.IsSuccessStatusCode)
                return new UpdateCheckResult(UpdateStatus.Error,
                    $"Relay обновлений вернул ошибку {(int)response.StatusCode}.");

            var body = await response.Content.ReadAsStringAsync(ct);
            var manifest = JsonSerializer.Deserialize<ProxyManifest>(body, HttpService.JsonOptions);
            if (manifest is null)
                return new UpdateCheckResult(UpdateStatus.Error, "Relay вернул пустой манифест.");

            var parsed = VersionUtil.TryParse(manifest.Version);
            if (parsed is null)
                return new UpdateCheckResult(UpdateStatus.Error, "Relay вернул неизвестную версию.");

            var remote = VersionUtil.Normalize(parsed);
            if (remote <= VersionUtil.Normalize(AppInfo.Version))
                return new UpdateCheckResult(UpdateStatus.UpToDate, "Установлена актуальная версия.", null);

            var downloadUrl = ResolveUrl(manifest.DownloadUrl);
            var signatureUrl = ResolveUrl(manifest.SignatureUrl);
            if (!IsSafeReleaseUrl(downloadUrl) || !IsSafeReleaseUrl(signatureUrl))
                return new UpdateCheckResult(UpdateStatus.Error, "Relay вернул небезопасную ссылку.");

            var version = VersionUtil.Format(remote);
            var info = new UpdateInfo(
                version,
                manifest.Tag,
                string.IsNullOrWhiteSpace(manifest.Name)
                    ? AppInfo.ProductName + " " + version + " " + AppInfo.Edition
                    : manifest.Name,
                manifest.Notes ?? "",
                downloadUrl,
                manifest.Size,
                ParseSha256(manifest.Sha256),
                manifest.ReleaseUrl ?? "",
                "",
                signatureUrl,
                "");

            return new UpdateCheckResult(UpdateStatus.Available,
                $"Доступно обновление {info.Version}.", info);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Write("UPDATE: relay недоступен: " + ex.Message);
            return new UpdateCheckResult(UpdateStatus.Error, "Не удалось проверить обновления через relay.");
        }
    }

    /// <summary>
    /// Downloads the release asset and performs all checks before it is allowed to replace
    /// the running executable: signature, size, PE header, product name, version and SHA-256.
    /// </summary>
    public async Task<string> DownloadAsync(
        UpdateInfo info,
        IProgress<double>? progress,
        CancellationToken ct,
        IProgress<string>? stage = null)
    {
        Directory.CreateDirectory(AppPaths.UpdateDirectory);

        var destination = Path.Combine(AppPaths.UpdateDirectory,
            $"nVielLauncher-{SafeFilePart(info.Version)}.exe");

        CleanupOldDownloads(destination);

        progress?.Report(0);
        stage?.Report("1 / 4 · Проверка подписи релиза");

        var token = string.IsNullOrWhiteSpace(AppInfo.UpdateProxyUrl)
            ? (await GetSourceAsync(ct)).Token
            : "";
        var signature = await DownloadSignatureAsync(info, token, ct);

        if (File.Exists(destination) && new FileInfo(destination).Length == info.Size &&
            await TryVerifyAsync(destination, info, signature, ct))
        {
            progress?.Report(1d);
            return destination;
        }

        TryDelete(destination);
        stage?.Report("2 / 4 · Скачивание обновления");

        var url = !string.IsNullOrWhiteSpace(info.ApiUrl) ? info.ApiUrl : info.DownloadUrl;
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException("В релизе не указана ссылка на файл обновления.");

        await DownloadAssetAsync(url, destination, info, token, progress, ct);

        progress?.Report(1d);
        stage?.Report("3 / 4 · Подпись, размер, PE-заголовок, версия и SHA-256");

        try
        {
            await VerifyAsync(destination, info, signature, ct);
        }
        catch
        {
            TryDelete(destination);
            TryDelete(destination + ".part");
            throw;
        }

        stage?.Report("4 / 4 · Установка и перезапуск");
        return destination;
    }

    private async Task<string> DownloadSignatureAsync(UpdateInfo info, string token, CancellationToken ct)
    {
        var url = !string.IsNullOrWhiteSpace(info.SignatureApiUrl)
            ? info.SignatureApiUrl
            : info.SignatureUrl;

        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException("У релиза отсутствует цифровая подпись.");

        var text = await DownloadTextAsync(url, token, MaxSignatureTextBytes, ct);
        text = text.Trim();
        if (text.Length == 0)
            throw new InvalidOperationException("Файл подписи релиза пуст.");

        try
        {
            var bytes = Convert.FromBase64String(text);
            if (bytes.Length is <= 0 or > UpdateSignature.MaxSignatureBytes)
                throw new FormatException();

            return text;
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("Файл подписи релиза повреждён.");
        }
    }

    private async Task<string> DownloadTextAsync(
        string url,
        string token,
        int maxBytes,
        CancellationToken ct)
    {
        var current = url;

        try
        {
            for (var redirect = 0; redirect < 8; redirect++)
            {
                ct.ThrowIfCancellationRequested();

                var uri = new Uri(current, UriKind.Absolute);
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                if (IsGitHubAuthHost(uri.Host) && !string.IsNullOrEmpty(token))
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                request.Headers.Accept.ParseAdd(
                    uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase)
                        ? GitHubAssetAccept
                        : "*/*");

                using var response = await _github.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

                if (IsRedirect(response.StatusCode))
                {
                    var location = response.Headers.Location;
                    if (location is null) throw new InvalidOperationException("Пустое перенаправление.");
                    current = location.IsAbsoluteUri ? location.ToString() : new Uri(uri, location).ToString();
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException($"Не удалось скачать подпись: HTTP {(int)response.StatusCode}.");

                if (response.Content.Headers.ContentLength is long contentLength && contentLength > maxBytes)
                    throw new InvalidOperationException("Файл подписи слишком большой.");

                var bytes = await response.Content.ReadAsByteArrayAsync(ct);
                if (bytes.Length > maxBytes)
                    throw new InvalidOperationException("Файл подписи слишком большой.");

                return Encoding.UTF8.GetString(bytes);
            }

            throw new InvalidOperationException("Слишком много перенаправлений при скачивании подписи.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException("Не удалось скачать подпись релиза: " + ex.Message, ex);
        }
    }

    /// <summary>
    /// Downloads an asset with resumable .part files and without forwarding the bearer token
    /// to the signed storage host.
    /// </summary>
    private async Task DownloadAssetAsync(
        string url,
        string destination,
        UpdateInfo info,
        string token,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        var temp = destination + ".part";
        var existing = File.Exists(temp) ? new FileInfo(temp).Length : 0;
        if (existing > info.Size && info.Size > 0)
        {
            TryDelete(temp);
            existing = 0;
        }

        if (info.Size > 0 && existing == info.Size)
        {
            File.Move(temp, destination, overwrite: true);
            progress?.Report(1d);
            return;
        }

        var current = url;
        var restarted = false;

        try
        {
            for (var redirect = 0; redirect < 8; redirect++)
            {
                ct.ThrowIfCancellationRequested();

                var uri = new Uri(current, UriKind.Absolute);
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);

                if (IsGitHubAuthHost(uri.Host) && !string.IsNullOrEmpty(token))
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                request.Headers.Accept.ParseAdd(
                    uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase)
                        ? GitHubAssetAccept
                        : "*/*");

                if (existing > 0)
                    request.Headers.Range = new RangeHeaderValue(existing, null);

                using var response = await _github.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

                if (IsRedirect(response.StatusCode))
                {
                    var location = response.Headers.Location;
                    if (location is null)
                        throw new InvalidOperationException("GitHub вернул перенаправление без адреса.");

                    current = location.IsAbsoluteUri
                        ? location.ToString()
                        : new Uri(uri, location).ToString();
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && existing > 0 && !restarted)
                {
                    TryDelete(temp);
                    existing = 0;
                    restarted = true;
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.Unauthorized ||
                    response.StatusCode == HttpStatusCode.Forbidden)
                {
                    throw new InvalidOperationException(
                        "Нет доступа к файлу обновления. Проверь relay или токен GitHub.");
                }

                if (response.StatusCode == HttpStatusCode.NotFound)
                    throw new InvalidOperationException("Файл обновления не найден или нет доступа.");

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    throw new InvalidOperationException("GitHub временно ограничил скачивание. Попробуй позже.");

                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException($"Сервер вернул ошибку {(int)response.StatusCode}.");

                var mediaType = response.Content.Headers.ContentType?.MediaType;
                if (string.Equals(mediaType, "text/html", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Сервер вернул страницу вместо файла.");

                var resumed = response.StatusCode == HttpStatusCode.PartialContent && existing > 0;
                if (!resumed && existing > 0)
                {
                    existing = 0;
                }

                var contentLength = response.Content.Headers.ContentLength ?? 0;
                var total = response.Content.Headers.ContentRange?.Length
                            ?? (contentLength > 0 ? existing + contentLength : info.Size);

                await using (var source = await response.Content.ReadAsStreamAsync(ct))
                await using (var target = new FileStream(temp,
                                 resumed ? FileMode.Append : FileMode.Create,
                                 FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    var buffer = new byte[81920];
                    long read = existing;
                    int count;

                    while ((count = await source.ReadAsync(buffer, ct)) > 0)
                    {
                        await target.WriteAsync(buffer.AsMemory(0, count), ct);
                        read += count;

                        if (total > 0)
                            progress?.Report(Math.Clamp((double)read / total, 0, 1));
                    }
                }

                if (info.Size > 0 && new FileInfo(temp).Length != info.Size)
                    throw new IOException("Размер скачанного файла не совпадает с релизом.");

                File.Move(temp, destination, overwrite: true);
                progress?.Report(1d);
                return;
            }

            throw new InvalidOperationException("Слишком много перенаправлений при скачивании обновления.");
        }
        catch (OperationCanceledException)
        {
            // Keep the .part file: the next attempt resumes with an HTTP Range request.
            throw;
        }
        catch (Exception ex)
        {
            Log.Write("UPDATE: скачивание прервано, .part сохранён для продолжения: " + ex.Message);
            throw;
        }
    }

    /// <summary>Launches a detached helper with health check and automatic rollback.</summary>
    public void ApplyAndRestart(string downloadedPath)
    {
        var target = AppInfo.ExecutablePath;

        if (!File.Exists(downloadedPath))
            throw new FileNotFoundException("Скачанный файл обновления не найден.", downloadedPath);

        var targetName = Path.GetFileName(target);
        if (targetName.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase) ||
            targetName.Equals("testhost.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Обновление недоступно при запуске через dotnet в режиме разработки.");
        }

        Directory.CreateDirectory(AppPaths.UpdateDirectory);
        Directory.CreateDirectory(AppPaths.LogsDirectory);

        var marker = Path.Combine(AppPaths.UpdateDirectory, "health-" + Guid.NewGuid().ToString("N") + ".ok");
        var log = Path.Combine(AppPaths.LogsDirectory, "update.log");
        // A portable session started only with --portable must pass that flag to the new build,
        // otherwise it would use the normal %LOCALAPPDATA% data root and never write the marker.
        var portableArgument = AppPaths.IsPortable ? " --portable" : "";

        var script = $@"
$ErrorActionPreference = 'SilentlyContinue'
$oldPid = {Environment.ProcessId}
$src = '{PowerShellQuote(downloadedPath)}'
$dst = '{PowerShellQuote(target)}'
$backup = $dst + '.old'
$marker = '{PowerShellQuote(marker)}'
$log = '{PowerShellQuote(log)}'

function Write-UpdateLog([string]$message) {{
    Add-Content -LiteralPath $log -Value ((Get-Date -Format o) + ' ' + $message)
}}

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $log) | Out-Null
Write-UpdateLog 'Waiting for previous launcher process...'
for ($i = 0; $i -lt 240; $i++) {{
    if (-not (Get-Process -Id $oldPid -ErrorAction SilentlyContinue)) {{ break }}
    Start-Sleep -Milliseconds 250
}}
Start-Sleep -Milliseconds 500

Remove-Item -LiteralPath $marker -Force
Remove-Item -LiteralPath $backup -Force
$copied = $false
$movedOld = $false

try {{
    if (Test-Path -LiteralPath $dst) {{
        Move-Item -LiteralPath $dst -Destination $backup -Force
        $movedOld = $true
    }}

    for ($i = 0; $i -lt 80; $i++) {{
        try {{
            Copy-Item -LiteralPath $src -Destination $dst -Force -ErrorAction Stop
            $copied = $true
            break
        }} catch {{
            Start-Sleep -Milliseconds 250
        }}
    }}

    if (-not $copied) {{ throw 'Could not replace the launcher executable.' }}

    $dir = [System.IO.Path]::GetDirectoryName($dst)
    $arguments = '--update-health ""' + $marker + '""' + '{portableArgument}'
    $newProcess = Start-Process -FilePath $dst -WorkingDirectory $dir -ArgumentList $arguments -PassThru
    Write-UpdateLog 'New build started; waiting for health marker...'

    $healthy = $false
    for ($i = 0; $i -lt 180; $i++) {{
        if (Test-Path -LiteralPath $marker) {{ $healthy = $true; break }}
        if ($newProcess -and $newProcess.HasExited) {{ break }}
        Start-Sleep -Milliseconds 500
    }}

    if ($healthy) {{
        Remove-Item -LiteralPath $backup -Force
        Remove-Item -LiteralPath $marker -Force
        Remove-Item -LiteralPath $src -Force
        Write-UpdateLog 'Update healthy; rollback copy removed.'
    }} else {{
        if ($newProcess -and -not $newProcess.HasExited) {{
            Stop-Process -Id $newProcess.Id -Force
        }}
        if ($movedOld -and (Test-Path -LiteralPath $backup)) {{
            Remove-Item -LiteralPath $dst -Force
            Move-Item -LiteralPath $backup -Destination $dst -Force
        }}
        Write-UpdateLog 'Health check failed; previous build restored.'
        Start-Process -FilePath $dst -WorkingDirectory $dir
        try {{
            Add-Type -AssemblyName PresentationFramework
            [System.Windows.MessageBox]::Show('Новая версия не запустилась. Восстановлена предыдущая версия.', 'nViel Launcher', 'OK', 'Warning') | Out-Null
        }} catch {{ }}
    }}
}} catch {{
    Write-UpdateLog ('Update failed: ' + $_.Exception.Message)
    if ($movedOld -and (Test-Path -LiteralPath $backup)) {{
        Remove-Item -LiteralPath $dst -Force
        Move-Item -LiteralPath $backup -Destination $dst -Force
    }}
    if (-not (Test-Path -LiteralPath $dst)) {{
        Copy-Item -LiteralPath $src -Destination $dst -Force
    }}
    Start-Process -FilePath $dst -WorkingDirectory ([System.IO.Path]::GetDirectoryName($dst))
    try {{
        Add-Type -AssemblyName PresentationFramework
        [System.Windows.MessageBox]::Show('Не удалось установить обновление. Запущена предыдущая версия.', 'nViel Launcher', 'OK', 'Warning') | Out-Null
    }} catch {{ }}
}}
";

        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

        Process.Start(new ProcessStartInfo("powershell.exe",
            "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand " + encoded)
        {
            UseShellExecute = false,
            CreateNoWindow = true
        });

        Log.Write($"UPDATE: установка {downloadedPath} → {target}; health={marker}");
        Application.Current?.Shutdown();
    }

    // ------------------------------------------------------------------ verification

    private static async Task<bool> TryVerifyAsync(
        string path,
        UpdateInfo info,
        string signature,
        CancellationToken ct)
    {
        try
        {
            await VerifyAsync(path, info, signature, ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static async Task VerifyAsync(
        string path,
        UpdateInfo info,
        string signature,
        CancellationToken ct)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length <= 0)
            throw new InvalidOperationException("Скачанный файл пуст.");

        if (info.Size > 0 && file.Length != info.Size)
            throw new InvalidOperationException("Размер файла не совпадает с релизом.");

        await using (var stream = File.OpenRead(path))
        {
            var header = new byte[64];

            try
            {
                await stream.ReadExactlyAsync(header, ct);
            }
            catch (EndOfStreamException)
            {
                throw new InvalidOperationException("Файл повреждён или слишком мал.");
            }

            if (header[0] != (byte)'M' || header[1] != (byte)'Z')
                throw new InvalidOperationException("Файл не является исполняемым файлом Windows.");

            var peOffset = BitConverter.ToInt32(header, 0x3C);
            if (peOffset <= 0 || peOffset > 0x100000)
                throw new InvalidOperationException("Повреждён заголовок исполняемого файла.");

            stream.Position = peOffset;
            var peSignature = new byte[4];

            try
            {
                await stream.ReadExactlyAsync(peSignature, ct);
            }
            catch (EndOfStreamException)
            {
                throw new InvalidOperationException("Файл повреждён или слишком мал.");
            }

            if (peSignature[0] != (byte)'P' || peSignature[1] != (byte)'E' ||
                peSignature[2] != 0 || peSignature[3] != 0)
                throw new InvalidOperationException("Файл не является приложением Windows.");
        }

        var versionInfo = FileVersionInfo.GetVersionInfo(path);
        var product = versionInfo.ProductName ?? "";
        if (!product.Contains("nViel", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Файл принадлежит другому приложению.");

        var fileVersion = VersionUtil.TryParse(versionInfo.FileVersion);
        var expected = VersionUtil.TryParse(info.Version);
        if (fileVersion is null || expected is null ||
            VersionUtil.Normalize(fileVersion) < VersionUtil.Normalize(expected))
            throw new InvalidOperationException("Версия скачанного файла не совпадает с релизом.");

        if (!string.IsNullOrWhiteSpace(info.Sha256))
        {
            await using var stream = File.OpenRead(path);
            var hash = await SHA256.HashDataAsync(stream, ct);
            var actual = Convert.ToHexString(hash);

            if (!actual.Equals(info.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Контрольная сумма файла не совпадает.");
        }

        if (!await UpdateSignature.VerifyFileAsync(path, signature, ct))
            throw new InvalidOperationException("Цифровая подпись обновления недействительна.");
    }

    // ------------------------------------------------------------------ helpers

    private static async Task<(string Owner, string Repository, string Token)> GetSourceAsync(
        CancellationToken ct)
    {
        var token = await GitHubTokenProvider.ResolveAsync(ct);
        return (AppInfo.GitHubOwner, AppInfo.ReleasesRepository, token);
    }

    private static bool IsRedirect(HttpStatusCode code) =>
        code is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Found
            or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    private static bool IsGitHubAuthHost(string host) =>
        host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("www.github.com", StringComparison.OrdinalIgnoreCase);

    private static string? ParseSha256(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest)) return null;

        const string prefix = "sha256:";
        return digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? digest[prefix.Length..].Trim()
            : digest.Trim();
    }

    private static string SafeFilePart(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    private static string ResolveUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute)) return absolute.ToString();
        var baseUrl = AppInfo.UpdateProxyUrl.TrimEnd('/') + "/";
        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) &&
            Uri.TryCreate(baseUri, value, out var relative))
            return relative.ToString();
        return "";
    }

    private static bool IsSafeReleaseUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme == Uri.UriSchemeHttps) return true;
        return uri.Scheme == Uri.UriSchemeHttp && (uri.IsLoopback || uri.Host == "localhost");
    }

    private static void CleanupOldDownloads(string keep)
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-7);
            foreach (var path in Directory.EnumerateFiles(AppPaths.UpdateDirectory, "*", SearchOption.TopDirectoryOnly))
            {
                if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(keep), StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Path.GetFullPath(path), Path.GetFullPath(keep + ".part"), StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    if (File.GetLastWriteTimeUtc(path) < cutoff) File.Delete(path);
                }
                catch
                {
                    // a locked stale file is harmless
                }
            }
        }
        catch
        {
            // optional cleanup
        }
    }

    private static string PowerShellQuote(string value) => value.Replace("'", "''");

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // ignore — a fresh name is used for every update attempt
        }
    }

    public void Dispose()
    {
        _github.Dispose();
        _proxy.Dispose();
    }

    // ------------------------------------------------------------------ GitHub / relay models

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string TagName { get; set; } = "";
        public string Name { get; set; } = "";
        public string Body { get; set; } = "";
        [JsonPropertyName("html_url")] public string HtmlUrl { get; set; } = "";
        public bool Draft { get; set; }
        public bool Prerelease { get; set; }
        public List<GitHubAsset>? Assets { get; set; }
    }

    private sealed class GitHubAsset
    {
        public string Name { get; set; } = "";

        [JsonPropertyName("url")]
        public string ApiUrl { get; set; } = "";

        [JsonPropertyName("browser_download_url")]
        public string DownloadUrl { get; set; } = "";

        public long Size { get; set; }
        public string? Digest { get; set; }
    }

    private sealed class ProxyManifest
    {
        public string Version { get; set; } = "";
        public string Tag { get; set; } = "";
        public string? Name { get; set; }
        public string? Notes { get; set; }
        [JsonPropertyName("download_url")] public string DownloadUrl { get; set; } = "";
        [JsonPropertyName("signature_url")] public string SignatureUrl { get; set; } = "";
        public long Size { get; set; }
        public string? Sha256 { get; set; }
        [JsonPropertyName("release_url")] public string? ReleaseUrl { get; set; }
    }
}
