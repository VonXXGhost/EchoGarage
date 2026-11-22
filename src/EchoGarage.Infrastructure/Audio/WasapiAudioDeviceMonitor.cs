using EchoGarage.Core.Abstractions;
using EchoGarage.Core.Models;
using EchoGarage.Core.Services;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace EchoGarage.Infrastructure.Audio;

/// <summary>
/// WASAPI 输出设备热插拔监视器。
/// <para>
/// 专用 MTA 后台线程持有 <see cref="MMDeviceEnumerator"/> 并注册
/// <see cref="IMMNotificationClient"/>；端点变化只负责唤醒线程，线程去抖 250 ms 后重新枚举
/// 并与上一份快照比较，只有真正变化时才触发 <see cref="DevicesChanged"/>。
/// 另外每 10 s 做一次兜底重扫，防止个别驱动不发送端点通知。
/// </para>
/// <para>
/// 注意：NAudio 2.x 中通知客户端必须由强引用长期持有，否则 CCW 被 GC 回收后只会收到
/// 最初两条通知（NAudio issue #1021）。这里保存在 <see cref="_notificationClient"/> 字段中。
/// </para>
/// </summary>
public sealed class WasapiAudioDeviceMonitor : IAudioDeviceMonitor
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan FallbackPollInterval = TimeSpan.FromSeconds(10);

    private readonly IAppLogger _logger;
    private readonly object _gate = new();
    private readonly AutoResetEvent _wake = new(false);

    private Thread? _thread;
    private IMMNotificationClient? _notificationClient;
    private IReadOnlyList<AudioDeviceInfo> _lastSnapshot = [];
    private volatile bool _stopRequested;
    private bool _started;
    private bool _disposed;

    public WasapiAudioDeviceMonitor(IAppLogger logger)
    {
        _logger = logger;
    }

    public event EventHandler<AudioDevicesChangedEventArgs>? DevicesChanged;

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
            {
                return;
            }

            _started = true;
            _stopRequested = false;
            var thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "EchoGarage audio devices"
            };
            thread.SetApartmentState(ApartmentState.MTA);
            _thread = thread;
            thread.Start();
        }
    }

    public void Stop()
    {
        Thread? thread;
        lock (_gate)
        {
            if (!_started)
            {
                return;
            }

            _started = false;
            _stopRequested = true;
            thread = _thread;
            _thread = null;
            _wake.Set();
        }

        if (thread is not null && thread.IsAlive && !thread.Join(TimeSpan.FromSeconds(3)))
        {
            _logger.Warn("Audio device monitor did not stop within 3 s.");
        }
    }

    public void Dispose()
    {
        Stop();
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _wake.Dispose();
        }
    }

    private void Run()
    {
        MMDeviceEnumerator? enumerator = null;
        DeviceNotificationClient? client = null;
        try
        {
            enumerator = new MMDeviceEnumerator();
            client = new DeviceNotificationClient(this);
            var result = enumerator.RegisterEndpointNotificationCallback(client);
            if (result != 0)
            {
                _logger.Warn($"Registering audio endpoint notifications failed: HRESULT=0x{result:X8}");
            }

            // 强引用：见类注释（NAudio #1021）。
            _notificationClient = client;

            var snapshot = WasapiOutputDeviceEnumerator.GetActiveRenderDevices(enumerator);
            _lastSnapshot = snapshot;
            _logger.Info($"Audio device monitor started; initial output devices: {Describe(snapshot)}");
            RaiseDevicesChanged(snapshot, AudioDeviceChangeSet.Empty, isInitial: true);

            while (!_stopRequested)
            {
                var signaled = _wake.WaitOne(FallbackPollInterval);
                if (_stopRequested)
                {
                    break;
                }

                if (signaled)
                {
                    // 热插拔往往伴随一串端点事件（禁用 / 启用 / 默认设备切换），合并成一次重扫。
                    Thread.Sleep(DebounceDelay);
                    while (_wake.WaitOne(0))
                    {
                        // 排空去抖窗口内累计的唤醒信号。
                    }
                }

                try
                {
                    var current = WasapiOutputDeviceEnumerator.GetActiveRenderDevices(enumerator);
                    var changes = AudioDeviceChangeDetector.Compare(_lastSnapshot, current);
                    if (!changes.HasChanges)
                    {
                        continue;
                    }

                    _lastSnapshot = current;
                    _logger.Info($"Audio output devices changed: {DescribeChanges(changes)}; now: {Describe(current)}");
                    RaiseDevicesChanged(current, changes, isInitial: false);
                }
                catch (Exception ex)
                {
                    _logger.Warn($"Audio device rescan failed: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Audio device monitor stopped unexpectedly", ex);
        }
        finally
        {
            if (enumerator is not null)
            {
                if (client is not null)
                {
                    try
                    {
                        enumerator.UnregisterEndpointNotificationCallback(client);
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn($"Unregistering audio endpoint notifications failed: {ex.Message}");
                    }
                }

                try
                {
                    enumerator.Dispose();
                }
                catch
                {
                    // Ignore dispose errors during shutdown.
                }
            }

            lock (_gate)
            {
                _notificationClient = null;
                _thread = null;
                _started = false;
            }

            _logger.Info("Audio device monitor stopped.");
        }
    }

    private void RaiseDevicesChanged(
        IReadOnlyList<AudioDeviceInfo> devices,
        AudioDeviceChangeSet changes,
        bool isInitial)
    {
        try
        {
            DevicesChanged?.Invoke(this, new AudioDevicesChangedEventArgs
            {
                Devices = devices,
                Changes = changes,
                IsInitialSnapshot = isInitial
            });
        }
        catch (Exception ex)
        {
            // 订阅方的异常不能终止监视线程。
            _logger.Warn($"Audio device change handler failed: {ex.Message}");
        }
    }

    private void SignalFromEndpointCallback(string detail)
    {
        try
        {
            _logger.Debug($"Audio endpoint notification: {detail}");
            _wake.Set();
        }
        catch (ObjectDisposedException)
        {
            // 监视器已释放；忽略迟到的系统回调。
        }
        catch
        {
            // 通知线程上的异常不能影响音频服务。
        }
    }

    private static string Describe(IReadOnlyList<AudioDeviceInfo> devices)
    {
        if (devices.Count == 0)
        {
            return "(none)";
        }

        return string.Join("; ", devices.Select(d =>
            d.IsDefault ? $"{d.FriendlyName} [default]" : d.FriendlyName));
    }

    private static string DescribeChanges(AudioDeviceChangeSet changes)
    {
        var parts = new List<string>();
        if (changes.Added.Count > 0)
        {
            parts.Add("added=[" + string.Join(", ", changes.Added.Select(d => d.FriendlyName)) + "]");
        }

        if (changes.Removed.Count > 0)
        {
            parts.Add("removed=[" + string.Join(", ", changes.Removed.Select(d => d.FriendlyName)) + "]");
        }

        if (changes.Renamed.Count > 0)
        {
            parts.Add("renamed=[" + string.Join(", ", changes.Renamed.Select(d => d.FriendlyName)) + "]");
        }

        if (changes.DefaultChanged)
        {
            parts.Add($"default={changes.CurrentDefault?.FriendlyName ?? "(none)"}");
        }

        return parts.Count == 0 ? "(no changes)" : string.Join(", ", parts);
    }

    /// <summary>
    /// 转发系统端点通知：只做去抖唤醒，真正的设备枚举在监视线程上完成。
    /// 回调可能来自任意 COM 线程，因此这里只写日志 + 置位事件。
    /// </summary>
    private sealed class DeviceNotificationClient : IMMNotificationClient
    {
        private readonly WasapiAudioDeviceMonitor _owner;

        public DeviceNotificationClient(WasapiAudioDeviceMonitor owner)
        {
            _owner = owner;
        }

        public void OnDeviceStateChanged(string deviceId, DeviceState newState) =>
            _owner.SignalFromEndpointCallback($"state={newState}, device={deviceId}");

        public void OnDeviceAdded(string pwstrDeviceId) =>
            _owner.SignalFromEndpointCallback($"added, device={pwstrDeviceId}");

        public void OnDeviceRemoved(string deviceId) =>
            _owner.SignalFromEndpointCallback($"removed, device={deviceId}");

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (flow == DataFlow.Render && role == Role.Multimedia)
            {
                _owner.SignalFromEndpointCallback($"default render/multimedia={defaultDeviceId}");
            }
        }

        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
        {
            // 音量 / 属性变化非常频繁；设备名称变化由 10 s 兜底重扫覆盖，这里不唤醒。
        }
    }
}
