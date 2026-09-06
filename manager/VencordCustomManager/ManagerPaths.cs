namespace VencordCustomManager;

public static class ManagerPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NightPlayProject",
        "VencordCustomPlugins");

    // v0.1.0-v0.1.2 used one shared payload for every Discord channel. Keep the
    // path readable so existing installs can be migrated one client at a time.
    public static string LegacyInstallDirectory => Path.Combine(Root, "current");
    public static string ClientsDirectory => Path.Combine(Root, "clients");
    public static string BackupsDirectory => Path.Combine(Root, "backups");
    public static string CacheDirectory => Path.Combine(Root, "cache");
    public static string StagingDirectory => Path.Combine(Root, "staging");
    public static string StateFile => Path.Combine(Root, "manager-state.json");
    public static string LegacyInstallMetadataFile => Path.Combine(LegacyInstallDirectory, ".manager-install.json");
    public static string PluginsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Vencord",
        "plugins");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ClientsDirectory);
        Directory.CreateDirectory(BackupsDirectory);
        Directory.CreateDirectory(CacheDirectory);
        Directory.CreateDirectory(StagingDirectory);
        Directory.CreateDirectory(PluginsDirectory);
    }

    public static string GetInstallDirectory(string branch) =>
        Path.Combine(ClientsDirectory, NormalizeExactBranch(branch));

    public static string GetInstallMetadataFile(string branch) =>
        Path.Combine(GetInstallDirectory(branch), ".manager-install.json");

    public static string GetBackupsDirectory(string branch) =>
        Path.Combine(BackupsDirectory, NormalizeExactBranch(branch));

    public static string GetPendingOperationFile(string branch) =>
        Path.Combine(Root, $"pending-{NormalizeExactBranch(branch)}.json");

    private static string NormalizeExactBranch(string branch) => branch.ToLowerInvariant() switch
    {
        "stable" => "stable",
        "ptb" => "ptb",
        "canary" => "canary",
        _ => throw new ArgumentOutOfRangeException(nameof(branch), "A specific Discord client is required.")
    };

    public static void EnsureManagedPath(string path)
    {
        var root = Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(path);
        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Refusing to modify a path outside the manager directory: {candidate}");
    }
}
