using System.Text;
using EchoGarage.Core.Abstractions;
using EchoGarage.Infrastructure.Configuration;

namespace EchoGarage.Infrastructure.Logging;

public sealed class FileLogger : IAppLogger
{
    private readonly object _gate = new();
    private readonly string _logDirectory;
    private string _currentFile = string.Empty;

    public FileLogger(AppPaths paths)
    {
        _logDirectory = paths.LogsDirectory;
        Directory.CreateDirectory(_logDirectory);
        PruneOldLogs();
        Info($"EchoGarage {typeof(FileLogger).Assembly.GetName().Version} logger started. OS={Environment.OSVersion.VersionString}, Runtime={Environment.Version}");
    }

    public string LogDirectory => _logDirectory;

    public void Info(string message) => Write("INFO", message, null);
    public void Warn(string message) => Write("WARN", message, null);
    public void Debug(string message) => Write("DEBUG", message, null);
    public void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private void Write(string level, string message, Exception? exception)
    {
        try
        {
            lock (_gate)
            {
                var now = DateTime.Now;
                var file = System.IO.Path.Combine(_logDirectory, $"echogarage-{now:yyyyMMdd}.log");
                if (!string.Equals(file, _currentFile, StringComparison.OrdinalIgnoreCase))
                {
                    _currentFile = file;
                }

                var builder = new StringBuilder(256);
                builder.Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                    .Append(" [").Append(level).Append("] [T")
                    .Append(Environment.CurrentManagedThreadId).Append("] ")
                    .Append(message);
                if (exception is not null)
                {
                    builder.AppendLine().Append(exception);
                }

                builder.AppendLine();
                File.AppendAllText(file, builder.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never become the reason the player fails.
        }
    }

    private void PruneOldLogs()
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-30);
            foreach (var file in Directory.EnumerateFiles(_logDirectory, "echogarage-*.log"))
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        catch
        {
            // Best effort.
        }
    }
}
