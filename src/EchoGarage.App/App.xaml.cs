using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using EchoGarage.App.Services;
using EchoGarage.App.Views;
using EchoGarage.Core.Models;

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
        AppIdentity.Apply();

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

            // ADR-015 诊断开关只影响本次进程，因此通过构造函数下发，不改写 Settings.SeekBarMode。
            var waveformDiagnostics = Array.Exists(e.Args,
                a => string.Equals(a, "--waveform", StringComparison.OrdinalIgnoreCase));
            // 文件关联 / 命令行指定文件启动时，窗口在初始化完成后再加载这些文件，
            // 从而跳过“恢复上次播放列表并选中上次曲目”的行为。
            var startupFiles = FileArgumentParser.ExtractAudioFiles(e.Args);
            var window = new MainWindow(Host, waveformDiagnostics, startupFiles);
            MainWindow = window;
            window.Show();
            Timeline.Mark("window-shown");
            Host.StartWarmup();

            _singleInstance.StartServer(paths =>
            {
                Dispatcher.BeginInvoke(new Action(() => window.HandleExternalFiles(paths)),
                    DispatcherPriority.Background);
            });

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

            var searchIndex = Array.FindIndex(e.Args, a => string.Equals(a, "--search", StringComparison.OrdinalIgnoreCase));
            if (searchIndex >= 0 && searchIndex + 1 < e.Args.Length)
            {
                // Diagnostic switch: verifies the list filter without injecting keyboard input.
                window.ViewModel.FilterText = e.Args[searchIndex + 1];
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

            var waveformIndex = Array.FindIndex(e.Args, a => string.Equals(a, "--waveform", StringComparison.OrdinalIgnoreCase));
            if (waveformIndex >= 0 && waveformIndex + 2 < e.Args.Length)
            {
                // ADR-015 诊断开关：--waveform <音频文件> <png>
                // 播放文件 → 等波形分析完成 → 跳到 40% 截图；无需注入鼠标键盘。
                var waveformFile = e.Args[waveformIndex + 1];
                var waveformScreenshot = e.Args[waveformIndex + 2];
                await window.ViewModel.PlayPathForDiagnosticsAsync(waveformFile);

                var deadline = DateTime.UtcNow.AddSeconds(20);
                while (DateTime.UtcNow < deadline && window.ViewModel.CurrentWaveform is not { IsComplete: true })
                {
                    await Task.Delay(150);
                }

                var peaks = window.ViewModel.CurrentWaveform;
                Host.Logger.Info(
                    $"Waveform diagnostic: file={waveformFile} columns={peaks?.ColumnCount ?? 0} " +
                    $"valid={peaks?.ValidCount ?? 0} complete={peaks?.IsComplete == true} " +
                    $"duration={window.ViewModel.DurationSeconds:F1}s");
                if (window.ViewModel.DurationSeconds > 0)
                {
                    window.ViewModel.SeekForDiagnostics(window.ViewModel.DurationSeconds * 0.4);
                }

                await Task.Delay(700);
                window.SaveScreenshot(waveformScreenshot);
            }

            var visualizerIndex = Array.FindIndex(e.Args,
                a => string.Equals(a, "--visualizer", StringComparison.OrdinalIgnoreCase));
            if (visualizerIndex >= 0 && visualizerIndex + 2 < e.Args.Length)
            {
                // ADR-023 诊断开关：--visualizer <音频文件> <png> [秒数]
                // 播放文件 → 等若干秒后统计渲染帧率与每帧耗时 → 截图；无需注入鼠标键盘。
                var visualizerFile = e.Args[visualizerIndex + 1];
                var visualizerScreenshot = e.Args[visualizerIndex + 2];
                var seconds = visualizerIndex + 3 < e.Args.Length &&
                              double.TryParse(e.Args[visualizerIndex + 3],
                                  System.Globalization.NumberStyles.Float,
                                  System.Globalization.CultureInfo.InvariantCulture, out var requestedSeconds)
                    ? Math.Clamp(requestedSeconds, 0.5, 30)
                    : 3.0;

                await window.ViewModel.PlayPathForDiagnosticsAsync(visualizerFile);
                await Task.Delay(600);
                window.Visualizer.ResetRenderStatistics();
                await Task.Delay(TimeSpan.FromSeconds(seconds));

                var visualizerStats = Host.Spectrum.Stats;
                var renderFrames = window.Visualizer.RenderedFrames;
                var measuredSeconds = window.Visualizer.MeasuredSeconds;
                Host.Logger.Info(
                    $"Visualizer diagnostic: file={visualizerFile} sampled={seconds:F1}s measured={measuredSeconds:F2}s " +
                    $"renderFrames={renderFrames} fps={window.Visualizer.RenderedFramesPerSecond:F1} " +
                    $"ticks={window.Visualizer.RenderingTicks} tickFps={window.Visualizer.RenderingFramesPerSecond:F1} " +
                    $"renderAvg={window.Visualizer.AverageRenderMilliseconds:F3} ms " +
                    $"renderMax={window.Visualizer.MaxRenderMilliseconds:F3} ms " +
                    $"analyzerFrames={visualizerStats.Frames} " +
                    $"analyzerAvg={visualizerStats.AverageMilliseconds:F3} ms " +
                    $"analyzerMax={visualizerStats.MaxMilliseconds:F3} ms " +
                    $"rate={visualizerStats.SampleRate}Hz bands={visualizerStats.BandCount}");
                window.SaveScreenshot(visualizerScreenshot);
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
                "EchoGarage 默认把配置和数据放在 exe 旁的 EchoGarageData 目录。" +
                "当前程序目录不可写（例如放在 Program Files 下）时，请把整个程序文件夹移动到可写目录（如 D:\\Apps\\EchoGarage），" +
                $"或设置环境变量 {EchoGarage.Infrastructure.Configuration.AppPaths.DataRootEnvironmentVariable} 指向一个可写目录后重试。\n\n" +
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
