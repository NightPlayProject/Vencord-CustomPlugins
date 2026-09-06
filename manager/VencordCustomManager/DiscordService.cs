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
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var restartTargets = new List<DiscordRestartTarget>();
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        foreach (var variant in Variants)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var processes = Process.GetProcessesByName(variant.Process);
            if (processes.Length == 0) continue;

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
}
