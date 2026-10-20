using System.Diagnostics;
using System.Text;

namespace nVielLauncher.Services;

/// <summary>
/// Resolves a GitHub token without embedding it in the executable:
/// saved DPAPI token → environment variables → Git Credential Manager.
/// The token is kept in memory only and is never written to the log.
/// </summary>
public static class GitHubTokenProvider
{
    private static readonly string[] EnvironmentNames =
    {
        "NVIEL_UPDATE_TOKEN",
        "GITHUB_TOKEN",
        "GH_TOKEN"
    };

    /// <summary>"settings", "environment", "git" or "none".</summary>
    public static string LastSource { get; private set; } = "none";

    public static bool HasEnvironmentToken =>
        EnvironmentNames.Any(name => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)));

    public static async Task<string> ResolveAsync(string? protectedToken, CancellationToken ct)
    {
        var saved = SecretProtector.Unprotect(protectedToken);
        if (!string.IsNullOrWhiteSpace(saved))
        {
            LastSource = "settings";
            return saved;
        }

        foreach (var name in EnvironmentNames)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                LastSource = "environment";
                return value.Trim();
            }
        }

        var git = await TryGitCredentialAsync(ct);
        LastSource = string.IsNullOrWhiteSpace(git) ? "none" : "git";
        return git;
    }

    private static async Task<string> TryGitCredentialAsync(CancellationToken ct)
    {
        try
        {
            var start = new ProcessStartInfo("git")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            start.ArgumentList.Add("credential");
            start.ArgumentList.Add("fill");
            start.Environment["GIT_TERMINAL_PROMPT"] = "0";
            start.Environment["GCM_INTERACTIVE"] = "never";
            start.Environment["GCM_GUI_PROMPT"] = "0";

            using var process = Process.Start(start);
            if (process is null) return "";

            process.StandardInput.Write("protocol=https\nhost=github.com\n\n");
            process.StandardInput.Close();

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));

            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }

                if (ct.IsCancellationRequested) throw;
                return "";
            }

            var output = await outputTask;
            await errorTask; // Drain stderr, never log or display it.

            foreach (var line in output.Split('\n'))
            {
                var trimmed = line.TrimEnd('\r');
                if (trimmed.StartsWith("password=", StringComparison.Ordinal))
                    return trimmed["password=".Length..].Trim();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Git is not installed, GCM is unavailable, or there is no credential.
        }

        return "";
    }
}
