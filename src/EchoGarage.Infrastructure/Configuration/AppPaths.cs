namespace EchoGarage.Infrastructure.Configuration;

public sealed class AppPaths
{
    public const string DataRootEnvironmentVariable = "ECHOGARAGE_DATA_DIR";

    public AppPaths(string? rootOverride = null)
    {
        var overrideRoot = string.IsNullOrWhiteSpace(rootOverride)
            ? Environment.GetEnvironmentVariable(DataRootEnvironmentVariable)
            : rootOverride;
        if (!string.IsNullOrWhiteSpace(overrideRoot))
        {
            var root = System.IO.Path.GetFullPath(Environment.ExpandEnvironmentVariables(overrideRoot.Trim()));
            ConfigDirectory = System.IO.Path.Combine(root, "config");
            DataDirectory = System.IO.Path.Combine(root, "data");
            DefaultCacheDirectory = System.IO.Path.Combine(DataDirectory, "Cache");
            LogsDirectory = System.IO.Path.Combine(DataDirectory, "Logs");
            SettingsFile = System.IO.Path.Combine(ConfigDirectory, "settings.json");
            DatabaseFile = System.IO.Path.Combine(DataDirectory, "library.db");
            IsPortableOverride = true;
            return;
        }

        ConfigDirectory = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EchoGarage");
        DataDirectory = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EchoGarage");
        DefaultCacheDirectory = System.IO.Path.Combine(DataDirectory, "Cache");
        LogsDirectory = System.IO.Path.Combine(DataDirectory, "Logs");
        SettingsFile = System.IO.Path.Combine(ConfigDirectory, "settings.json");
        DatabaseFile = System.IO.Path.Combine(DataDirectory, "library.db");
    }

    public string ConfigDirectory { get; }
    public string DataDirectory { get; }
    public string DefaultCacheDirectory { get; }
    public string LogsDirectory { get; }
    public string SettingsFile { get; }
    public string DatabaseFile { get; }

    /// <summary>True when ECHOGARAGE_DATA_DIR redirected all state to a custom root.</summary>
    public bool IsPortableOverride { get; }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(DefaultCacheDirectory);
    }

    public string ResolveCacheDirectory(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return DefaultCacheDirectory;
        }

        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(configured.Trim());
            var full = System.IO.Path.IsPathRooted(expanded)
                ? System.IO.Path.GetFullPath(expanded)
                : System.IO.Path.GetFullPath(System.IO.Path.Combine(DataDirectory, expanded));
            return full;
        }
        catch
        {
            return DefaultCacheDirectory;
        }
    }
}
