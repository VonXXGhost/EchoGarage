using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using EchoGarage.App.Services;
using EchoGarage.App.Views;

namespace EchoGarage.App;

public partial class App : Application
{
    private SingleInstanceGuard? _singleInstance;

    public static AppHost Host { get; private set; } = null!;
    public static StartupTimeline Timeline { get; } = new();

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Timeline.Mark("process-start");

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        _singleInstance = new SingleInstanceGuard();
        if (!_singleInstance.TryAcquire())
        {
            var forwarded = _singleInstance.ForwardToPrimary(e.Args);
            Shutdown(forwarded ? 0 : 1);
            return;
        }

        try
        {
            Host = new AppHost();
            await Host.InitializeAsync();
            ThemeManager.Apply(Host.Settings.Current.Theme);
            Host.Logger.Info($"Theme applied: {Host.Settings.Current.Theme}");
            Timeline.Mark("host-ready");

            var window = new MainWindow(Host);
            MainWindow = window;
            window.Show();
            Timeline.Mark("window-shown");
            Host.StartWarmup();

            _singleInstance.StartServer(paths =>
            {
                Dispatcher.BeginInvoke(new Action(() => window.HandleExternalFiles(paths)),
                    DispatcherPriority.Background);
            });

            var startupFiles = FileArgumentParser.ExtractAudioFiles(e.Args);
            if (startupFiles.Count > 0)
            {
                window.HandleExternalFiles(startupFiles);
            }

            var playIndex = Array.FindIndex(e.Args, a => string.Equals(a, "--play-file", StringComparison.OrdinalIgnoreCase));
            if (playIndex >= 0 && playIndex + 1 < e.Args.Length)
            {
                await window.ViewModel.PlayPathForDiagnosticsAsync(e.Args[playIndex + 1]);
            }

            var seekIndex = Array.FindIndex(e.Args, a => string.Equals(a, "--seek-seconds", StringComparison.OrdinalIgnoreCase));
            if (seekIndex >= 0 && seekIndex + 1 < e.Args.Length &&
                double.TryParse(e.Args[seekIndex + 1], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var seekSeconds))
            {
                await Task.Delay(1200);
                window.ViewModel.SeekForDiagnostics(seekSeconds);
            }

            var screenshotIndex = Array.FindIndex(e.Args, a => string.Equals(a, "--screenshot", StringComparison.OrdinalIgnoreCase));
            if (screenshotIndex >= 0 && screenshotIndex + 1 < e.Args.Length)
            {
                var screenshotPath = e.Args[screenshotIndex + 1];
                await Task.Delay(3200);
                window.SaveScreenshot(screenshotPath);
            }

            var allIndex = Array.FindIndex(e.Args, a => string.Equals(a, "--screenshot-all", StringComparison.OrdinalIgnoreCase));
            if (allIndex >= 0 && allIndex + 1 < e.Args.Length)
            {
                await Task.Delay(3200);
                await Diagnostics.CaptureAllAsync(Host, window, e.Args[allIndex + 1]);
            }

            Host.Logger.Info($"Startup complete in {Timeline.ElapsedSinceStart.TotalMilliseconds:F0} ms " +
                             $"(window shown at {Timeline.ElapsedAt("window-shown").TotalMilliseconds:F0} ms).");
        }
        catch (Exception ex)
        {
            Host?.Logger.Error("Fatal error during startup", ex);
            var defaults = new EchoGarage.Infrastructure.Configuration.AppPaths();
            try
            {
                var crashLog = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "EchoGarage-startup-error.log");
                System.IO.File.AppendAllText(crashLog,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] EchoGarage startup failed{Environment.NewLine}" +
                    $"Process: {Environment.ProcessPath}{Environment.NewLine}" +
                    $"User: {Environment.UserName} / {Environment.UserDomainName}{Environment.NewLine}" +
                    $"APPDATA={Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)}{Environment.NewLine}" +
                    $"LOCALAPPDATA={Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)}{Environment.NewLine}" +
                    $"TEMP={System.IO.Path.GetTempPath()}{Environment.NewLine}" +
                    $"{ex}{Environment.NewLine}{Environment.NewLine}");
            }
            catch
            {
                // Best effort only.
            }

            MessageBox.Show(
                $"EchoGarage 启动失败：\n{ex.Message}\n\n" +
                $"如果这是受限环境或便携使用，请设置环境变量 {EchoGarage.Infrastructure.Configuration.AppPaths.DataRootEnvironmentVariable} " +
                "指向一个可写目录后重试；也可以在 exe 旁新建名为 portable 的空文件强制便携模式（数据存到 exe 旁的 EchoGarageData）。\n\n" +
                $"当前默认配置目录：{Host?.Paths.ConfigDirectory ?? defaults.ConfigDirectory}\n" +
                $"当前默认数据目录：{Host?.Paths.DataDirectory ?? defaults.DataDirectory}\n\n" +
                $"详细错误已写入：{System.IO.Path.Combine(System.IO.Path.GetTempPath(), "EchoGarage-startup-error.log")}",
                "EchoGarage", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _singleInstance?.Dispose();
            Host?.Shutdown();
        }
        catch
        {
            // Never block shutdown.
        }

        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Host?.Logger.Error("Unhandled UI exception", e.Exception);
        e.Handled = true;
        MessageBox.Show($"发生未处理的错误：\n{e.Exception.Message}\n\n详细信息已写入日志：{Host?.Paths.LogsDirectory}",
            "EchoGarage", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            Host?.Logger.Error("Unhandled domain exception", ex);
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Host?.Logger.Error("Unobserved task exception", e.Exception);
        e.SetObserved();
    }
}

public sealed class StartupTimeline
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly Dictionary<string, TimeSpan> _marks = new(StringComparer.OrdinalIgnoreCase);

    public void Mark(string name)
    {
        _marks[name] = _stopwatch.Elapsed;
    }

    public TimeSpan ElapsedAt(string name) => _marks.TryGetValue(name, out var value) ? value : TimeSpan.Zero;

    public TimeSpan ElapsedSinceStart => _stopwatch.Elapsed;
}
