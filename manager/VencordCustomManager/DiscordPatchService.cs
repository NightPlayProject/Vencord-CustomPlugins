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

    public DiscordInstallProbe EnsureManagedPatch(string branch, IProgress<OperationProgress>? progress = null)
    {
        var expectedPatcher = Path.Combine(ManagerPaths.InstallDirectory, "dist", "patcher.js");
        if (!File.Exists(expectedPatcher))
            throw new InvalidOperationException("The managed Vencord patcher is missing. Reinstall the release files first.");

        var probe = Probe(branch) ?? throw new InvalidOperationException($"Discord {DisplayBranch(branch)} was not found on this PC.");
        if (probe.IsPatched && probe.IsManagedPatch)
        {
            progress?.Report(new OperationProgress($"Existing {DisplayBranch(probe.Branch)} injection already points to this manager."));
            return VerifyManagedPatch(probe.Branch, progress);
        }

        if (probe.IsPatched)
        {
            progress?.Report(new OperationProgress($"Replacing the existing Vencord injection in {DisplayBranch(probe.Branch)}…"));
            UnpatchResources(probe.ResourcesDirectory);
        }

        progress?.Report(new OperationProgress($"Injecting Custom Vencord into {DisplayBranch(probe.Branch)}…"));
        PatchResources(probe.ResourcesDirectory, expectedPatcher);
        return VerifyManagedPatch(probe.Branch, progress);
    }

    public DiscordInstallProbe VerifyManagedPatch(string branch, IProgress<OperationProgress>? progress = null)
    {
        progress?.Report(new OperationProgress("Verifying Discord injection…"));
        var probe = Probe(branch) ?? throw new InvalidOperationException($"Discord {DisplayBranch(branch)} could not be found during verification.");
        if (!probe.IsPatched)
            throw new InvalidDataException($"Verification failed: {DisplayBranch(probe.Branch)} is not patched.");
        if (!probe.IsManagedPatch)
            throw new InvalidDataException($"Verification failed: {DisplayBranch(probe.Branch)} points to a different Vencord installation.");

        var expectedPatcher = Path.Combine(ManagerPaths.InstallDirectory, "dist", "patcher.js");
        if (!File.Exists(expectedPatcher))
            throw new InvalidDataException("Verification failed: the managed patcher file is missing.");

        progress?.Report(new OperationProgress($"Verified {DisplayBranch(probe.Branch)} → managed Custom Vencord.", 100));
        return probe;
    }

    public void UnpatchManaged(string branch, IProgress<OperationProgress>? progress = null)
    {
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

    public void RestorePatchTarget(string branch, string patchTarget)
    {
        if (string.IsNullOrWhiteSpace(patchTarget) || !File.Exists(patchTarget)) return;
        var probe = Probe(branch);
        if (probe is null) return;
        if (probe.IsPatched) UnpatchResources(probe.ResourcesDirectory);
        PatchResources(probe.ResourcesDirectory, patchTarget);
    }

    public void EnsureUnpatched(string branch)
    {
        var probe = Probe(branch);
        if (probe?.IsPatched == true) UnpatchResources(probe.ResourcesDirectory);
    }

    private static DiscordInstallProbe? ProbeVariant(string branch, string folder)
    {
        var baseDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), folder);
        if (!Directory.Exists(baseDirectory)) return null;

        var appDirectory = FindLatestAppDirectory(baseDirectory);
        if (appDirectory is null) return null;

        var resources = Path.Combine(appDirectory.FullName, "resources");
        var appAsar = Path.Combine(resources, "app.asar");
        var backupAsar = Path.Combine(resources, "_app.asar");
        if (!Directory.Exists(resources) || !File.Exists(appAsar)) return null;

        var patched = File.Exists(backupAsar);
        var patchTarget = patched ? ReadPatchTarget(appAsar) : string.Empty;
        var managedTarget = Path.Combine(ManagerPaths.InstallDirectory, "dist", "patcher.js");
        var isManaged = patched && PathsEqual(patchTarget, managedTarget);
        return new DiscordInstallProbe(branch, baseDirectory, resources, appAsar, backupAsar, patched, patchTarget, isManaged);
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
