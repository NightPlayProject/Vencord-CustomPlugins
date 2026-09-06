using System.IO.Compression;
using System.Security.Cryptography;
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
        ManagerPaths.EnsureCreated();
        SelfUpdater = new SelfUpdateService(_updateClient);
    }

    public Task<UpdateManifest> GetManifestAsync(CancellationToken cancellationToken = default) =>
        _updateClient.GetManifestAsync(cancellationToken);

    public ManagerState LoadState()
    {
        ManagerState state;
        if (!TryReadJson(ManagerPaths.StateFile, out state))
        {
            if (!TryReadJson(ManagerPaths.StateFile + ".bak", out state))
                state = new ManagerState();
        }

        // JSON deserialization does not preserve the dictionary comparer from the initializer.
        var normalizedClients = new Dictionary<string, ClientInstallState>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in state.Clients ?? new Dictionary<string, ClientInstallState>())
        {
            var client = pair.Value ?? new ClientInstallState();
            client.InstalledVersion = NormalizeStoredVersion(client.InstalledVersion);
            normalizedClients[NormalizeStateBranch(pair.Key)] = client;
        }
        state.Clients = normalizedClients;
        state.InstalledVersion = NormalizeStoredVersion(state.InstalledVersion);

        MigrateLegacyState(state);
        return state;
    }

    public ClientInstallState GetClientState(string branch, ManagerState? state = null)
    {
        branch = NormalizeExactBranch(branch);
        state ??= LoadState();
        if (!state.Clients.TryGetValue(branch, out var client))
        {
            client = new ClientInstallState();
            state.Clients[branch] = client;
        }
        return client;
    }

    public string GetInstalledVersion(string branch, ManagerState? state = null) =>
        GetClientState(branch, state).InstalledVersion;

    public bool IsInstalled(string branch, ManagerState? state = null)
    {
        branch = NormalizeExactBranch(branch);
        state ??= LoadState();
        var client = GetClientState(branch, state);
        return !string.IsNullOrWhiteSpace(client.InstalledVersion)
            && HasManagedFiles(branch)
            && _patchService.Probe(branch)?.IsBranchScopedManagedPatch == true;
    }

    public bool HasManagedFiles(string branch)
    {
        branch = NormalizeExactBranch(branch);
        return HasPayloadFiles(ManagerPaths.GetInstallDirectory(branch));
    }

    public bool HasLegacyManagedFiles() => HasPayloadFiles(ManagerPaths.LegacyInstallDirectory);

    public bool HasAnyManagedFiles() =>
        HasLegacyManagedFiles() || new[] { "stable", "ptb", "canary" }.Any(HasManagedFiles);

    public bool HasUsableManagedPayload(DiscordInstallProbe? probe)
    {
        if (probe?.IsBranchScopedManagedPatch == true)
            return HasManagedFiles(probe.Branch) && VerifyManagedPayloadIntegrity(probe.Branch);
        if (probe?.IsLegacyManagedPatch == true) return HasLegacyManagedFiles();
        return false;
    }

    public bool VerifyManagedPayloadIntegrity(string branch)
    {
        branch = NormalizeExactBranch(branch);
        var directory = ManagerPaths.GetInstallDirectory(branch);
        if (!HasPayloadFiles(directory)) return false;
        var metadata = LoadInstallMetadata(ManagerPaths.GetInstallMetadataFile(branch));
        return metadata is not null && VerifyPayloadHashes(directory, metadata.FileSha256);
    }

    public string GetManagedInstallDirectoryForBranch(string branch)
    {
        branch = NormalizeExactBranch(branch);
        var probe = _patchService.Probe(branch);
        if (probe?.IsBranchScopedManagedPatch == true && HasManagedFiles(branch))
            return ManagerPaths.GetInstallDirectory(branch);
        if (probe?.IsLegacyManagedPatch == true && HasLegacyManagedFiles())
            return ManagerPaths.LegacyInstallDirectory;
        if (HasManagedFiles(branch)) return ManagerPaths.GetInstallDirectory(branch);
        return ManagerPaths.Root;
    }

    public DiscordInstallProbe? ProbeLocalInstallation(string branch) => _patchService.Probe(branch);

    public IReadOnlyList<DiscordInstallProbe> ProbeAllLocalInstallations() => _patchService.ProbeAll();

    public IReadOnlyList<string> GetPendingRecoveryBranches() =>
        new[] { "stable", "ptb", "canary" }
            .Where(branch =>
            {
                var path = ManagerPaths.GetPendingOperationFile(branch);
                return File.Exists(path) || File.Exists(path + ".bak");
            })
            .ToArray();

    public bool HasPendingRecovery(string branch)
    {
        var path = ManagerPaths.GetPendingOperationFile(NormalizeExactBranch(branch));
        return File.Exists(path) || File.Exists(path + ".bak");
    }

    public async Task RecoverInterruptedOperationAsync(
        string branch,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        branch = NormalizeExactBranch(branch);
        var journalPath = ManagerPaths.GetPendingOperationFile(branch);
        if (!TryReadJson(journalPath, out PendingOperationJournal journal)
            && !TryReadJson(journalPath + ".bak", out journal))
        {
            throw new InvalidDataException(
                $"The interrupted-operation journal for {DisplayBranch(branch)} is unreadable. " +
                "The manager will not modify that client until the journal can be recovered or removed manually.");
        }

        IReadOnlyList<DiscordRestartTarget> restartTargets = Array.Empty<DiscordRestartTarget>();
        var restartSafe = true;
        try
        {
            progress?.Report(new OperationProgress($"Recovering interrupted operation for {DisplayBranch(branch)}…"));
            restartTargets = await _discordService.StopRunningAsync(branch, progress, cancellationToken);
            bool recovered;
            try
            {
                recovered = RecoverPendingJournal(branch, journal, progress);
            }
            catch
            {
                restartSafe = false;
                throw;
            }
            if (!recovered)
            {
                restartSafe = false;
                throw new InvalidOperationException(
                    $"Automatic recovery for {DisplayBranch(branch)} could not be verified. The client will remain closed until it is repaired manually.");
            }

            DeletePendingOperation(branch);
            TryRemoveLegacyPayloadIfUnused(branch, progress);
            progress?.Report(new OperationProgress($"Interrupted {DisplayBranch(branch)} operation recovered and verified.", 100));
        }
        finally
        {
            if (restartSafe)
                _discordService.Restart(restartTargets);
            else
                progress?.Report(new OperationProgress($"{DisplayBranch(branch)} was left closed because recovery was not verified."));
        }
    }

    public ManagerState RecoverStateFromLocalInstallation(string branch, UpdateManifest? manifest = null)
    {
        var state = LoadState();
        var normalized = NormalizeBranchPreference(branch);
        if (normalized == "auto")
            return RecoverAllStateFromLocalInstallations(manifest, state);

        if (HasPendingRecovery(normalized)) return state;

        var probe = _patchService.Probe(normalized);
        if (probe is not null) RecoverProbeState(state, probe, manifest);
        SaveState(state);
        return state;
    }

    public ManagerState RecoverAllStateFromLocalInstallations(UpdateManifest? manifest = null, ManagerState? state = null)
    {
        state ??= LoadState();
        foreach (var probe in _patchService.ProbeAll())
        {
            if (HasPendingRecovery(probe.Branch)) continue;
            RecoverProbeState(state, probe, manifest);
        }
        SaveState(state);
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
        branch = NormalizeExactBranch(branch);
        ManagerPaths.EnsureCreated();

        var oldState = LoadState();
        var oldClient = CloneClientState(GetClientState(branch, oldState));
        var installDirectory = ManagerPaths.GetInstallDirectory(branch);
        var previousProbe = _patchService.Probe(branch)
            ?? throw new InvalidOperationException($"{DisplayBranch(branch)} was not found on this PC.");

        progress?.Report(new OperationProgress($"Checking {DisplayBranch(branch)}…"));
        var previousStatus = previousProbe.IsBranchScopedManagedPatch
            ? "client-specific Custom Vencord injection found"
            : previousProbe.IsLegacyManagedPatch
                ? "legacy shared Custom Vencord injection found"
                : previousProbe.IsPatched
                    ? "another Vencord injection found"
                    : "no Vencord injection found";
        progress?.Report(new OperationProgress($"{DisplayBranch(branch)}: {previousStatus}."));

        var payloadAlreadyCurrent = HasManagedFiles(branch)
            && !string.IsNullOrWhiteSpace(oldClient.InstalledVersion)
            && UpdateClient.CompareVersions(oldClient.InstalledVersion, manifest.Version) == 0
            && IsClientPayloadVersionConfirmed(branch, manifest.Version);

        // The selected client already has its own verified payload; if it merely lost or
        // changed its injection, attach that payload without touching any other client.
        if (!repair && previousProbe.IsBranchScopedManagedPatch != true && payloadAlreadyCurrent)
        {
            IReadOnlyList<DiscordRestartTarget> restartTargets = Array.Empty<DiscordRestartTarget>();
            var attachRestartSafe = true;
            DiscordInstallProbe? rollbackProbe = null;
            PendingOperationJournal? attachJournal = null;
            try
            {
                progress?.Report(new OperationProgress($"Attaching the existing v{oldClient.InstalledVersion} payload to {DisplayBranch(branch)}…"));
                restartTargets = await _discordService.StopRunningAsync(branch, progress, cancellationToken);
                rollbackProbe = _patchService.Probe(branch)
                    ?? throw new InvalidOperationException($"{DisplayBranch(branch)} disappeared after it was closed.");
                attachJournal = new PendingOperationJournal
                {
                    OperationKind = PendingOperationAttach,
                    Branch = branch,
                    HadClientDirectory = true,
                    PreviousWasPatched = rollbackProbe.IsPatched,
                    PreviousPatchTarget = rollbackProbe.PatchTarget,
                    PreviousResourcesDirectory = rollbackProbe.ResourcesDirectory,
                    Phase = PendingPhasePatching,
                    StartedAtUtc = DateTimeOffset.UtcNow
                };
                WritePendingOperation(attachJournal);
                var verifiedProbe = _patchService.EnsureManagedPatchAtResources(branch, rollbackProbe.ResourcesDirectory, progress);
                attachJournal.Phase = PendingPhasePatchVerified;
                WritePendingOperation(attachJournal);
                var now = DateTimeOffset.UtcNow;
                var client = GetClientState(branch, oldState);
                client.LastVerifiedAt = now;
                client.InstalledAt ??= now;
                WriteInstallMetadata(branch, new ManagedInstallMetadata
                {
                    DistributionVersion = client.InstalledVersion,
                    DiscordBranch = branch,
                    PreparedAt = client.InstalledAt ?? now,
                    VerifiedAt = now,
                    FileSha256 = ComputePayloadHashes(ManagerPaths.GetInstallDirectory(branch))
                });
                SaveState(oldState);
                DeletePendingOperation(branch);
                progress?.Report(new OperationProgress($"Installation verified · {DisplayBranch(verifiedProbe.Branch)} now uses Custom Vencord v{client.InstalledVersion}.", 100));
                return;
            }
            catch (Exception operationError)
            {
                var patchRestored = rollbackProbe is null || TryRestorePreviousDiscordPatch(rollbackProbe, branch);
                RestoreClientState(oldState, branch, oldClient);
                var stateRestored = TrySaveState(oldState);
                attachRestartSafe = patchRestored;
                if (patchRestored && stateRestored)
                {
                    DeletePendingOperation(branch);
                    throw;
                }

                throw new InvalidOperationException(
                    $"The {DisplayBranch(branch)} install failed and its previous injection could not be fully restored. " +
                    "Discord will be left closed to avoid launching an uncertain state.",
                    operationError);
            }
            finally
            {
                if (attachRestartSafe)
                    _discordService.Restart(restartTargets);
                else
                    progress?.Report(new OperationProgress($"{DisplayBranch(branch)} was left closed because rollback was not fully verified."));
            }
        }

        var hadClientDirectory = Directory.Exists(installDirectory);
        var stagingRoot = Path.Combine(ManagerPaths.StagingDirectory, Guid.NewGuid().ToString("N"));
        var extracted = Path.Combine(stagingRoot, "package");
        var zipPath = Path.Combine(stagingRoot, manifest.Assets.WindowsRelease.Name);
        Directory.CreateDirectory(extracted);

        string? backupPath = hadClientDirectory ? CreateBackupPath(branch, oldClient.InstalledVersion) : null;
        IReadOnlyList<DiscordRestartTarget> operationRestartTargets = Array.Empty<DiscordRestartTarget>();
        var restartSafe = true;
        var payloadMutationStarted = false;
        var patchMutationStarted = false;
        DiscordInstallProbe? mutationProbe = null;
        PendingOperationJournal? pendingOperation = null;

        try
        {
            progress?.Report(new OperationProgress($"Preparing Custom Vencord v{manifest.Version} for {DisplayBranch(branch)}…", 0));
            await _updateClient.DownloadFileAsync(manifest.Assets.WindowsRelease.Url, zipPath, progress, cancellationToken);

            progress?.Report(new OperationProgress("Verifying SHA-256…", 100));
            var actualHash = await UpdateClient.ComputeSha256Async(zipPath, cancellationToken);
            if (!actualHash.Equals(manifest.Assets.WindowsRelease.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Update integrity check failed. Expected {manifest.Assets.WindowsRelease.Sha256}, got {actualHash}.");

            progress?.Report(new OperationProgress("Extracting update…"));
            ZipFile.ExtractToDirectory(zipPath, extracted, overwriteFiles: true);
            ValidatePackage(extracted);

            // Only the selected Discord process is stopped. Other clients remain running.
            operationRestartTargets = await _discordService.StopRunningAsync(branch, progress, cancellationToken);

            // Capture the exact app-* resources directory after Discord is fully stopped. The
            // updater may have changed app directories while the package was downloading.
            mutationProbe = _patchService.Probe(branch)
                ?? throw new InvalidOperationException($"{DisplayBranch(branch)} disappeared after it was closed.");
            pendingOperation = new PendingOperationJournal
            {
                OperationKind = PendingOperationInstall,
                Branch = branch,
                BackupPath = backupPath ?? string.Empty,
                HadClientDirectory = hadClientDirectory,
                PreviousWasPatched = mutationProbe.IsPatched,
                PreviousPatchTarget = mutationProbe.PatchTarget,
                PreviousResourcesDirectory = mutationProbe.ResourcesDirectory,
                Phase = PendingPhasePrepared,
                StartedAtUtc = DateTimeOffset.UtcNow
            };

            WritePendingOperation(pendingOperation);

            if (hadClientDirectory)
            {
                progress?.Report(new OperationProgress($"Backing up {DisplayBranch(branch)}'s current managed payload…"));
                Directory.Move(installDirectory, backupPath!);
                payloadMutationStarted = true;
            }

            progress?.Report(new OperationProgress($"Installing the new payload for {DisplayBranch(branch)}…"));
            Directory.Move(extracted, installDirectory);
            payloadMutationStarted = true;
            var preparedAt = DateTimeOffset.UtcNow;
            var payloadHashes = ComputePayloadHashes(installDirectory);
            WriteInstallMetadata(branch, new ManagedInstallMetadata
            {
                DistributionVersion = manifest.Version,
                DiscordBranch = branch,
                PreparedAt = preparedAt,
                FileSha256 = payloadHashes
            });
            pendingOperation.Phase = PendingPhasePayloadInstalled;
            WritePendingOperation(pendingOperation);

            cancellationToken.ThrowIfCancellationRequested();
            pendingOperation.Phase = PendingPhasePatching;
            WritePendingOperation(pendingOperation);
            patchMutationStarted = true;
            var verified = _patchService.EnsureManagedPatchAtResources(branch, mutationProbe.ResourcesDirectory, progress);
            _patchService.VerifyManagedPatchAtResources(branch, mutationProbe.ResourcesDirectory, progress);
            pendingOperation.Phase = PendingPhasePatchVerified;
            WritePendingOperation(pendingOperation);

            var now = DateTimeOffset.UtcNow;
            var clientState = GetClientState(branch, oldState);
            clientState.InstalledVersion = manifest.Version;
            clientState.InstalledAt ??= oldClient.InstalledAt ?? now;
            clientState.LastUpdatedAt = now;
            clientState.LastVerifiedAt = now;
            clientState.LastBackupPath = backupPath ?? oldClient.LastBackupPath;
            SaveState(oldState);
            WriteInstallMetadata(branch, new ManagedInstallMetadata
            {
                DistributionVersion = manifest.Version,
                DiscordBranch = verified.Branch,
                PreparedAt = clientState.InstalledAt ?? now,
                VerifiedAt = now,
                FileSha256 = payloadHashes
            });
            PruneBackups(branch, 3);
            DeletePendingOperation(branch);

            var completion = repair
                ? $"Repair verified · {DisplayBranch(branch)} only."
                : previousProbe.IsManagedPatch || hadClientDirectory
                    ? $"Update verified · {DisplayBranch(branch)} only."
                    : $"Installation verified · {DisplayBranch(branch)} only.";
            progress?.Report(new OperationProgress(completion, 100));
        }
        catch (Exception operationError)
        {
            progress?.Report(new OperationProgress($"Operation failed. Rolling back {DisplayBranch(branch)} only…"));
            var filesRestored = !payloadMutationStarted || TryRollback(installDirectory, backupPath, hadClientDirectory);
            var patchRestored = !patchMutationStarted || (mutationProbe is not null && TryRestorePreviousDiscordPatch(mutationProbe, branch));
            RestoreClientState(oldState, branch, oldClient);
            var stateRestored = TrySaveState(oldState);
            restartSafe = filesRestored && patchRestored;

            if (filesRestored && patchRestored && stateRestored)
            {
                DeletePendingOperation(branch);
                throw;
            }

            var recoveryPath = backupPath ?? "(no backup was created)";
            throw new InvalidOperationException(
                $"The {DisplayBranch(branch)} operation failed and automatic rollback could not be fully verified. " +
                $"Discord will be left closed to avoid launching a potentially broken client. Backup: {recoveryPath}",
                operationError);
        }
        finally
        {
            try { SafeDeleteDirectory(stagingRoot); }
            catch { progress?.Report(new OperationProgress("Temporary staging cleanup could not be completed; it will be retried later.")); }
            if (restartSafe)
            {
                _discordService.Restart(operationRestartTargets);
            }
            else
            {
                progress?.Report(new OperationProgress($"{DisplayBranch(branch)} was left closed because rollback was not fully verified."));
            }
        }
    }

    public Task RepairAsync(
        UpdateManifest manifest,
        string branch,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        InstallOrUpdateAsync(manifest, branch, repair: true, progress, cancellationToken);

    public async Task UninstallAsync(
        string branch,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        branch = NormalizeExactBranch(branch);
        var probe = _patchService.Probe(branch);
        if (probe?.IsManagedPatch != true) return;

        IReadOnlyList<DiscordRestartTarget> restartTargets = Array.Empty<DiscordRestartTarget>();
        var restartSafe = true;
        var journalWritten = false;
        try
        {
            restartTargets = await _discordService.StopRunningAsync(branch, progress, cancellationToken);
            WritePendingOperation(new PendingOperationJournal
            {
                OperationKind = PendingOperationUninstall,
                Branch = branch,
                Phase = PendingPhaseUninstalling,
                StartedAtUtc = DateTimeOffset.UtcNow
            });
            journalWritten = true;

            FinishUninstallAfterStop(branch, progress);
            DeletePendingOperation(branch);
            progress?.Report(new OperationProgress($"Uninstall verified · {DisplayBranch(branch)} only is clean.", 100));
        }
        catch (Exception operationError)
        {
            if (journalWritten) restartSafe = false;
            throw new InvalidOperationException(
                journalWritten
                    ? $"The {DisplayBranch(branch)} uninstall was interrupted and will be recovered on the next manager launch. The client will remain closed until recovery is verified."
                    : $"The {DisplayBranch(branch)} uninstall could not start safely.",
                operationError);
        }
        finally
        {
            if (restartSafe)
                _discordService.Restart(restartTargets);
            else
                progress?.Report(new OperationProgress($"{DisplayBranch(branch)} was left closed because uninstall recovery is still pending."));
        }
    }

    private void FinishUninstallAfterStop(string branch, IProgress<OperationProgress>? progress = null)
    {
        branch = NormalizeExactBranch(branch);
        _patchService.UnpatchAllManagedForBranch(branch, progress);

        var clientDirectory = ManagerPaths.GetInstallDirectory(branch);
        if (Directory.Exists(clientDirectory))
        {
            progress?.Report(new OperationProgress($"Removing {DisplayBranch(branch)}'s managed payload…"));
            SafeDeleteDirectory(clientDirectory);
        }

        TryRemoveLegacyPayloadIfUnused(branch, progress);

        var state = LoadState();
        state.Clients[branch] = new ClientInstallState
        {
            LastUpdatedAt = DateTimeOffset.UtcNow,
            LastVerifiedAt = DateTimeOffset.UtcNow
        };
        SaveState(state);

        if (_patchService.Probe(branch)?.IsManagedPatch == true)
            throw new InvalidDataException($"Uninstall verification failed: {DisplayBranch(branch)} still has a manager-owned injection.");
    }

    private void TryRemoveLegacyPayloadIfUnused(string currentBranch, IProgress<OperationProgress>? progress = null)
    {
        currentBranch = NormalizeExactBranch(currentBranch);
        if (!Directory.Exists(ManagerPaths.LegacyInstallDirectory)) return;

        // Another client's pending operation may need the legacy patcher as its exact rollback
        // target even when its app.asar is temporarily between rename steps and cannot be probed.
        // Never delete that rollback dependency until the other journal is resolved.
        if (GetPendingRecoveryBranches().Any(branch => !branch.Equals(currentBranch, StringComparison.OrdinalIgnoreCase)))
        {
            progress?.Report(new OperationProgress("Keeping the legacy shared payload because another Discord client still has pending recovery."));
            return;
        }

        var legacyPatcher = Path.Combine(ManagerPaths.LegacyInstallDirectory, "dist", "patcher.js");
        if (_patchService.AnyDiscordPatchReferences(legacyPatcher)) return;

        progress?.Report(new OperationProgress("No clients still use the legacy shared payload; removing it…"));
        SafeDeleteDirectory(ManagerPaths.LegacyInstallDirectory);
    }

    private void RecoverProbeState(ManagerState state, DiscordInstallProbe probe, UpdateManifest? manifest)
    {
        if (!probe.IsManagedPatch || !HasUsableManagedPayload(probe)) return;

        var client = GetClientState(probe.Branch, state);
        var payloadDirectory = probe.IsBranchScopedManagedPatch
            ? ManagerPaths.GetInstallDirectory(probe.Branch)
            : ManagerPaths.LegacyInstallDirectory;
        var metadata = probe.IsBranchScopedManagedPatch
            ? LoadInstallMetadata(ManagerPaths.GetInstallMetadataFile(probe.Branch))
            : LoadInstallMetadata(ManagerPaths.LegacyInstallMetadataFile);

        // Metadata is written alongside the payload before injection. Prefer it over stale
        // manager-state data so interrupted updates recover the version that is actually on disk.
        var version = metadata?.DistributionVersion ?? string.Empty;
        if (string.IsNullOrWhiteSpace(version)) version = client.InstalledVersion;
        if (string.IsNullOrWhiteSpace(version) && probe.IsLegacyManagedPatch) version = state.InstalledVersion;
        if (string.IsNullOrWhiteSpace(version) && manifest is not null && LocalPackageMatchesManifest(manifest, payloadDirectory))
            version = manifest.Version;
        if (string.IsNullOrWhiteSpace(version)) return;

        var now = DateTimeOffset.UtcNow;
        client.InstalledVersion = version;
        client.InstalledAt ??= metadata?.PreparedAt ?? state.InstalledAt ?? now;
        client.LastVerifiedAt = now;

        if (probe.IsBranchScopedManagedPatch)
        {
            if (!VerifyPayloadHashes(payloadDirectory, metadata?.FileSha256)) return;
            WriteInstallMetadata(probe.Branch, new ManagedInstallMetadata
            {
                DistributionVersion = version,
                DiscordBranch = probe.Branch,
                PreparedAt = client.InstalledAt ?? now,
                VerifiedAt = now,
                FileSha256 = new Dictionary<string, string>(metadata!.FileSha256, StringComparer.OrdinalIgnoreCase)
            });
        }
    }

    private void MigrateLegacyState(ManagerState state)
    {
        if (string.IsNullOrWhiteSpace(state.InstalledVersion)) return;
        foreach (var probe in _patchService.ProbeAll().Where(x => x.IsManagedPatch))
        {
            var branch = probe.Branch;
            if (state.Clients.TryGetValue(branch, out var existing) && !string.IsNullOrWhiteSpace(existing.InstalledVersion))
                continue;

            state.Clients[branch] = new ClientInstallState
            {
                InstalledVersion = state.InstalledVersion,
                InstalledAt = state.InstalledAt,
                LastUpdatedAt = state.LastUpdatedAt,
                LastVerifiedAt = state.LastVerifiedAt,
                LastBackupPath = state.LastBackupPath
            };
        }
    }

    private static ClientInstallState CloneClientState(ClientInstallState source) => new()
    {
        InstalledVersion = source.InstalledVersion,
        InstalledAt = source.InstalledAt,
        LastUpdatedAt = source.LastUpdatedAt,
        LastVerifiedAt = source.LastVerifiedAt,
        LastBackupPath = source.LastBackupPath
    };

    private static void RestoreClientState(ManagerState state, string branch, ClientInstallState oldClient) =>
        state.Clients[NormalizeExactBranch(branch)] = CloneClientState(oldClient);

    private static bool HasPayloadFiles(string directory)
    {
        if (!Directory.Exists(directory)) return false;
        foreach (var relative in new[]
                 {
                     Path.Combine("dist", "renderer.js"),
                     Path.Combine("dist", "patcher.js"),
                     Path.Combine("dist", "vencordDesktopRenderer.js"),
                     "README.md"
                 })
        {
            var path = Path.Combine(directory, relative);
            if (!File.Exists(path) || new FileInfo(path).Length == 0) return false;
        }
        return true;
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

        var missing = required.Where(path => !File.Exists(path) || new FileInfo(path).Length == 0).ToArray();
        if (missing.Length > 0)
            throw new InvalidDataException("The release archive is missing expected files: " + string.Join(", ", missing.Select(Path.GetFileName)));
    }

    private bool TryRestorePreviousDiscordPatch(DiscordInstallProbe? previousProbe, string branch)
    {
        try
        {
            branch = NormalizeExactBranch(branch);
            if (previousProbe is null) return true;
            _patchService.RecoverResourcesToSnapshot(
                branch,
                previousProbe.ResourcesDirectory,
                previousProbe.IsPatched,
                previousProbe.PatchTarget);
            var verified = _patchService.ProbeLocalResourcesForRollback(branch, previousProbe.ResourcesDirectory);
            if (previousProbe.IsPatched)
                return verified?.IsPatched == true && PathsEqual(verified.PatchTarget, previousProbe.PatchTarget);
            return verified is not null && !verified.IsPatched;
        }
        catch
        {
            return false;
        }
    }

    private static ManagedInstallMetadata? LoadInstallMetadata(string metadataPath)
    {
        if (!TryReadJson(metadataPath, out ManagedInstallMetadata metadata)
            && !TryReadJson(metadataPath + ".bak", out metadata))
            return null;
        metadata.DistributionVersion = NormalizeStoredVersion(metadata.DistributionVersion);
        return metadata;
    }

    private static bool IsClientPayloadVersionConfirmed(string branch, string version)
    {
        var metadata = LoadInstallMetadata(ManagerPaths.GetInstallMetadataFile(branch));
        if (metadata is null || string.IsNullOrWhiteSpace(metadata.DistributionVersion)) return false;
        var expected = NormalizeStoredVersion(version);
        return !string.IsNullOrWhiteSpace(expected)
            && metadata.DistributionVersion.Equals(expected, StringComparison.OrdinalIgnoreCase)
            && VerifyPayloadHashes(ManagerPaths.GetInstallDirectory(branch), metadata.FileSha256);
    }

    private static readonly string[] IntegrityFiles =
    [
        Path.Combine("dist", "package.json"),
        Path.Combine("dist", "patcher.js"),
        Path.Combine("dist", "preload.js"),
        Path.Combine("dist", "renderer.js"),
        Path.Combine("dist", "renderer.css"),
        Path.Combine("dist", "vencordDesktopMain.js"),
        Path.Combine("dist", "vencordDesktopPreload.js"),
        Path.Combine("dist", "vencordDesktopRenderer.js"),
        Path.Combine("dist", "vencordDesktopRenderer.css"),
        "README.md"
    ];

    private static Dictionary<string, string> ComputePayloadHashes(string directory)
    {
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var relative in IntegrityFiles)
        {
            var path = Path.Combine(directory, relative);
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
                throw new InvalidDataException($"Managed payload integrity file is missing or empty: {relative}");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            hashes[relative.Replace(Path.DirectorySeparatorChar, '/')] = Convert.ToHexString(SHA256.HashData(stream));
        }
        return hashes;
    }

    private static bool VerifyPayloadHashes(string directory, IReadOnlyDictionary<string, string>? expected)
    {
        if (expected is null || expected.Count == 0) return false;
        try
        {
            foreach (var relative in IntegrityFiles)
            {
                var key = relative.Replace(Path.DirectorySeparatorChar, '/');
                if (!expected.TryGetValue(key, out var expectedHash) || expectedHash.Length != 64) return false;
                var path = Path.Combine(directory, relative);
                if (!File.Exists(path) || new FileInfo(path).Length == 0) return false;
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var actual = Convert.ToHexString(SHA256.HashData(stream));
                if (!actual.Equals(expectedHash, StringComparison.OrdinalIgnoreCase)) return false;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void WriteInstallMetadata(string branch, ManagedInstallMetadata metadata)
    {
        var installDirectory = ManagerPaths.GetInstallDirectory(branch);
        Directory.CreateDirectory(installDirectory);
        AtomicWriteText(ManagerPaths.GetInstallMetadataFile(branch), JsonSerializer.Serialize(metadata, JsonOptions));
    }

    private static bool LocalPackageMatchesManifest(UpdateManifest manifest, string directory)
    {
        try
        {
            var readmePath = Path.Combine(directory, "README.md");
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

    private static string DisplayBranch(string branch) => NormalizeExactBranch(branch) switch
    {
        "stable" => "Discord Stable",
        "ptb" => "Discord PTB",
        "canary" => "Discord Canary",
        _ => "Discord"
    };

    private static string NormalizeExactBranch(string branch) => branch.ToLowerInvariant() switch
    {
        "stable" => "stable",
        "ptb" => "ptb",
        "canary" => "canary",
        _ => throw new ArgumentOutOfRangeException(nameof(branch), "A specific Discord client is required for this operation.")
    };

    private static string NormalizeStateBranch(string branch) => branch.ToLowerInvariant() switch
    {
        "stable" => "stable",
        "ptb" => "ptb",
        "canary" => "canary",
        _ => branch.ToLowerInvariant()
    };

    private static string NormalizeBranchPreference(string branch) => branch.ToLowerInvariant() switch
    {
        "stable" => "stable",
        "ptb" => "ptb",
        "canary" => "canary",
        _ => "auto"
    };

    private static string CreateBackupPath(string branch, string version)
    {
        branch = NormalizeExactBranch(branch);
        var backupRoot = ManagerPaths.GetBackupsDirectory(branch);
        Directory.CreateDirectory(backupRoot);
        var safeVersion = string.IsNullOrWhiteSpace(version) ? "unknown" : version.Replace(Path.DirectorySeparatorChar, '-');
        var path = Path.Combine(backupRoot, $"v{safeVersion}-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}");
        ManagerPaths.EnsureManagedPath(path);
        return path;
    }

    private static bool TryRollback(string installDirectory, string? backupPath, bool hadInstall)
    {
        try
        {
            if (Directory.Exists(installDirectory)) SafeDeleteDirectory(installDirectory);
            if (hadInstall && !string.IsNullOrWhiteSpace(backupPath) && Directory.Exists(backupPath))
                Directory.Move(backupPath, installDirectory);
            return hadInstall ? Directory.Exists(installDirectory) : !Directory.Exists(installDirectory);
        }
        catch
        {
            return false;
        }
    }

    private static void PruneBackups(string branch, int keep)
    {
        try
        {
            var backupRoot = ManagerPaths.GetBackupsDirectory(branch);
            if (!Directory.Exists(backupRoot)) return;
            var directories = new DirectoryInfo(backupRoot)
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

    private static void SaveState(ManagerState state)
    {
        ManagerPaths.EnsureCreated();
        var json = JsonSerializer.Serialize(state, JsonOptions);
        AtomicWriteText(ManagerPaths.StateFile, json);
    }

    private static bool TrySaveState(ManagerState state)
    {
        try
        {
            SaveState(state);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool RecoverPendingJournal(
        string branch,
        PendingOperationJournal journal,
        IProgress<OperationProgress>? progress = null)
    {
        branch = NormalizeExactBranch(branch);
        if (!string.Equals(NormalizeExactBranch(journal.Branch), branch, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The pending-operation journal does not match the Discord client being recovered.");

        ValidatePendingJournalPaths(branch, journal);

        var operationKind = string.IsNullOrWhiteSpace(journal.OperationKind)
            ? PendingOperationInstall
            : journal.OperationKind;
        if (!operationKind.Equals(PendingOperationInstall, StringComparison.OrdinalIgnoreCase)
            && !operationKind.Equals(PendingOperationAttach, StringComparison.OrdinalIgnoreCase)
            && !operationKind.Equals(PendingOperationUninstall, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The pending-operation journal contains an unknown operation type.");
        if (operationKind.Equals(PendingOperationUninstall, StringComparison.OrdinalIgnoreCase))
        {
            progress?.Report(new OperationProgress($"Resuming interrupted uninstall for {DisplayBranch(branch)}…"));
            FinishUninstallAfterStop(branch, progress);
            return true;
        }

        var installDirectory = ManagerPaths.GetInstallDirectory(branch);
        try
        {
            var phase = journal.Phase ?? PendingPhasePrepared;
            if (phase.Equals(PendingPhasePatchVerified, StringComparison.OrdinalIgnoreCase)
                && VerifyManagedPayloadIntegrity(branch)
                && !string.IsNullOrWhiteSpace(journal.PreviousResourcesDirectory))
            {
                try
                {
                    _patchService.VerifyManagedPatchAtResources(branch, journal.PreviousResourcesDirectory, progress);
                    return true;
                }
                catch
                {
                    // Fall through to conservative rollback below.
                }
            }

            if (phase.Equals(PendingPhasePayloadInstalled, StringComparison.OrdinalIgnoreCase)
                && VerifyManagedPayloadIntegrity(branch))
            {
                // The package had been fully moved and its metadata written, but no Discord
                // injection mutation had begun. Keeping the isolated payload is safe.
                return true;
            }

            progress?.Report(new OperationProgress($"Rolling back interrupted {DisplayBranch(branch)} changes…"));
            var filesRestored = RecoverJournalPayload(branch, installDirectory, journal);
            var patchRestored = phase.Equals(PendingPhasePatching, StringComparison.OrdinalIgnoreCase)
                || phase.Equals(PendingPhasePatchVerified, StringComparison.OrdinalIgnoreCase)
                ? RecoverJournalPatch(branch, journal)
                : true;
            return filesRestored && patchRestored;
        }
        catch
        {
            return false;
        }
    }

    private static bool RecoverJournalPayload(string branch, string installDirectory, PendingOperationJournal journal)
    {
        try
        {
            if (!journal.HadClientDirectory)
            {
                if (Directory.Exists(installDirectory)) SafeDeleteDirectory(installDirectory);
                return !Directory.Exists(installDirectory);
            }

            if (string.IsNullOrWhiteSpace(journal.BackupPath) || !Directory.Exists(journal.BackupPath))
                return Directory.Exists(installDirectory);

            ManagerPaths.EnsureManagedPath(journal.BackupPath);
            if (Directory.Exists(installDirectory)) SafeDeleteDirectory(installDirectory);
            Directory.Move(journal.BackupPath, installDirectory);
            return Directory.Exists(installDirectory);
        }
        catch
        {
            return false;
        }
    }

    private bool RecoverJournalPatch(string branch, PendingOperationJournal journal)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(journal.PreviousResourcesDirectory)) return false;
            _patchService.RecoverResourcesToSnapshot(
                branch,
                journal.PreviousResourcesDirectory,
                journal.PreviousWasPatched,
                journal.PreviousPatchTarget);
            var restored = _patchService.ProbeLocalResourcesForRollback(branch, journal.PreviousResourcesDirectory);
            if (restored is null) return false;
            return journal.PreviousWasPatched
                ? restored.IsPatched && PathsEqual(restored.PatchTarget, journal.PreviousPatchTarget)
                : !restored.IsPatched;
        }
        catch
        {
            return false;
        }
    }

    private static void WritePendingOperation(PendingOperationJournal journal)
    {
        journal.Branch = NormalizeExactBranch(journal.Branch);
        ValidatePendingJournalPaths(journal.Branch, journal);
        AtomicWriteText(
            ManagerPaths.GetPendingOperationFile(journal.Branch),
            JsonSerializer.Serialize(journal, JsonOptions));
    }

    private static void DeletePendingOperation(string branch)
    {
        var path = ManagerPaths.GetPendingOperationFile(branch);
        try
        {
            // Remove the older backup first. If cleanup is interrupted, the newest primary
            // journal remains authoritative and recovery cannot replay a stale earlier phase.
            if (File.Exists(path + ".bak")) File.Delete(path + ".bak");
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }

    private static void ValidatePendingJournalPaths(string branch, PendingOperationJournal journal)
    {
        branch = NormalizeExactBranch(branch);
        if (!string.IsNullOrWhiteSpace(journal.BackupPath))
        {
            ManagerPaths.EnsureManagedPath(journal.BackupPath);
            var backupRoot = Path.GetFullPath(ManagerPaths.GetBackupsDirectory(branch)).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var candidate = Path.GetFullPath(journal.BackupPath);
            if (!candidate.StartsWith(backupRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The pending-operation backup path is outside the selected Discord client's backup directory.");
        }

        if (!string.IsNullOrWhiteSpace(journal.PreviousResourcesDirectory))
        {
            // This validates branch ownership without requiring app.asar to still exist; a crash
            // may have interrupted one of the rename steps.
            var variantFolder = branch switch
            {
                "stable" => "Discord",
                "ptb" => "DiscordPTB",
                "canary" => "DiscordCanary",
                _ => throw new ArgumentOutOfRangeException(nameof(branch))
            };
            var clientRoot = Path.GetFullPath(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                variantFolder)).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var resources = Path.GetFullPath(journal.PreviousResourcesDirectory);
            if (!resources.StartsWith(clientRoot, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(resources).Equals("resources", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The pending-operation resources path does not belong to the selected Discord client.");
        }
    }

    private static bool TryReadJson<T>(string path, out T value) where T : class, new()
    {
        value = new T();
        try
        {
            if (!File.Exists(path)) return false;
            var parsed = JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions);
            if (parsed is null) return false;
            value = parsed;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizeStoredVersion(string value)
    {
        value = value?.Trim().TrimStart('v', 'V') ?? string.Empty;
        return Version.TryParse(value, out var parsed) ? parsed.ToString() : string.Empty;
    }

    private static void AtomicWriteText(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        var backup = path + ".bak";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(content);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
            {
                try
                {
                    File.Replace(temporary, path, backup, ignoreMetadataErrors: true);
                }
                catch (PlatformNotSupportedException)
                {
                    File.Copy(path, backup, overwrite: true);
                    File.Move(temporary, path, overwrite: true);
                }
                catch (IOException)
                {
                    File.Copy(path, backup, overwrite: true);
                    File.Move(temporary, path, overwrite: true);
                }
            }
            else
            {
                File.Move(temporary, path);
            }
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch { }
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private sealed class PendingOperationJournal
    {
        public string OperationKind { get; set; } = PendingOperationInstall;
        public string Branch { get; set; } = string.Empty;
        public string BackupPath { get; set; } = string.Empty;
        public bool HadClientDirectory { get; set; }
        public bool PreviousWasPatched { get; set; }
        public string PreviousPatchTarget { get; set; } = string.Empty;
        public string PreviousResourcesDirectory { get; set; } = string.Empty;
        public string Phase { get; set; } = PendingPhasePrepared;
        public DateTimeOffset StartedAtUtc { get; set; }
    }

    private const string PendingPhasePrepared = "prepared";
    private const string PendingPhasePayloadInstalled = "payload-installed";
    private const string PendingPhasePatching = "patching";
    private const string PendingPhasePatchVerified = "patch-verified";
    private const string PendingPhaseUninstalling = "uninstalling";
    private const string PendingOperationInstall = "install-update-repair";
    private const string PendingOperationAttach = "attach";
    private const string PendingOperationUninstall = "uninstall";

    public void Dispose() => _updateClient.Dispose();
}
