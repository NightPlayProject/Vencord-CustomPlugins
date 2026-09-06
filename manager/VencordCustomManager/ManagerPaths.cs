namespace VencordCustomManager;

public static class ManagerPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NightPlayProject",
        "VencordCustomPlugins");

    public static string InstallDirectory => Path.Combine(Root, "current");
    public static string BackupsDirectory => Path.Combine(Root, "backups");
    public static string CacheDirectory => Path.Combine(Root, "cache");
    public static string StagingDirectory => Path.Combine(Root, "staging");
    public static string StateFile => Path.Combine(Root, "manager-state.json");
    public static string InstallMetadataFile => Path.Combine(InstallDirectory, ".manager-install.json");
    public static string PluginsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Vencord",
        "plugins");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(BackupsDirectory);
        Directory.CreateDirectory(CacheDirectory);
        Directory.CreateDirectory(StagingDirectory);
        Directory.CreateDirectory(PluginsDirectory);
    }

    public static void EnsureManagedPath(string path)
    {
        var root = Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(path);
        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Refusing to modify a path outside the manager directory: {candidate}");
    }
}
