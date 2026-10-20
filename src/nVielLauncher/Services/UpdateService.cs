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
    string ApiUrl = "");

public sealed record UpdateCheckResult(UpdateStatus Status, string Message, UpdateInfo? Update = null);

/// <summary>
/// Checks the private GitHub Releases repository, downloads the new single-file launcher,
/// verifies it and replaces the running exe through a small detached PowerShell helper.
/// </summary>
public sealed class UpdateService : IDisposable
{
    private const string ApiBase = "https://api.github.com";
    private const string GitHubApiAccept = "application/vnd.github+json";
    private const string GitHubAssetAccept = "application/octet-stream";

    private readonly HttpClient _github;
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
        var source = await GetSourceAsync(ct);
        var url = $"{ApiBase}/repos/{source.Owner}/{source.Repository}/releases/latest";

        GitHubRelease? release;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd(GitHubApiAccept);
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            if (!string.IsNullOrEmpty(source.Token))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", source.Token);

            using var response = await _github.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new UpdateCheckResult(UpdateStatus.NoReleases,
                    "Релизов нет или нет доступа к репозиторию. Проверь имя репозитория, токен и право Contents: Read.");
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return new UpdateCheckResult(UpdateStatus.Error,
                    "Токен GitHub недействителен или истёк.");
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                return new UpdateCheckResult(UpdateStatus.Error,
                    "GitHub ограничил доступ. Проверь токен, права и лимит запросов.");
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return new UpdateCheckResult(UpdateStatus.Error,
                    "GitHub временно ограничил проверку. Попробуй позже.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return new UpdateCheckResult(UpdateStatus.Error,
                    $"GitHub вернул ошибку {(int)response.StatusCode}.");
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            release = JsonSerializer.Deserialize<GitHubRelease>(body, HttpService.JsonOptions);
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

        var parsedVersion = ParseVersion(release.TagName);
        if (parsedVersion is null)
            return new UpdateCheckResult(UpdateStatus.Error, "Не удалось прочитать версию релиза.");

        var remoteVersion = Normalize(parsedVersion);
        var current = Normalize(AppInfo.Version);
        if (remoteVersion <= current)
        {
            return new UpdateCheckResult(UpdateStatus.UpToDate,
                "Установлена актуальная версия.", null);
        }

        var asset = release.Assets?
            .FirstOrDefault(a => string.Equals(a.Name, "nVielLauncher.exe", StringComparison.OrdinalIgnoreCase))
            ?? release.Assets?.FirstOrDefault(a =>
                a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                !a.Name.Contains("sha", StringComparison.OrdinalIgnoreCase));

        if (asset is null ||
            (string.IsNullOrWhiteSpace(asset.ApiUrl) && string.IsNullOrWhiteSpace(asset.DownloadUrl)))
        {
            return new UpdateCheckResult(UpdateStatus.NoReleases,
                $"В релизе {release.TagName} нет файла nVielLauncher.exe.");
        }

        var info = new UpdateInfo(
            remoteVersion.ToString(3),
            release.TagName,
            string.IsNullOrWhiteSpace(release.Name) ? "nViel Launcher " + remoteVersion.ToString(3) : release.Name,
            release.Body ?? "",
            asset.DownloadUrl,
            asset.Size,
            ParseSha256(asset.Digest),
            release.HtmlUrl,
            asset.ApiUrl);

        return new UpdateCheckResult(UpdateStatus.Available,
            $"Доступно обновление {info.Version}.", info);
    }

    /// <summary>
    /// Downloads the release asset and performs all checks before it is allowed to replace
    /// the running executable: size, PE header, product name, file version and SHA-256.
    /// </summary>
    public async Task<string> DownloadAsync(
        UpdateInfo info,
        IProgress<double>? progress,
        CancellationToken ct,
        IProgress<string>? stage = null)
    {
        var directory = Path.Combine(AppPaths.AppRoot, "update");
        Directory.CreateDirectory(directory);

        var destination = Path.Combine(directory, $"nVielLauncher-{info.Version}.exe");
        TryDelete(destination);

        progress?.Report(0);
        stage?.Report("1 / 3 · Скачивание обновления");

        var source = await GetSourceAsync(ct);
        var url = !string.IsNullOrWhiteSpace(info.ApiUrl) ? info.ApiUrl : info.DownloadUrl;

        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException("В релизе не указана ссылка на файл обновления.");

        await DownloadAssetAsync(url, destination, info, source.Token, progress, ct);

        progress?.Report(1d);
        stage?.Report("2 / 3 · Проверка размера, PE-заголовка, версии и SHA-256");
        await VerifyAsync(destination, info, ct);
        return destination;
    }

    /// <summary>
    /// Downloads an asset without forwarding the bearer token to the signed storage host.
    /// GitHub's API asset URL is preferred because it works for private repositories.
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
        TryDelete(temp);
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
                    if (location is null)
                        throw new InvalidOperationException("GitHub вернул перенаправление без адреса.");

                    current = location.IsAbsoluteUri
                        ? location.ToString()
                        : new Uri(uri, location).ToString();
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.Unauthorized ||
                    response.StatusCode == HttpStatusCode.Forbidden)
                {
                    throw new InvalidOperationException(
                        "Нет доступа к файлу обновления. Проверь токен GitHub и права Contents: Read.");
                }

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    throw new InvalidOperationException(
                        "Файл обновления не найден или нет доступа к репозиторию.");
                }

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    throw new InvalidOperationException(
                        "GitHub временно ограничил скачивание. Попробуй позже.");
                }

                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException($"GitHub вернул ошибку {(int)response.StatusCode}.");

                var mediaType = response.Content.Headers.ContentType?.MediaType;
                if (string.Equals(mediaType, "text/html", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "GitHub вернул страницу вместо файла. Проверь токен и доступ к репозиторию.");
                }

                var total = response.Content.Headers.ContentLength ?? info.Size;

                await using (var source = await response.Content.ReadAsStreamAsync(ct))
                await using (var target = new FileStream(temp, FileMode.Create, FileAccess.Write,
                                 FileShare.None, 81920, useAsync: true))
                {
                    var buffer = new byte[81920];
                    long read = 0;
                    int count;

                    while ((count = await source.ReadAsync(buffer, ct)) > 0)
                    {
                        await target.WriteAsync(buffer.AsMemory(0, count), ct);
                        read += count;

                        if (total > 0)
                            progress?.Report(Math.Clamp((double)read / total, 0, 1));
                    }

                    await target.FlushAsync(ct);
                }

                File.Move(temp, destination, overwrite: true);
                progress?.Report(1d);
                return;
            }

            throw new InvalidOperationException("Слишком много перенаправлений при скачивании обновления.");
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>Launches a detached helper that waits for this process, swaps the exe and restarts it.</summary>
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

        var script = $@"
$ErrorActionPreference = 'SilentlyContinue'
$oldPid = {Environment.ProcessId}
for ($i = 0; $i -lt 240; $i++) {{
    if (-not (Get-Process -Id $oldPid -ErrorAction SilentlyContinue)) {{ break }}
    Start-Sleep -Milliseconds 250
}}
Start-Sleep -Milliseconds 600
$src = '{PowerShellQuote(downloadedPath)}'
$dst = '{PowerShellQuote(target)}'
$copied = $false
for ($i = 0; $i -lt 60; $i++) {{
    try {{
        Copy-Item -LiteralPath $src -Destination $dst -Force -ErrorAction Stop
        $copied = $true
        break
    }} catch {{
        Start-Sleep -Milliseconds 250
    }}
}}
Start-Sleep -Milliseconds 300
$dir = [System.IO.Path]::GetDirectoryName($dst)
Start-Process -FilePath $dst -WorkingDirectory $dir
Start-Sleep -Milliseconds 700
if ($copied) {{ Remove-Item -LiteralPath $src -Force }}
";

        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

        Process.Start(new ProcessStartInfo("powershell.exe",
            "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand " + encoded)
        {
            UseShellExecute = false,
            CreateNoWindow = true
        });

        Log.Write($"UPDATE: установка {downloadedPath} → {target}");
        Application.Current?.Shutdown();
    }

    // ------------------------------------------------------------------ verification

    public static async Task VerifyAsync(string path, UpdateInfo info, CancellationToken ct)
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
            var signature = new byte[4];

            try
            {
                await stream.ReadExactlyAsync(signature, ct);
            }
            catch (EndOfStreamException)
            {
                throw new InvalidOperationException("Файл повреждён или слишком мал.");
            }

            if (signature[0] != (byte)'P' || signature[1] != (byte)'E' ||
                signature[2] != 0 || signature[3] != 0)
                throw new InvalidOperationException("Файл не является приложением Windows.");
        }

        var versionInfo = FileVersionInfo.GetVersionInfo(path);
        var product = versionInfo.ProductName ?? "";
        if (!product.Contains("nViel", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Файл принадлежит другому приложению.");

        var fileVersion = ParseVersion(versionInfo.FileVersion);
        var expected = ParseVersion(info.Version);
        if (fileVersion is null || expected is null || Normalize(fileVersion) < Normalize(expected))
            throw new InvalidOperationException("Версия скачанного файла не совпадает с релизом.");

        if (!string.IsNullOrWhiteSpace(info.Sha256))
        {
            await using var stream = File.OpenRead(path);
            var hash = await SHA256.HashDataAsync(stream, ct);
            var actual = Convert.ToHexString(hash);

            if (!actual.Equals(info.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Контрольная сумма файла не совпадает.");
        }
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

    private static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        text = text.Trim();
        if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..];

        var dash = text.IndexOf('-');
        if (dash > 0) text = text[..dash];

        return Version.TryParse(text, out var version) ? version : null;
    }

    private static Version Normalize(Version version) =>
        new(version.Major, version.Minor, Math.Max(version.Build, 0), Math.Max(version.Revision, 0));

    private static string? ParseSha256(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest)) return null;

        const string prefix = "sha256:";
        return digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? digest[prefix.Length..].Trim()
            : digest.Trim();
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

    public void Dispose() => _github.Dispose();

    // ------------------------------------------------------------------ GitHub models

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string TagName { get; set; } = "";
        public string Name { get; set; } = "";
        public string Body { get; set; } = "";
        [JsonPropertyName("html_url")] public string HtmlUrl { get; set; } = "";
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
}
