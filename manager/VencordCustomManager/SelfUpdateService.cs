using System.Diagnostics;
using System.Text;
using System.Windows;

namespace VencordCustomManager;

public sealed class SelfUpdateService
{
    private readonly UpdateClient _updateClient;

    public SelfUpdateService(UpdateClient updateClient)
    {
        _updateClient = updateClient;
    }

    public static bool IsUpdateAvailable(UpdateManifest? manifest)
    {
        if (manifest?.Manager is null || string.IsNullOrWhiteSpace(manifest.Manager.Version)) return false;
        return UpdateClient.CompareVersions(AppInfo.CurrentVersion, manifest.Manager.Version) < 0;
    }

    public async Task DownloadAndRestartAsync(
        UpdateManifest manifest,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var manager = manifest.Manager ?? throw new InvalidOperationException("The update manifest does not contain a manager release.");
        if (!IsUpdateAvailable(manifest)) return;

        var currentExe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentExe) || !File.Exists(currentExe))
            throw new InvalidOperationException("Could not determine the running manager executable path.");

        var updateDir = Path.Combine(ManagerPaths.CacheDirectory, "manager-update");
        Directory.CreateDirectory(updateDir);
        var downloadedExe = Path.Combine(updateDir, $"VencordCustomManager-{manager.Version}.exe");

        progress?.Report(new OperationProgress($"Downloading manager v{manager.Version}…", 0));
        await _updateClient.DownloadFileAsync(manager.Asset.Url, downloadedExe, progress, cancellationToken);

        progress?.Report(new OperationProgress("Verifying manager SHA-256…", 100));
        var actualHash = await UpdateClient.ComputeSha256Async(downloadedExe, cancellationToken);
        if (!actualHash.Equals(manager.Asset.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Manager integrity check failed. Expected {manager.Asset.Sha256}, got {actualHash}.");

        // The helper waits for this process to exit, replaces the EXE, starts the new one,
        // and deletes the downloaded temporary copy. EncodedCommand avoids path quoting issues.
        static string PsQuote(string value) => "'" + value.Replace("'", "''") + "'";
        var command = string.Join("; ",
            $"Wait-Process -Id {Environment.ProcessId} -ErrorAction SilentlyContinue",
            "Start-Sleep -Milliseconds 350",
            $"Copy-Item -LiteralPath {PsQuote(downloadedExe)} -Destination {PsQuote(currentExe)} -Force",
            $"Start-Process -FilePath {PsQuote(currentExe)}",
            $"Remove-Item -LiteralPath {PsQuote(downloadedExe)} -Force -ErrorAction SilentlyContinue");
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));

        var helper = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand {encoded}",
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Could not start the manager update helper.");
        helper.Dispose();

        progress?.Report(new OperationProgress("Restarting into the new manager version…", 100));
        await Task.Delay(250, cancellationToken);
        Application.Current.Shutdown();
    }
}
