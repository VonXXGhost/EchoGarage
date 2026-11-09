namespace EchoGarage.Infrastructure.Configuration;

public sealed class AppPaths
{
    public const string DataRootEnvironmentVariable = "ECHOGARAGE_DATA_DIR";
    public const string PortableMarkerFileName = "portable";
    public const string PortableDataFolderName = "EchoGarageData";

    /// <summary>
    /// Resolves the data root for a normal start:
    /// 1. a <c>portable</c> marker next to the executable forces portable mode;
    /// 2. otherwise the standard AppData folders;
    /// 3. if those cannot be created (locked-down profile, sandbox, policy), fall back to
    ///    <c>EchoGarageData</c> next to the executable and then to TEMP, so the app still starts.
    /// </summary>
    public static AppPaths CreateForStartup()
    {
        var exeDirectory = System.IO.Path.GetDirectoryName(Environment.ProcessPath);

        if (!string.IsNullOrWhiteSpace(exeDirectory) &&
            File.Exists(System.IO.Path.Combine(exeDirectory, PortableMarkerFileName)))
        {
            var portable = new AppPaths(System.IO.Path.Combine(exeDirectory, PortableDataFolderName));
            try
            {
                portable.EnsureCreated();
                return portable;
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (IOException)
            {
            }
        }

        var defaults = new AppPaths();
        try
        {
            defaults.EnsureCreated();
            return defaults;
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (IOException)
        {
        }

        if (!string.IsNullOrWhiteSpace(exeDirectory))
        {
            var besideExe = new AppPaths(System.IO.Path.Combine(exeDirectory, PortableDataFolderName));
            try
            {
                besideExe.EnsureCreated();
                return besideExe;
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (IOException)
            {
            }
        }

        var temp = new AppPaths(System.IO.Path.Combine(System.IO.Path.GetTempPath(), PortableDataFolderName));
        try
        {
            temp.EnsureCreated();
            return temp;
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (IOException)
        {
        }

        return defaults;
    }

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
