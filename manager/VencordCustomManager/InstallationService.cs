using System.IO.Compression;
using System.Text.Json;

namespace VencordCustomManager;

public sealed class InstallationService : IDisposable
{
    private readonly UpdateClient _updateClient = new();
    private readonly DiscordService _discordService = new();
    private readonly VencordInstallerService _installerService;

    public SelfUpdateService SelfUpdater { get; }

    public InstallationService()
    {
        _installerService = new VencordInstallerService(_updateClient);
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
        return !string.IsNullOrWhiteSpace(state.InstalledVersion)
            && Directory.Exists(ManagerPaths.InstallDirectory)
            && File.Exists(Path.Combine(ManagerPaths.InstallDirectory, "dist", "renderer.js"));
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
        var hadInstall = IsInstalled(oldState);
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

            if (!hadInstall)
            {
                await _installerService.RunAsync("install", branch, ManagerPaths.InstallDirectory, progress, cancellationToken);
            }
            else if (repair)
            {
                await _installerService.RunAsync("repair", branch, ManagerPaths.InstallDirectory, progress, cancellationToken);
            }

            var now = DateTimeOffset.UtcNow;
            var newState = new ManagerState
            {
                InstalledVersion = manifest.Version,
                InstalledAt = oldState.InstalledAt ?? now,
                LastUpdatedAt = now,
                DiscordBranch = branch,
                LastBackupPath = backupPath ?? oldState.LastBackupPath
            };
            SaveState(newState);
            PruneBackups(3);

            progress?.Report(new OperationProgress(repair ? "Repair complete." : "Update complete.", 100));
        }
        catch
        {
            progress?.Report(new OperationProgress("Update failed. Rolling back…"));
            TryRollback(backupPath, hadManagedDirectory);
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
        if (!IsInstalled(state)) return;

        IReadOnlyList<DiscordRestartTarget> restartTargets = Array.Empty<DiscordRestartTarget>();
        try
        {
            restartTargets = await _discordService.StopRunningAsync(progress, cancellationToken);
            await _installerService.RunAsync("uninstall", branch, ManagerPaths.InstallDirectory, progress, cancellationToken);

            progress?.Report(new OperationProgress("Removing managed Vencord files…"));
            SafeDeleteDirectory(ManagerPaths.InstallDirectory);
            state.InstalledVersion = string.Empty;
            state.LastUpdatedAt = DateTimeOffset.UtcNow;
            state.DiscordBranch = branch;
            SaveState(state);
            progress?.Report(new OperationProgress("Uninstall complete.", 100));
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
