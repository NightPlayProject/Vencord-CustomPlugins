using System.Diagnostics;

namespace VencordCustomManager;

public sealed class DiscordService
{
    private static readonly (string Process, string Folder, string Branch)[] Variants =
    [
        ("Discord", "Discord", "stable"),
        ("DiscordPTB", "DiscordPTB", "ptb"),
        ("DiscordCanary", "DiscordCanary", "canary"),
    ];

    public async Task<IReadOnlyList<DiscordRestartTarget>> StopRunningAsync(
        string branch,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        branch = NormalizeExactBranch(branch);
        var restartTargets = new List<DiscordRestartTarget>();
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var variant = Variants.First(x => x.Branch.Equals(branch, StringComparison.OrdinalIgnoreCase));
        cancellationToken.ThrowIfCancellationRequested();
        var processes = Process.GetProcessesByName(variant.Process);
        if (processes.Length == 0) return restartTargets;

        var updateExe = Path.Combine(localAppData, variant.Folder, "Update.exe");
        if (File.Exists(updateExe))
            restartTargets.Add(new DiscordRestartTarget(variant.Branch, updateExe, variant.Process + ".exe"));

        progress?.Report(new OperationProgress($"Closing {variant.Process}…"));
        foreach (var process in processes)
        {
            try
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            finally { process.Dispose(); }
        }

        var stillRunning = Process.GetProcessesByName(variant.Process);
        try
        {
            if (stillRunning.Length > 0)
                throw new InvalidOperationException($"Could not fully close {variant.Process}. Close it manually and try again.");
        }
        finally
        {
            foreach (var process in stillRunning) process.Dispose();
        }

        // Give Squirrel/Discord child processes a moment to release loaded files.
        await Task.Delay(600, cancellationToken);
        return restartTargets;
    }

    public void Restart(IEnumerable<DiscordRestartTarget> targets)
    {
        foreach (var target in targets.DistinctBy(x => x.UpdateExecutable, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = target.UpdateExecutable,
                    Arguments = $"--processStart {target.ProcessExecutable}",
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(target.UpdateExecutable) ?? string.Empty
                });
            }
            catch
            {
                // Restart is best-effort. Installation itself has already succeeded.
            }
        }
    }

    private static string NormalizeExactBranch(string branch) => branch.ToLowerInvariant() switch
    {
        "stable" => "stable",
        "ptb" => "ptb",
        "canary" => "canary",
        _ => throw new ArgumentOutOfRangeException(nameof(branch), "A specific Discord client is required.")
    };
}
