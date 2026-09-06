using System.IO.Compression;
using System.Text.Json;

namespace VencordCustomManager;

public sealed class InstallationService : IDisposable
{
    private readonly UpdateClient _updateClient = new();
    private readonly DiscordService _discordService = new();
    private readonly DiscordPatchService _patchService = new();

    public SelfUpdateService SelfUpdater { get; }

    public InstallationService()
    {
        SelfUpdater = new SelfUpdateService(_updateClient);
        ManagerPaths.EnsureCreated();
    }

    public Task<UpdateManifest> GetManifestAsync(CancellationToken cancellationToken = default) =>
        _updateClient.GetManifestAsync(cancellationToken);

    public ManagerState LoadState()
    {
        try
        {
            if (!File.Exists(ManagerPaths.StateFile)) return new ManagerState();
            var json = File.ReadAllText(ManagerPaths.StateFile);
            return JsonSerializer.Deserialize<ManagerState>(json, JsonOptions) ?? new ManagerState();
        }
        catch
        {
            return new ManagerState();
        }
    }

    public bool IsInstalled(ManagerState? state = null)
    {
        state ??= LoadState();
        return IsInstalled(state.DiscordBranch, state);
    }

    public bool IsInstalled(string branch, ManagerState? state = null)
    {
        state ??= LoadState();
        return !string.IsNullOrWhiteSpace(state.InstalledVersion)
            && HasManagedFiles()
            && _patchService.Probe(branch)?.IsManagedPatch == true;
    }

    public bool HasManagedFiles() =>
        Directory.Exists(ManagerPaths.InstallDirectory)
        && File.Exists(Path.Combine(ManagerPaths.InstallDirectory, "dist", "renderer.js"))
        && File.Exists(Path.Combine(ManagerPaths.InstallDirectory, "dist", "patcher.js"));

    public DiscordInstallProbe? ProbeLocalInstallation(string branch) => _patchService.Probe(branch);

    public IReadOnlyList<DiscordInstallProbe> ProbeAllLocalInstallations() => _patchService.ProbeAll();

    public ManagerState RecoverStateFromLocalInstallation(string branch, UpdateManifest? manifest = null)
    {
        var state = LoadState();
        var probe = _patchService.Probe(branch);
        if (probe?.IsManagedPatch != true)
            probe = _patchService.ProbeAll().FirstOrDefault(x => x.IsManagedPatch);
        if (probe?.IsManagedPatch != true || !HasManagedFiles()) return state;

        var metadata = LoadInstallMetadata();
        var version = state.InstalledVersion;
        if (string.IsNullOrWhiteSpace(version))
            version = metadata?.DistributionVersion ?? string.Empty;
        if (string.IsNullOrWhiteSpace(version) && manifest is not null && LocalPackageMatchesManifest(manifest))
            version = manifest.Version;

        if (string.IsNullOrWhiteSpace(version)) return state;

        var now = DateTimeOffset.UtcNow;
        state.InstalledVersion = version;
        state.InstalledAt ??= metadata?.PreparedAt ?? now;
        state.LastVerifiedAt = now;
        SaveState(state);
        WriteInstallMetadata(new ManagedInstallMetadata
        {
            DistributionVersion = version,
            DiscordBranch = probe.Branch,
            PreparedAt = state.InstalledAt ?? now,
            VerifiedAt = now
        });
        return state;
    }

    public void SavePreferredBranch(string branch)
    {
        var state = LoadState();
        state.DiscordBranch = NormalizeBranchPreference(branch);
        SaveState(state);
    }

    public async Task InstallOrUpdateAsync(
        UpdateManifest manifest,
        string branch,
        bool repair,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ManagerPaths.EnsureCreated();
        var oldState = LoadState();
        progress?.Report(new OperationProgress("Checking the existing Discord installation…"));
        var previousProbe = _patchService.Probe(branch);
        var previouslyManagedBranches = _patchService.ProbeAll()
            .Where(x => x.IsManagedPatch)
            .Select(x => x.Branch)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (previousProbe is not null)
        {
            var previousStatus = previousProbe.IsManagedPatch
                ? "managed Custom Vencord injection found"
                : previousProbe.IsPatched
                    ? "another Vencord injection found"
                    : "Discord found with no Vencord injection";
            progress?.Report(new OperationProgress($"{DisplayBranch(previousProbe.Branch)}: {previousStatus}."));
        }

        var payloadAlreadyCurrent = HasManagedFiles()
            && !string.IsNullOrWhiteSpace(oldState.InstalledVersion)
            && UpdateClient.CompareVersions(oldState.InstalledVersion, manifest.Version) == 0;

        // If another Discord channel already uses the current managed payload, adding this
        // channel only requires an injection. Avoid redownloading/replacing the shared build.
        if (!repair && previousProbe?.IsManagedPatch != true && payloadAlreadyCurrent)
        {
            IReadOnlyList<DiscordRestartTarget> attachRestartTargets = Array.Empty<DiscordRestartTarget>();
            try
            {
                progress?.Report(new OperationProgress("Current managed build is already verified locally; attaching it to the selected Discord client…"));
                attachRestartTargets = await _discordService.StopRunningAsync(progress, cancellationToken);
                var verifiedProbe = _patchService.EnsureManagedPatch(branch, progress);
                var now = DateTimeOffset.UtcNow;
                oldState.LastVerifiedAt = now;
                oldState.DiscordBranch = NormalizeBranchPreference(branch);
                SaveState(oldState);
                WriteInstallMetadata(new ManagedInstallMetadata
                {
                    DistributionVersion = oldState.InstalledVersion,
                    DiscordBranch = verifiedProbe.Branch,
                    PreparedAt = oldState.InstalledAt ?? now,
                    VerifiedAt = now
                });
                progress?.Report(new OperationProgress($"Installation verified · {DisplayBranch(verifiedProbe.Branch)} now uses Custom Vencord v{oldState.InstalledVersion}.", 100));
                return;
            }
            finally
            {
                _discordService.Restart(attachRestartTargets);
            }
        }

        var hadManagedDirectory = Directory.Exists(ManagerPaths.InstallDirectory);
        var stagingRoot = Path.Combine(ManagerPaths.StagingDirectory, Guid.NewGuid().ToString("N"));
        var extracted = Path.Combine(stagingRoot, "package");
        var zipPath = Path.Combine(stagingRoot, manifest.Assets.WindowsRelease.Name);
        Directory.CreateDirectory(extracted);

        string? backupPath = null;
        IReadOnlyList<DiscordRestartTarget> restartTargets = Array.Empty<DiscordRestartTarget>();

        try
        {
            progress?.Report(new OperationProgress($"Preparing Vencord Custom Plugins v{manifest.Version}…", 0));
            await _updateClient.DownloadFileAsync(manifest.Assets.WindowsRelease.Url, zipPath, progress, cancellationToken);

            progress?.Report(new OperationProgress("Verifying SHA-256…", 100));
            var actualHash = await UpdateClient.ComputeSha256Async(zipPath, cancellationToken);
            if (!actualHash.Equals(manifest.Assets.WindowsRelease.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Update integrity check failed. Expected {manifest.Assets.WindowsRelease.Sha256}, got {actualHash}.");

            progress?.Report(new OperationProgress("Extracting update…"));
            ZipFile.ExtractToDirectory(zipPath, extracted, overwriteFiles: true);
            ValidatePackage(extracted);

            restartTargets = await _discordService.StopRunningAsync(progress, cancellationToken);

            if (hadManagedDirectory)
            {
                backupPath = CreateBackupPath(oldState.InstalledVersion);
                progress?.Report(new OperationProgress("Backing up the current installation…"));
                Directory.Move(ManagerPaths.InstallDirectory, backupPath);
            }

            progress?.Report(new OperationProgress("Installing the new build…"));
            Directory.Move(extracted, ManagerPaths.InstallDirectory);
            CopyExamplePluginIfMissing();
            WriteInstallMetadata(new ManagedInstallMetadata
            {
                DistributionVersion = manifest.Version,
                DiscordBranch = branch,
                PreparedAt = DateTimeOffset.UtcNow
            });

            cancellationToken.ThrowIfCancellationRequested();
            var verifiedProbe = _patchService.EnsureManagedPatch(branch, progress);

            var branchesToVerify = previouslyManagedBranches
                .Append(verifiedProbe.Branch)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (branchesToVerify.Length > 1)
                progress?.Report(new OperationProgress("Verifying every Discord client that uses the shared managed build…"));
            foreach (var managedBranch in branchesToVerify)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _patchService.VerifyManagedPatch(managedBranch, progress);
            }

            var now = DateTimeOffset.UtcNow;
            var newState = new ManagerState
            {
                InstalledVersion = manifest.Version,
                InstalledAt = oldState.InstalledAt ?? now,
                LastUpdatedAt = now,
                LastVerifiedAt = now,
                DiscordBranch = NormalizeBranchPreference(branch),
                LastBackupPath = backupPath ?? oldState.LastBackupPath
            };
            SaveState(newState);
            WriteInstallMetadata(new ManagedInstallMetadata
            {
                DistributionVersion = manifest.Version,
                DiscordBranch = verifiedProbe.Branch,
                PreparedAt = newState.InstalledAt ?? now,
                VerifiedAt = now
            });
            PruneBackups(3);

            var completion = repair
                ? "Repair verified · complete."
                : previousProbe?.IsManagedPatch == true || hadManagedDirectory
                    ? "Update verified · complete."
                    : "Installation verified · complete.";
            progress?.Report(new OperationProgress(completion, 100));
        }
        catch
        {
            progress?.Report(new OperationProgress("Update failed. Rolling back…"));
            TryRollback(backupPath, hadManagedDirectory);
            TryRestorePreviousDiscordPatch(previousProbe, branch);
            throw;
        }
        finally
        {
            SafeDeleteDirectory(stagingRoot);
            _discordService.Restart(restartTargets);
        }
    }

    public async Task RepairAsync(
        UpdateManifest manifest,
        string branch,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        await InstallOrUpdateAsync(manifest, branch, repair: true, progress, cancellationToken);

    public async Task UninstallAsync(
        string branch,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var state = LoadState();
        var probe = _patchService.Probe(branch);
        var hasManagedPatch = probe?.IsManagedPatch == true;
        if (!hasManagedPatch) return;

        IReadOnlyList<DiscordRestartTarget> restartTargets = Array.Empty<DiscordRestartTarget>();
        try
        {
            restartTargets = await _discordService.StopRunningAsync(progress, cancellationToken);
            if (hasManagedPatch)
                _patchService.UnpatchManaged(probe!.Branch, progress);

            var remainingManagedClients = _patchService.ProbeAll().Where(x => x.IsManagedPatch).ToArray();
            if (remainingManagedClients.Length == 0)
            {
                progress?.Report(new OperationProgress("No other Discord clients use this build; removing managed Vencord files…"));
                SafeDeleteDirectory(ManagerPaths.InstallDirectory);
                state.InstalledVersion = string.Empty;
                TryDeleteFile(ManagerPaths.InstallMetadataFile);
            }
            else
            {
                var remaining = string.Join(", ", remainingManagedClients.Select(x => DisplayBranch(x.Branch)));
                progress?.Report(new OperationProgress($"Keeping the shared managed build because it is still used by {remaining}."));
            }
            state.LastUpdatedAt = DateTimeOffset.UtcNow;
            state.LastVerifiedAt = DateTimeOffset.UtcNow;
            state.DiscordBranch = NormalizeBranchPreference(branch);
            SaveState(state);
            progress?.Report(new OperationProgress($"Uninstall verified · {DisplayBranch(probe!.Branch)} is clean.", 100));
        }
        finally
        {
            _discordService.Restart(restartTargets);
        }
    }

    private static void ValidatePackage(string directory)
    {
        string[] required =
        [
            Path.Combine(directory, "dist", "renderer.js"),
            Path.Combine(directory, "dist", "patcher.js"),
            Path.Combine(directory, "dist", "vencordDesktopRenderer.js"),
            Path.Combine(directory, "install.bat"),
            Path.Combine(directory, "README.md")
        ];

        var missing = required.Where(path => !File.Exists(path)).ToArray();
        if (missing.Length > 0)
            throw new InvalidDataException("The release archive is missing expected files: " + string.Join(", ", missing.Select(Path.GetFileName)));
    }

    private static void CopyExamplePluginIfMissing()
    {
        var examples = Path.Combine(ManagerPaths.InstallDirectory, "example-plugins");
        if (!Directory.Exists(examples)) return;
        Directory.CreateDirectory(ManagerPaths.PluginsDirectory);

        foreach (var source in Directory.EnumerateFiles(examples, "*.js", SearchOption.TopDirectoryOnly))
        {
            var destination = Path.Combine(ManagerPaths.PluginsDirectory, Path.GetFileName(source));
            if (!File.Exists(destination)) File.Copy(source, destination);
        }
    }

    private void TryRestorePreviousDiscordPatch(DiscordInstallProbe? previousProbe, string requestedBranch)
    {
        try
        {
            var branch = previousProbe?.Branch ?? requestedBranch;
            var current = _patchService.Probe(branch);
            if (previousProbe?.IsPatched == true)
            {
                if (!string.IsNullOrWhiteSpace(previousProbe.PatchTarget)
                    && (!current?.IsPatched ?? true || !PathsEqual(current.PatchTarget, previousProbe.PatchTarget)))
                    _patchService.RestorePatchTarget(branch, previousProbe.PatchTarget);
            }
            else if (current?.IsManagedPatch == true)
            {
                _patchService.EnsureUnpatched(branch);
            }
        }
        catch
        {
            // Preserve the original installation exception. The managed-file backup remains available.
        }
    }

    private static ManagedInstallMetadata? LoadInstallMetadata()
    {
        try
        {
            if (!File.Exists(ManagerPaths.InstallMetadataFile)) return null;
            return JsonSerializer.Deserialize<ManagedInstallMetadata>(File.ReadAllText(ManagerPaths.InstallMetadataFile), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static void WriteInstallMetadata(ManagedInstallMetadata metadata)
    {
        Directory.CreateDirectory(ManagerPaths.InstallDirectory);
        File.WriteAllText(ManagerPaths.InstallMetadataFile, JsonSerializer.Serialize(metadata, JsonOptions));
    }

    private static bool LocalPackageMatchesManifest(UpdateManifest manifest)
    {
        try
        {
            var readmePath = Path.Combine(ManagerPaths.InstallDirectory, "README.md");
            if (!File.Exists(readmePath)) return false;
            var readme = File.ReadAllText(readmePath);
            var orion = manifest.Components.OrionQuests.Trim().TrimStart('v', 'V');
            var commit = manifest.Components.Vencord.Commit;
            var shortCommit = commit.Length >= 8 ? commit[..8] : commit;

            return readme.Contains($"OrionQuests v{orion}", StringComparison.OrdinalIgnoreCase)
                && readme.Contains(manifest.Components.Vencord.Version, StringComparison.OrdinalIgnoreCase)
                && (string.IsNullOrWhiteSpace(shortCommit) || readme.Contains(shortCommit, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private static bool PathsEqual(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try
        {
            return Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar)
                .Equals(Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string DisplayBranch(string branch) => branch.ToLowerInvariant() switch
    {
        "stable" => "Discord Stable",
        "ptb" => "Discord PTB",
        "canary" => "Discord Canary",
        _ => "Discord"
    };

    private static string NormalizeBranchPreference(string branch) => branch.ToLowerInvariant() switch
    {
        "stable" => "stable",
        "ptb" => "ptb",
        "canary" => "canary",
        _ => "auto"
    };

    private static string CreateBackupPath(string version)
    {
        var safeVersion = string.IsNullOrWhiteSpace(version) ? "unknown" : version.Replace(Path.DirectorySeparatorChar, '-');
        var path = Path.Combine(ManagerPaths.BackupsDirectory, $"v{safeVersion}-{DateTime.UtcNow:yyyyMMdd-HHmmss}");
        ManagerPaths.EnsureManagedPath(path);
        return path;
    }

    private static void TryRollback(string? backupPath, bool hadInstall)
    {
        try
        {
            if (Directory.Exists(ManagerPaths.InstallDirectory)) SafeDeleteDirectory(ManagerPaths.InstallDirectory);
            if (hadInstall && !string.IsNullOrWhiteSpace(backupPath) && Directory.Exists(backupPath))
                Directory.Move(backupPath, ManagerPaths.InstallDirectory);
        }
        catch
        {
            // Preserve the original exception. The backup remains under the backups directory.
        }
    }

    private static void PruneBackups(int keep)
    {
        try
        {
            var directories = new DirectoryInfo(ManagerPaths.BackupsDirectory)
                .EnumerateDirectories()
                .OrderByDescending(x => x.CreationTimeUtc)
                .Skip(keep)
                .ToArray();
            foreach (var directory in directories) SafeDeleteDirectory(directory.FullName);
        }
        catch { }
    }

    private static void SafeDeleteDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        ManagerPaths.EnsureManagedPath(path);
        Directory.Delete(path, recursive: true);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }

    private static void SaveState(ManagerState state)
    {
        ManagerPaths.EnsureCreated();
        var json = JsonSerializer.Serialize(state, JsonOptions);
        File.WriteAllText(ManagerPaths.StateFile, json);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public void Dispose() => _updateClient.Dispose();
}
