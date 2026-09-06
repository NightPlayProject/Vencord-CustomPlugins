using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VencordCustomManager;

public sealed partial class DiscordPatchService
{
    private static readonly (string Branch, string Folder)[] Variants =
    [
        ("stable", "Discord"),
        ("canary", "DiscordCanary"),
        ("ptb", "DiscordPTB")
    ];

    private const string PackageJson = "{\n\t\"name\": \"discord\",\n\t\"main\": \"index.js\"\n}";

    public DiscordInstallProbe? Probe(string branch)
    {
        branch = NormalizeBranch(branch);
        if (branch == "auto")
        {
            foreach (var variant in Variants)
            {
                var probe = ProbeVariant(variant.Branch, variant.Folder);
                if (probe is not null) return probe;
            }
            return null;
        }

        var match = Variants.FirstOrDefault(x => x.Branch.Equals(branch, StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(match.Branch) ? null : ProbeVariant(match.Branch, match.Folder);
    }

    public IReadOnlyList<DiscordInstallProbe> ProbeAll() =>
        Variants.Select(x => ProbeVariant(x.Branch, x.Folder)).Where(x => x is not null).Cast<DiscordInstallProbe>().ToArray();

    public DiscordInstallProbe? ProbeLocalResourcesForRollback(string branch, string resourcesDirectory)
    {
        branch = NormalizeExactBranch(branch);
        ValidateResourcesDirectory(branch, resourcesDirectory);
        return ProbeResources(branch, resourcesDirectory);
    }

    public DiscordInstallProbe EnsureManagedPatch(string branch, IProgress<OperationProgress>? progress = null)
    {
        branch = NormalizeExactBranch(branch);
        var probe = Probe(branch) ?? throw new InvalidOperationException($"Discord {DisplayBranch(branch)} was not found on this PC.");
        return EnsureManagedPatchAtResources(branch, probe.ResourcesDirectory, progress);
    }

    public DiscordInstallProbe EnsureManagedPatchAtResources(
        string branch,
        string resourcesDirectory,
        IProgress<OperationProgress>? progress = null)
    {
        branch = NormalizeExactBranch(branch);
        ValidateResourcesDirectory(branch, resourcesDirectory);
        var expectedPatcher = Path.Combine(ManagerPaths.GetInstallDirectory(branch), "dist", "patcher.js");
        if (!File.Exists(expectedPatcher))
            throw new InvalidOperationException("The managed Vencord patcher is missing. Reinstall the release files first.");

        var probe = ProbeResources(branch, resourcesDirectory)
            ?? throw new InvalidOperationException($"The selected {DisplayBranch(branch)} resources directory is no longer valid.");
        if (probe.IsPatched && probe.IsBranchScopedManagedPatch)
        {
            progress?.Report(new OperationProgress($"Existing {DisplayBranch(probe.Branch)} injection already points to this manager."));
            return VerifyManagedPatchAtResources(probe.Branch, resourcesDirectory, progress);
        }

        if (probe.IsPatched)
        {
            progress?.Report(new OperationProgress($"Replacing the existing Vencord injection in {DisplayBranch(probe.Branch)}…"));
            UnpatchResources(probe.ResourcesDirectory);
        }

        progress?.Report(new OperationProgress($"Injecting Custom Vencord into {DisplayBranch(probe.Branch)}…"));
        PatchResources(probe.ResourcesDirectory, expectedPatcher);
        return VerifyManagedPatchAtResources(probe.Branch, resourcesDirectory, progress);
    }

    public DiscordInstallProbe VerifyManagedPatch(string branch, IProgress<OperationProgress>? progress = null)
    {
        branch = NormalizeExactBranch(branch);
        var probe = Probe(branch) ?? throw new InvalidOperationException($"Discord {DisplayBranch(branch)} could not be found during verification.");
        return VerifyManagedPatchAtResources(branch, probe.ResourcesDirectory, progress);
    }

    public DiscordInstallProbe VerifyManagedPatchAtResources(
        string branch,
        string resourcesDirectory,
        IProgress<OperationProgress>? progress = null)
    {
        branch = NormalizeExactBranch(branch);
        ValidateResourcesDirectory(branch, resourcesDirectory);
        progress?.Report(new OperationProgress("Verifying Discord injection…"));
        var probe = ProbeResources(branch, resourcesDirectory)
            ?? throw new InvalidOperationException($"The selected {DisplayBranch(branch)} resources directory could not be read during verification.");
        if (!probe.IsPatched)
            throw new InvalidDataException($"Verification failed: {DisplayBranch(probe.Branch)} is not patched.");
        if (!probe.IsBranchScopedManagedPatch)
            throw new InvalidDataException($"Verification failed: {DisplayBranch(probe.Branch)} does not point to its client-specific managed Vencord payload.");

        var expectedPatcher = Path.Combine(ManagerPaths.GetInstallDirectory(branch), "dist", "patcher.js");
        if (!File.Exists(expectedPatcher))
            throw new InvalidDataException("Verification failed: the managed patcher file is missing.");

        progress?.Report(new OperationProgress($"Verified {DisplayBranch(probe.Branch)} → managed Custom Vencord.", 100));
        return probe;
    }

    public void UnpatchManaged(string branch, IProgress<OperationProgress>? progress = null)
    {
        branch = NormalizeExactBranch(branch);
        var probe = Probe(branch);
        if (probe is null || !probe.IsPatched) return;
        if (!probe.IsManagedPatch)
            throw new InvalidOperationException($"Refusing to uninstall another Vencord installation from {DisplayBranch(probe.Branch)}.");

        progress?.Report(new OperationProgress($"Removing Custom Vencord injection from {DisplayBranch(probe.Branch)}…"));
        UnpatchResources(probe.ResourcesDirectory);

        var verified = Probe(probe.Branch);
        if (verified is null || verified.IsPatched)
            throw new InvalidDataException("Uninstall verification failed: the Discord patch is still present.");
        progress?.Report(new OperationProgress("Discord injection removal verified.", 100));
    }

    public int UnpatchAllManagedForBranch(string branch, IProgress<OperationProgress>? progress = null)
    {
        branch = NormalizeExactBranch(branch);
        var variant = Variants.First(x => x.Branch.Equals(branch, StringComparison.OrdinalIgnoreCase));
        var baseDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), variant.Folder);
        if (!Directory.Exists(baseDirectory)) return 0;

        var branchManagedTarget = Path.Combine(ManagerPaths.GetInstallDirectory(branch), "dist", "patcher.js");
        var legacyManagedTarget = Path.Combine(ManagerPaths.LegacyInstallDirectory, "dist", "patcher.js");
        var removed = 0;

        foreach (var appDirectory in new DirectoryInfo(baseDirectory)
                     .EnumerateDirectories("app-*", SearchOption.TopDirectoryOnly)
                     .OrderByDescending(x => ParseAppVersion(x.Name)))
        {
            var resources = Path.Combine(appDirectory.FullName, "resources");
            var appAsar = Path.Combine(resources, "app.asar");
            var backupAsar = Path.Combine(resources, "_app.asar");
            var interruptedOld = Path.Combine(resources, "app.asar.manager-old");

            if (File.Exists(interruptedOld))
            {
                progress?.Report(new OperationProgress($"Recovering interrupted uninstall in {DisplayBranch(branch)} {appDirectory.Name}…"));
                RecoverResourcesToUnpatched(branch, resources);
                removed++;
                continue;
            }
            if (!File.Exists(appAsar) || !File.Exists(backupAsar)) continue;

            var target = ReadPatchTarget(appAsar);
            if (!PathsEqual(target, branchManagedTarget) && !PathsEqual(target, legacyManagedTarget)) continue;

            progress?.Report(new OperationProgress($"Removing manager-owned injection from {DisplayBranch(branch)} {appDirectory.Name}…"));
            UnpatchResources(resources);
            removed++;
        }

        if (AnyBranchPatchReferences(branch, branchManagedTarget) || AnyBranchPatchReferences(branch, legacyManagedTarget))
            throw new InvalidDataException($"Uninstall verification failed: a manager-owned {DisplayBranch(branch)} injection is still present.");

        progress?.Report(new OperationProgress($"Verified {DisplayBranch(branch)} manager-owned injections removed.", 100));
        return removed;
    }

    public void RestorePatchTarget(string branch, string patchTarget)
    {
        branch = NormalizeExactBranch(branch);
        var probe = Probe(branch);
        if (probe is null) throw new InvalidOperationException($"{DisplayBranch(branch)} disappeared during rollback.");
        RestorePatchTargetAtResources(branch, probe.ResourcesDirectory, patchTarget);
    }

    public void RestorePatchTargetAtResources(string branch, string resourcesDirectory, string patchTarget)
    {
        branch = NormalizeExactBranch(branch);
        ValidateResourcesDirectory(branch, resourcesDirectory);
        if (string.IsNullOrWhiteSpace(patchTarget) || !File.Exists(patchTarget))
            throw new InvalidOperationException("The previous Vencord patch target is no longer available for rollback.");

        var probe = ProbeResources(branch, resourcesDirectory)
            ?? throw new InvalidOperationException("The previously modified Discord resources directory no longer exists.");
        if (probe.IsPatched) UnpatchResources(resourcesDirectory);
        PatchResources(resourcesDirectory, patchTarget);

        var restored = ProbeResources(branch, resourcesDirectory);
        if (restored?.IsPatched != true || !PathsEqual(restored.PatchTarget, patchTarget))
            throw new InvalidDataException("Rollback verification failed for the exact Discord resources directory.");
    }

    public void EnsureUnpatched(string branch)
    {
        branch = NormalizeExactBranch(branch);
        var probe = Probe(branch);
        if (probe is not null) EnsureUnpatchedAtResources(branch, probe.ResourcesDirectory);
    }

    public void EnsureUnpatchedAtResources(string branch, string resourcesDirectory)
    {
        branch = NormalizeExactBranch(branch);
        ValidateResourcesDirectory(branch, resourcesDirectory);
        var probe = ProbeResources(branch, resourcesDirectory);
        if (probe?.IsPatched == true) UnpatchResources(resourcesDirectory);
        var verified = ProbeResources(branch, resourcesDirectory);
        if (verified is null)
            throw new InvalidDataException("Unpatch verification failed: Discord app.asar is missing or unreadable.");
        if (verified.IsPatched)
            throw new InvalidDataException("Unpatch verification failed for the exact Discord resources directory.");
    }

    public void RecoverResourcesToSnapshot(
        string branch,
        string resourcesDirectory,
        bool wasPatched,
        string previousPatchTarget)
    {
        branch = NormalizeExactBranch(branch);
        ValidateResourcesDirectory(branch, resourcesDirectory);
        NormalizeResourcesToUnpatched(resourcesDirectory);

        var unpatched = ProbeResources(branch, resourcesDirectory);
        if (unpatched is null || unpatched.IsPatched)
            throw new InvalidDataException("Could not recover Discord to a verified unpatched baseline.");

        if (!wasPatched) return;
        if (string.IsNullOrWhiteSpace(previousPatchTarget) || !File.Exists(previousPatchTarget))
            throw new InvalidOperationException("The previous Vencord patch target is no longer available for recovery.");

        PatchResources(resourcesDirectory, previousPatchTarget);
        var restored = ProbeResources(branch, resourcesDirectory);
        if (restored?.IsPatched != true || !PathsEqual(restored.PatchTarget, previousPatchTarget))
            throw new InvalidDataException("Discord patch recovery could not restore the exact previous injection.");
    }

    public void RecoverResourcesToUnpatched(string branch, string resourcesDirectory)
    {
        branch = NormalizeExactBranch(branch);
        ValidateResourcesDirectory(branch, resourcesDirectory);
        NormalizeResourcesToUnpatched(resourcesDirectory);
        var verified = ProbeResources(branch, resourcesDirectory);
        if (verified is null || verified.IsPatched)
            throw new InvalidDataException("Discord unpatch recovery could not be verified.");
    }

    public bool AnyDiscordPatchReferences(string patcherPath)
    {
        if (string.IsNullOrWhiteSpace(patcherPath)) return false;
        foreach (var variant in Variants)
        {
            var baseDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), variant.Folder);
            if (!Directory.Exists(baseDirectory)) continue;

            foreach (var appDirectory in new DirectoryInfo(baseDirectory).EnumerateDirectories("app-*", SearchOption.TopDirectoryOnly))
            {
                var resources = Path.Combine(appDirectory.FullName, "resources");
                var appAsar = Path.Combine(resources, "app.asar");
                var backupAsar = Path.Combine(resources, "_app.asar");
                if (!File.Exists(appAsar) || !File.Exists(backupAsar)) continue;
                if (PathsEqual(ReadPatchTarget(appAsar), patcherPath)) return true;
            }
        }
        return false;
    }

    private static bool AnyBranchPatchReferences(string branch, string patcherPath)
    {
        var variant = Variants.First(x => x.Branch.Equals(branch, StringComparison.OrdinalIgnoreCase));
        var baseDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), variant.Folder);
        if (!Directory.Exists(baseDirectory)) return false;
        foreach (var appDirectory in new DirectoryInfo(baseDirectory).EnumerateDirectories("app-*", SearchOption.TopDirectoryOnly))
        {
            var resources = Path.Combine(appDirectory.FullName, "resources");
            var appAsar = Path.Combine(resources, "app.asar");
            var backupAsar = Path.Combine(resources, "_app.asar");
            if (!File.Exists(appAsar) || !File.Exists(backupAsar)) continue;
            if (PathsEqual(ReadPatchTarget(appAsar), patcherPath)) return true;
        }
        return false;
    }

    private static DiscordInstallProbe? ProbeVariant(string branch, string folder)
    {
        var baseDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), folder);
        if (!Directory.Exists(baseDirectory)) return null;

        var appDirectory = FindLatestAppDirectory(baseDirectory);
        if (appDirectory is null) return null;

        var resources = Path.Combine(appDirectory.FullName, "resources");
        return ProbeResources(branch, resources);
    }

    private static DiscordInstallProbe? ProbeResources(string branch, string resources)
    {
        var variant = Variants.FirstOrDefault(x => x.Branch.Equals(branch, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(variant.Branch)) return null;
        var baseDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), variant.Folder);
        var appAsar = Path.Combine(resources, "app.asar");
        var backupAsar = Path.Combine(resources, "_app.asar");
        if (!Directory.Exists(resources) || !File.Exists(appAsar)) return null;

        var patched = File.Exists(backupAsar);
        var patchTarget = patched ? ReadPatchTarget(appAsar) : string.Empty;
        var branchManagedTarget = Path.Combine(ManagerPaths.GetInstallDirectory(branch), "dist", "patcher.js");
        var legacyManagedTarget = Path.Combine(ManagerPaths.LegacyInstallDirectory, "dist", "patcher.js");
        var isBranchScopedManaged = patched && PathsEqual(patchTarget, branchManagedTarget);
        var isLegacyManaged = patched && PathsEqual(patchTarget, legacyManagedTarget);
        var isManaged = isBranchScopedManaged || isLegacyManaged;
        return new DiscordInstallProbe(
            branch,
            baseDirectory,
            resources,
            appAsar,
            backupAsar,
            patched,
            patchTarget,
            isManaged,
            isBranchScopedManaged,
            isLegacyManaged);
    }

    private static void ValidateResourcesDirectory(string branch, string resourcesDirectory)
    {
        var variant = Variants.First(x => x.Branch.Equals(branch, StringComparison.OrdinalIgnoreCase));
        var baseDirectory = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            variant.Folder)).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(resourcesDirectory);
        if (!candidate.StartsWith(baseDirectory, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(candidate).Equals("resources", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to modify a Discord resources directory outside the selected client.");
    }

    private static DirectoryInfo? FindLatestAppDirectory(string baseDirectory)
    {
        return new DirectoryInfo(baseDirectory)
            .EnumerateDirectories("app-*", SearchOption.TopDirectoryOnly)
            .Select(directory => (Directory: directory, Version: ParseAppVersion(directory.Name)))
            .OrderByDescending(x => x.Version)
            .ThenByDescending(x => x.Directory.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Directory)
            .FirstOrDefault();
    }

    private static Version ParseAppVersion(string name)
    {
        var value = name.StartsWith("app-", StringComparison.OrdinalIgnoreCase) ? name[4..] : name;
        return Version.TryParse(value, out var version) ? version : new Version(0, 0);
    }

    private static string ReadPatchTarget(string appAsar)
    {
        try
        {
            var info = new FileInfo(appAsar);
            if (info.Length <= 0 || info.Length > 1024 * 1024) return string.Empty;
            var text = Encoding.UTF8.GetString(File.ReadAllBytes(appAsar));
            var match = RequireRegex().Match(text);
            if (!match.Success) return string.Empty;
            return JsonSerializer.Deserialize<string>(match.Groups["path"].Value) ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void PatchResources(string resourcesDirectory, string patcherPath)
    {
        var appAsar = Path.Combine(resourcesDirectory, "app.asar");
        var backupAsar = Path.Combine(resourcesDirectory, "_app.asar");
        var newAsar = Path.Combine(resourcesDirectory, "app.asar.manager-new");

        if (!File.Exists(appAsar)) throw new FileNotFoundException("Discord app.asar was not found.", appAsar);
        if (File.Exists(backupAsar)) throw new InvalidOperationException("Discord is already patched. Unpatch it before creating a new injection.");

        try
        {
            WriteAppAsar(newAsar, patcherPath);
            File.Move(appAsar, backupAsar);
            File.Move(newAsar, appAsar);
        }
        catch
        {
            TryDelete(newAsar);
            if (!File.Exists(appAsar) && File.Exists(backupAsar)) File.Move(backupAsar, appAsar);
            throw;
        }
    }

    private static void UnpatchResources(string resourcesDirectory)
    {
        var appAsar = Path.Combine(resourcesDirectory, "app.asar");
        var backupAsar = Path.Combine(resourcesDirectory, "_app.asar");
        var temporary = Path.Combine(resourcesDirectory, "app.asar.manager-old");
        if (!File.Exists(backupAsar)) return;

        TryDelete(temporary);
        try
        {
            if (File.Exists(appAsar)) File.Move(appAsar, temporary);
            File.Move(backupAsar, appAsar);
            TryDelete(temporary);
        }
        catch
        {
            if (!File.Exists(backupAsar) && File.Exists(appAsar)) File.Move(appAsar, backupAsar);
            if (!File.Exists(appAsar) && File.Exists(temporary)) File.Move(temporary, appAsar);
            throw;
        }
    }

    private static void NormalizeResourcesToUnpatched(string resourcesDirectory)
    {
        var appAsar = Path.Combine(resourcesDirectory, "app.asar");
        var backupAsar = Path.Combine(resourcesDirectory, "_app.asar");
        var newAsar = Path.Combine(resourcesDirectory, "app.asar.manager-new");
        var oldAsar = Path.Combine(resourcesDirectory, "app.asar.manager-old");

        if (File.Exists(backupAsar))
        {
            TryDelete(appAsar);
            File.Move(backupAsar, appAsar);
            TryDelete(newAsar);
            TryDelete(oldAsar);
            return;
        }

        if (!File.Exists(appAsar))
            throw new InvalidDataException("Discord app.asar is missing and no original _app.asar backup is available.");
        if (!string.IsNullOrWhiteSpace(ReadPatchTarget(appAsar)))
            throw new InvalidDataException("Discord app.asar still appears to be an injection but its original backup is missing.");

        TryDelete(newAsar);
        TryDelete(oldAsar);
    }

    private static void WriteAppAsar(string outputPath, string patcherPath)
    {
        var indexJs = "require(" + JsonSerializer.Serialize(patcherPath) + ")";
        var indexSize = Encoding.UTF8.GetByteCount(indexJs);
        var packageSize = Encoding.UTF8.GetByteCount(PackageJson);
        var header = JsonSerializer.Serialize(new
        {
            files = new Dictionary<string, object>
            {
                ["index.js"] = new { size = indexSize, offset = "0" },
                ["package.json"] = new { size = packageSize, offset = indexSize.ToString() }
            }
        });

        var headerStringSize = (uint)Encoding.UTF8.GetByteCount(header);
        const uint dataSize = 4;
        var alignedSize = (headerStringSize + dataSize - 1) & ~(dataSize - 1);
        var headerSize = alignedSize + 8;
        var headerObjectSize = alignedSize + dataSize;
        var paddedHeader = header + new string('0', checked((int)(alignedSize - headerStringSize)));

        using var stream = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false);
        writer.Write(dataSize);
        writer.Write(headerSize);
        writer.Write(headerObjectSize);
        writer.Write(headerStringSize);
        writer.Write(Encoding.UTF8.GetBytes(paddedHeader));
        writer.Write(Encoding.UTF8.GetBytes(indexJs));
        writer.Write(Encoding.UTF8.GetBytes(PackageJson));
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

    private static string NormalizeBranch(string branch) => branch.ToLowerInvariant() switch
    {
        "stable" => "stable",
        "ptb" => "ptb",
        "canary" => "canary",
        _ => "auto"
    };

    private static string NormalizeExactBranch(string branch) => branch.ToLowerInvariant() switch
    {
        "stable" => "stable",
        "ptb" => "ptb",
        "canary" => "canary",
        _ => throw new ArgumentOutOfRangeException(nameof(branch), "A specific Discord client is required for this operation.")
    };

    private static string DisplayBranch(string branch) => NormalizeBranch(branch) switch
    {
        "stable" => "Discord Stable",
        "ptb" => "Discord PTB",
        "canary" => "Discord Canary",
        _ => "Discord"
    };

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }

    [GeneratedRegex("require\\((?<path>\"(?:\\\\.|[^\"\\\\])*\")\\)", RegexOptions.CultureInvariant)]
    private static partial Regex RequireRegex();
}
