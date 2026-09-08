using System.Text.Json.Serialization;

namespace VencordCustomManager;

public sealed class UpdateManifest
{
    [JsonPropertyName("schema")]
    public int Schema { get; set; }

    [JsonPropertyName("channel")]
    public string Channel { get; set; } = "stable";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "0.0.0";

    [JsonPropertyName("published_at")]
    public DateTimeOffset PublishedAt { get; set; }

    [JsonPropertyName("release_page")]
    public string ReleasePage { get; set; } = string.Empty;

    [JsonPropertyName("components")]
    public ManifestComponents Components { get; set; } = new();

    [JsonPropertyName("assets")]
    public ManifestAssets Assets { get; set; } = new();

    [JsonPropertyName("manager")]
    public ManagerRelease? Manager { get; set; }
}

public sealed class ManagerRelease
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "0.0.0";

    [JsonPropertyName("asset")]
    public ManifestAsset Asset { get; set; } = new();
}

public sealed class ManifestComponents
{
    [JsonPropertyName("vencord")]
    public VencordComponent Vencord { get; set; } = new();

    [JsonPropertyName("orion_quests")]
    public string OrionQuests { get; set; } = "unknown";

    [JsonPropertyName("nitro_sniper")]
    public string NitroSniper { get; set; } = "unknown";

    [JsonPropertyName("runtime_plugin_loader")]
    public bool RuntimePluginLoader { get; set; }
}

public sealed class VencordComponent
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "unknown";

    [JsonPropertyName("commit")]
    public string Commit { get; set; } = string.Empty;
}

public sealed class ManifestAssets
{
    [JsonPropertyName("windows_release")]
    public ManifestAsset WindowsRelease { get; set; } = new();

    [JsonPropertyName("source")]
    public ManifestAsset Source { get; set; } = new();
}

public sealed class ManifestAsset
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;
}

public sealed class ManagerState
{
    // Legacy v0.1.x fields are kept for migration from the original shared-payload design.
    public string InstalledVersion { get; set; } = string.Empty;
    public DateTimeOffset? InstalledAt { get; set; }
    public DateTimeOffset? LastUpdatedAt { get; set; }
    public DateTimeOffset? LastVerifiedAt { get; set; }
    public string DiscordBranch { get; set; } = "auto";
    public string LastBackupPath { get; set; } = string.Empty;
    public Dictionary<string, ClientInstallState> Clients { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ClientInstallState
{
    public string InstalledVersion { get; set; } = string.Empty;
    public DateTimeOffset? InstalledAt { get; set; }
    public DateTimeOffset? LastUpdatedAt { get; set; }
    public DateTimeOffset? LastVerifiedAt { get; set; }
    public string LastBackupPath { get; set; } = string.Empty;
}

public sealed class ManagedInstallMetadata
{
    public string DistributionVersion { get; set; } = string.Empty;
    public string DiscordBranch { get; set; } = "auto";
    public DateTimeOffset PreparedAt { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
    public Dictionary<string, string> FileSha256 { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record DiscordInstallProbe(
    string Branch,
    string BaseDirectory,
    string ResourcesDirectory,
    string AppAsarPath,
    string BackupAsarPath,
    bool IsPatched,
    string PatchTarget,
    bool IsManagedPatch,
    bool IsBranchScopedManagedPatch,
    bool IsLegacyManagedPatch);

public sealed record OperationProgress(string Message, double? Percent = null);

public sealed record DiscordRestartTarget(string Branch, string UpdateExecutable, string ProcessExecutable);

public static class AppInfo
{
    public const string CurrentVersion = "0.1.5";
}
