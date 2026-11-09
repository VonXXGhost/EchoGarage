namespace EchoGarage.Infrastructure.Configuration;

/// <summary>
/// EchoGarage is portable by default: configuration, library, cache and logs live in
/// <c>EchoGarageData</c> next to the executable. <c>ECHOGARAGE_DATA_DIR</c> can redirect the
/// whole root elsewhere (tests, shared drives, environments where the program folder is read-only).
/// </summary>
public sealed class AppPaths
{
    public const string DataRootEnvironmentVariable = "ECHOGARAGE_DATA_DIR";
    public const string PortableDataFolderName = "EchoGarageData";

    public AppPaths(string? rootOverride = null)
    {
        var customRoot = string.IsNullOrWhiteSpace(rootOverride)
            ? Environment.GetEnvironmentVariable(DataRootEnvironmentVariable)
            : rootOverride;

        if (!string.IsNullOrWhiteSpace(customRoot))
        {
            RootDirectory = System.IO.Path.GetFullPath(Environment.ExpandEnvironmentVariables(customRoot.Trim()));
            IsCustomRoot = true;
        }
        else
        {
            var exeDirectory = System.IO.Path.GetDirectoryName(Environment.ProcessPath);
            if (string.IsNullOrWhiteSpace(exeDirectory))
            {
                exeDirectory = AppContext.BaseDirectory;
            }

            RootDirectory = System.IO.Path.Combine(exeDirectory, PortableDataFolderName);
            IsCustomRoot = false;
        }

        ConfigDirectory = System.IO.Path.Combine(RootDirectory, "config");
        DataDirectory = System.IO.Path.Combine(RootDirectory, "data");
        DefaultCacheDirectory = System.IO.Path.Combine(DataDirectory, "Cache");
        LogsDirectory = System.IO.Path.Combine(DataDirectory, "Logs");
        SettingsFile = System.IO.Path.Combine(ConfigDirectory, "settings.json");
        DatabaseFile = System.IO.Path.Combine(DataDirectory, "library.db");
    }

    /// <summary>Root folder that contains <c>config</c> and <c>data</c>.</summary>
    public string RootDirectory { get; }

    /// <summary>True when ECHOGARAGE_DATA_DIR redirected the root away from the program folder.</summary>
    public bool IsCustomRoot { get; }

    /// <summary>True for the default layout: everything stored next to the executable.</summary>
    public bool IsPortable => !IsCustomRoot;

    public string ConfigDirectory { get; }
    public string DataDirectory { get; }
    public string DefaultCacheDirectory { get; }
    public string LogsDirectory { get; }
    public string SettingsFile { get; }
    public string DatabaseFile { get; }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(RootDirectory);
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
