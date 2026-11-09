using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace EchoGarage.App.Services;

/// <summary>
/// Ensures a single running instance and forwards file-open requests from Explorer.
/// Falls back gracefully when the process cannot take the mutex (for example when the
/// whole app is started twice at the same moment) and when pipes are unavailable.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private const string MutexName = @"Local\EchoGarage.SingleInstance";
    private const string PipeNamePrefix = "EchoGarage.Ipc.";

    private Mutex? _mutex;
    private CancellationTokenSource? _serverCts;

    public bool TryAcquire()
    {
        try
        {
            _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
            if (createdNew)
            {
                return true;
            }

            _mutex.Dispose();
            _mutex = null;
            return false;
        }
        catch
        {
            // If the mutex cannot be created we prefer starting rather than refusing to run.
            return true;
        }
    }

    public bool ForwardToPrimary(IReadOnlyList<string> args)
    {
        var files = FileArgumentParser.ExtractAudioFiles(args);
        if (files.Count == 0)
        {
            return true;
        }

        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(1500);
            using var writer = new StreamWriter(client, Encoding.UTF8);
            foreach (var file in files)
            {
                writer.WriteLine(file);
            }

            writer.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void StartServer(Action<IReadOnlyList<string>> onFilesReceived)
    {
        _serverCts = new CancellationTokenSource();
        _ = Task.Run(() => ServerLoopAsync(onFilesReceived, _serverCts.Token));
    }

    private async Task ServerLoopAsync(Action<IReadOnlyList<string>> onFilesReceived, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(cancellationToken);
                using var reader = new StreamReader(server, Encoding.UTF8);
                var lines = new List<string>();
                string? line;
                while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        lines.Add(line);
                    }
                }

                if (lines.Count > 0)
                {
                    onFilesReceived(lines);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                await Task.Delay(500, cancellationToken).ContinueWith(_ => { }, TaskScheduler.Default);
            }
        }
    }

    public void Dispose()
    {
        try
        {
            _serverCts?.Cancel();
            _serverCts?.Dispose();
            _serverCts = null;
        }
        catch
        {
            // Ignore.
        }

        if (_mutex is not null)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch
            {
                // Not owned anymore.
            }

            _mutex.Dispose();
            _mutex = null;
        }
    }

    private static string PipeName
    {
        get
        {
            var identity = WindowsIdentity.GetCurrent().User?.Value ?? "default";
            var hash = SHA1.HashData(Encoding.UTF8.GetBytes(identity));
            return PipeNamePrefix + Convert.ToHexString(hash)[..12];
        }
    }
}

public static class FileArgumentParser
{
    public static IReadOnlyList<string> ExtractAudioFiles(IReadOnlyList<string> args)
    {
        var result = new List<string>();
        if (args.Count == 0)
        {
            return result;
        }

        var skipNext = false;
        foreach (var arg in args)
        {
            if (string.IsNullOrWhiteSpace(arg))
            {
                continue;
            }

            if (skipNext)
            {
                skipNext = false;
                continue;
            }

            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                skipNext = string.Equals(arg, "--screenshot", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(arg, "--screenshot-all", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(arg, "--play-file", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(arg, "--seek-seconds", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (arg.StartsWith("/", StringComparison.Ordinal) && arg.Length <= 3)
            {
                continue;
            }

            var path = arg.Trim('"');
            try
            {
                path = Path.GetFullPath(path);
            }
            catch
            {
                continue;
            }

            if (Directory.Exists(path))
            {
                result.AddRange(Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                    .Where(CoreServices.IsSupportedAudio));
            }
            else if (File.Exists(path) && CoreServices.IsSupportedAudio(path))
            {
                result.Add(path);
            }
        }

        return result;
    }

    private static class CoreServices
    {
        public static bool IsSupportedAudio(string path) => EchoGarage.Core.Services.PathUtil.IsSupportedAudio(path);
    }
}
