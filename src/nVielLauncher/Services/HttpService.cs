using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace nVielLauncher.Services;

public sealed class HttpService : IDisposable
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _client;

    public HttpService()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            MaxConnectionsPerServer = 32,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(20)
        };

        _client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(15)
        };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("nVielLauncher/" + AppInfo.VersionLabel);
        _client.DefaultRequestHeaders.Accept.ParseAdd("*/*");
    }

    public async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        using var response = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    public async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        var text = await GetStringAsync(url, ct);
        return JsonDocument.Parse(text);
    }

    public async Task<T?> GetJsonAsync<T>(string url, CancellationToken ct)
    {
        var text = await GetStringAsync(url, ct);
        return JsonSerializer.Deserialize<T>(text, JsonOptions);
    }

    /// <summary>Downloads a file with retries, SHA-1 validation and progress reporting.</summary>
    public async Task DownloadAsync(
        string url,
        string destination,
        long expectedSize,
        string? sha1,
        IProgress<double>? progress,
        CancellationToken ct,
        bool verifySha1OnExisting = true,
        int maxAttempts = 3)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        if (File.Exists(destination))
        {
            var info = new FileInfo(destination);
            if (expectedSize <= 0 || info.Length == expectedSize)
            {
                if (!verifySha1OnExisting || string.IsNullOrEmpty(sha1) || await HashMatchesAsync(destination, sha1, ct))
                {
                    progress?.Report(1d);
                    return;
                }
            }
        }

        var temp = destination + ".part";
        Exception? last = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (File.Exists(temp)) File.Delete(temp);

                using var response = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();

                var total = expectedSize > 0 ? expectedSize : response.Content.Headers.ContentLength ?? -1;

                await using (var source = await response.Content.ReadAsStreamAsync(ct))
                await using (var target = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 131072, useAsync: true))
                {
                    var buffer = new byte[131072];
                    long received = 0;
                    var lastReport = DateTime.UtcNow;

                    int read;
                    while ((read = await source.ReadAsync(buffer, ct)) > 0)
                    {
                        await target.WriteAsync(buffer.AsMemory(0, read), ct);
                        received += read;

                        if (progress is not null && (DateTime.UtcNow - lastReport).TotalMilliseconds > 120)
                        {
                            lastReport = DateTime.UtcNow;
                            progress.Report(total > 0 ? Math.Clamp((double)received / total, 0, 1) : -1);
                        }
                    }
                }

                if (!string.IsNullOrEmpty(sha1) && !await HashMatchesAsync(temp, sha1, ct))
                    throw new IOException("SHA-1 не совпадает: " + Path.GetFileName(destination));

                File.Move(temp, destination, overwrite: true);
                progress?.Report(1d);
                return;
            }
            catch (OperationCanceledException)
            {
                TryDelete(temp);
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
                TryDelete(temp);
                if (attempt < maxAttempts)
                    await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt), ct);
            }
        }

        throw new IOException($"Не удалось скачать {Path.GetFileName(destination)}: {last?.Message}", last);
    }

    private static async Task<bool> HashMatchesAsync(string path, string sha1, CancellationToken ct)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            var hash = await SHA1.HashDataAsync(stream, ct);
            return Convert.ToHexString(hash).Equals(sha1, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // ignored
        }
    }

    public void Dispose() => _client.Dispose();
}
