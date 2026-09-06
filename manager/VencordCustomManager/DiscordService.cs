using System.Diagnostics;

namespace VencordCustomManager;

public sealed class DiscordService
{
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan ClosePollInterval = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan CloseQuietPeriod = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan PerProcessExitWait = TimeSpan.FromSeconds(1);

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
        var displayName = DisplayBranch(variant.Branch);
        var updateExe = Path.Combine(localAppData, variant.Folder, "Update.exe");
        var restartTargetAdded = false;

        void RememberRestartTarget()
        {
            if (restartTargetAdded || !File.Exists(updateExe)) return;
            restartTargets.Add(new DiscordRestartTarget(variant.Branch, updateExe, variant.Process + ".exe"));
            restartTargetAdded = true;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var processes = GetLiveProcesses(variant.Process);
        if (processes.Count > 0)
        {
            RememberRestartTarget();
            progress?.Report(new OperationProgress($"Closing {displayName}…"));
        }
        foreach (var process in processes)
        {
            await TryStopProcessAsync(process, cancellationToken);
        }

        // Discord/Electron can leave short-lived process objects behind after the visible
        // window has already closed, and a child can briefly respawn while the original
        // process tree is winding down. A single immediate process-name check causes false
        // "close Discord manually" errors. Poll only the selected Discord client, discard
        // already-exited processes, and stop late-spawned instances before declaring failure.
        var deadline = DateTime.UtcNow + CloseTimeout;
        DateTime? quietSince = null;
        var waitingReported = false;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var liveProcesses = GetLiveProcesses(variant.Process);
            if (liveProcesses.Count == 0)
            {
                quietSince ??= DateTime.UtcNow;
                if (DateTime.UtcNow - quietSince.Value >= CloseQuietPeriod)
                {
                    if (restartTargetAdded || waitingReported)
                        progress?.Report(new OperationProgress($"{displayName} closed."));
                    return restartTargets;
                }

                await Task.Delay(ClosePollInterval, cancellationToken);
                continue;
            }

            quietSince = null;
            RememberRestartTarget();

            if (!waitingReported)
            {
                progress?.Report(new OperationProgress($"Waiting for {displayName} to finish closing…"));
                waitingReported = true;
            }

            foreach (var process in liveProcesses)
                await TryStopProcessAsync(process, cancellationToken);

            await Task.Delay(ClosePollInterval, cancellationToken);
        }

        var survivors = GetLiveProcesses(variant.Process);
        try
        {
            if (survivors.Count > 0)
            {
                throw new InvalidOperationException(
                    $"{displayName} is still running in the background after several automatic close attempts. " +
                    "End that Discord client from Task Manager, then try again.");
            }
        }
        finally
        {
            foreach (var process in survivors) process.Dispose();
        }

        if (restartTargetAdded || waitingReported)
            progress?.Report(new OperationProgress($"{displayName} closed."));
        return restartTargets;
    }

    private static async Task TryStopProcessAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            process.Refresh();
            if (process.HasExited) return;
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(cancellationToken).WaitAsync(PerProcessExitWait, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            // The process exited between discovery and the stop request.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Access/race failures are resolved by the verified retry loop above. An error is
            // shown only if the selected Discord client is genuinely still alive at timeout.
        }
        catch (TimeoutException)
        {
            // The verified retry loop will re-check and retry this exact client process.
        }
        finally
        {
            process.Dispose();
        }
    }

    private static List<Process> GetLiveProcesses(string processName)
    {
        var live = new List<Process>();
        foreach (var process in Process.GetProcessesByName(processName))
        {
            try
            {
                process.Refresh();
                if (!process.HasExited)
                {
                    live.Add(process);
                    continue;
                }
            }
            catch (InvalidOperationException)
            {
                // The process exited while we were validating it.
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // A query failure is not proof that the process exited. Keep it in the live
                // set so the retry loop stays fail-closed and only reports success after a
                // later clean snapshot confirms the selected client is gone.
                live.Add(process);
                continue;
            }

            process.Dispose();
        }

        return live;
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

    private static string DisplayBranch(string branch) => NormalizeExactBranch(branch) switch
    {
        "stable" => "Discord Stable",
        "ptb" => "Discord PTB",
        "canary" => "Discord Canary",
        _ => "Discord"
    };
}
