using System.Diagnostics;

namespace VencordCustomManager;

public sealed class VencordInstallerService
{
    private const string InstallerUrl = "https://github.com/Vencord/Installer/releases/latest/download/VencordInstallerCli.exe";
    private readonly UpdateClient _updateClient;

    public VencordInstallerService(UpdateClient updateClient)
    {
        _updateClient = updateClient;
    }

    public async Task RunAsync(
        string action,
        string branch,
        string vencordDirectory,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (action is not ("install" or "repair" or "uninstall"))
            throw new ArgumentOutOfRangeException(nameof(action));

        branch = NormalizeBranch(branch);
        var cli = Path.Combine(ManagerPaths.CacheDirectory, "VencordInstallerCli.exe");
        progress?.Report(new OperationProgress("Downloading the official Vencord installer…"));
        await _updateClient.DownloadFileAsync(InstallerUrl, cli, null, cancellationToken);

        progress?.Report(new OperationProgress($"Running Vencord {action}…"));
        var startInfo = new ProcessStartInfo
        {
            FileName = cli,
            Arguments = $"--{action} -branch {branch}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = vencordDirectory
        };
        startInfo.Environment["VENCORD_USER_DATA_DIR"] = vencordDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        startInfo.Environment["VENCORD_DEV_INSTALL"] = "1";

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not launch VencordInstallerCli.exe.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            var details = string.Join(Environment.NewLine, new[] { stdout, stderr }.Where(x => !string.IsNullOrWhiteSpace(x)));
            throw new InvalidOperationException($"Vencord installer exited with code {process.ExitCode}.{Environment.NewLine}{details}".Trim());
        }
    }

    private static string NormalizeBranch(string branch) => branch.ToLowerInvariant() switch
    {
        "stable" => "stable",
        "ptb" => "ptb",
        "canary" => "canary",
        _ => "auto"
    };
}
