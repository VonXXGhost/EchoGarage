using EchoGarage.Core.Abstractions;
using EchoGarage.Core.Models;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace EchoGarage.Infrastructure.Audio;

/// <summary>
/// WASAPI playback through NAudio. Shared mode feeds the device mix format so the
/// OS mixer does no implicit conversion; exclusive mode negotiates the source format
/// and falls back through a small ladder of widely supported formats.
/// </summary>
public sealed class WasapiAudioPlayer : IAudioPlayer
{
    private readonly object _gate = new();
    private readonly IAppLogger _logger;
    private readonly IAudioSampleSink? _sampleSink;

    private WasapiOut? _output;
    private SharedAudioFileReader? _reader;
    private VolumeSampleProvider? _volumeProvider;
    private MMDevice? _device;
    private WaveFormat? _deviceMixFormat;
    private string? _deviceId;
    private OutputMode _mode = OutputMode.Shared;
    private AudioPlaybackState _state = AudioPlaybackState.Stopped;
    private string _outputDescription = "未初始化";
    private double _volume = 0.75;
    private bool _userStopRequested;
    private bool _disposed;

    /// <summary>当前输出实际使用的端点 ID（用于判断设备是否在播放中消失）。</summary>
    private string? _activeDeviceId;

    /// <summary>播放因输出设备消失而中断；设备回退 / 恢复后应继续播放。</summary>
    private bool _pendingResumeAfterDeviceLoss;

    /// <summary>有等待消费的输出重建请求（由后台 worker 执行，避免阻塞 UI 线程）。</summary>
    private bool _reconfigureRequested;

    private bool _reconfigureWorkerActive;

    public WasapiAudioPlayer(IAppLogger logger, IAudioSampleSink? sampleSink = null)
    {
        _logger = logger;
        _sampleSink = sampleSink;
    }

    public event EventHandler<AudioPlaybackStateChangedEventArgs>? StateChanged;
    public event EventHandler? PlaybackEnded;

    public AudioPlaybackState State => _state;

    /// <summary>
    /// UI 线程专用的无阻塞播放快照。加载 / 切换输出时 <c>_gate</c> 会被后台加载线程持有数百
    /// 毫秒（WASAPI 初始化、格式协商），进度计时器如果直接读 <see cref="State"/> /
    /// <see cref="Position"/> 会把 UI 线程一起堵住，表现为切歌时整个界面卡住一下。
    /// 这里用零超时的 <see cref="Monitor.TryEnter(object)"/>：锁被占用就返回 false，调用方跳过本帧。
    /// </summary>
    public bool TryReadPlaybackSnapshot(out AudioPlaybackState state, out TimeSpan position, out TimeSpan duration)
    {
        state = _state;
        position = TimeSpan.Zero;
        duration = TimeSpan.Zero;
        if (!Monitor.TryEnter(_gate))
        {
            return false;
        }

        try
        {
            state = _state;
            try
            {
                position = _reader?.CurrentTime ?? TimeSpan.Zero;
                duration = _reader?.TotalTime ?? TimeSpan.Zero;
            }
            catch
            {
                position = TimeSpan.Zero;
                duration = TimeSpan.Zero;
            }

            return true;
        }
        finally
        {
            Monitor.Exit(_gate);
        }
    }

    public TimeSpan Position
    {
        get
        {
            lock (_gate)
            {
                try
                {
                    return _reader?.CurrentTime ?? TimeSpan.Zero;
                }
                catch
                {
                    return TimeSpan.Zero;
                }
            }
        }
    }

    public TimeSpan Duration
    {
        get
        {
            lock (_gate)
            {
                try
                {
                    return _reader?.TotalTime ?? TimeSpan.Zero;
                }
                catch
                {
                    return TimeSpan.Zero;
                }
            }
        }
    }

    public string? CurrentTrackPath { get; private set; }

    public double Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 1);
            var provider = _volumeProvider;
            if (provider is not null)
            {
                provider.Volume = (float)_volume;
            }
        }
    }

    public string OutputDescription => _outputDescription;

    public IReadOnlyList<AudioDeviceInfo> GetOutputDevices()
    {
        try
        {
            return WasapiOutputDeviceEnumerator.GetActiveRenderDevices();
        }
        catch (Exception ex)
        {
            _logger.Error("Enumerating audio devices failed", ex);
            return [];
        }
    }

    public void Warmup()
    {
        try
        {
            NAudio.MediaFoundation.MediaFoundationApi.Startup();
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            _ = device.AudioClient.MixFormat;
            _logger.Info($"Audio warmup completed ({device.FriendlyName}).");
        }
        catch (Exception ex)
        {
            _logger.Warn($"Audio warmup failed: {ex.Message}");
        }
    }

    public void ConfigureOutput(string? deviceId, OutputMode mode)
    {
        var normalizedId = string.IsNullOrWhiteSpace(deviceId) ? null : deviceId.Trim();
        var scheduleRebuild = false;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var deviceChanged = !string.Equals(_deviceId, normalizedId, StringComparison.OrdinalIgnoreCase);
            var modeChanged = _mode != mode;
            _deviceId = normalizedId;
            _mode = mode;

            if (!deviceChanged && !modeChanged && !_pendingResumeAfterDeviceLoss && !_reconfigureRequested)
            {
                return;
            }

            if (_output is not null && _reader is not null)
            {
                // 正在播放 / 已加载：在后台线程重建输出，不能在 UI 线程同步等待 WASAPI 初始化。
                _reconfigureRequested = true;
                if (!_reconfigureWorkerActive)
                {
                    _reconfigureWorkerActive = true;
                    scheduleRebuild = true;
                }
            }
            else
            {
                // 没有活动输出时只需丢弃缓存的设备，下次播放按新配置重新获取。
                DisposeDevice();
                _pendingResumeAfterDeviceLoss = false;
            }
        }

        _logger.Info($"Audio output configured: device={normalizedId ?? "(default)"}, mode={mode}");
        if (scheduleRebuild)
        {
            _ = Task.Run(ReconfigureOutputWorker);
        }
    }

    /// <summary>
    /// 消费输出重建请求的串行 worker。重建期间（WASAPI 初始化 / 格式协商）可能又有新的
    /// 设备变化请求进来，这里循环处理，直到没有待办请求再退出。
    /// </summary>
    private void ReconfigureOutputWorker()
    {
        while (true)
        {
            lock (_gate)
            {
                if (_disposed || !_reconfigureRequested)
                {
                    _reconfigureWorkerActive = false;
                    return;
                }

                _reconfigureRequested = false;
                try
                {
                    RebuildOutputForConfiguration();
                }
                catch (Exception ex)
                {
                    _logger.Error("Rebuilding audio output failed", ex);
                    SetState(AudioPlaybackState.Stopped, CurrentTrackPath, ex.Message);
                }
            }
        }
    }

    /// <summary>
    /// 用当前 <c>_deviceId / _mode</c> 重建输出，并在原来处于播放 / 暂停（或设备丢失中断）时
    /// 从原位置继续。调用方必须持有 <c>_gate</c>。
    /// </summary>
    private void RebuildOutputForConfiguration()
    {
        if (_output is null || _reader is null)
        {
            DisposeDevice();
            _pendingResumeAfterDeviceLoss = false;
            return;
        }

        var wasPlaying = _state == AudioPlaybackState.Playing;
        var wasPaused = _state == AudioPlaybackState.Paused;
        var resumeAfterDeviceLoss = _pendingResumeAfterDeviceLoss;
        var path = CurrentTrackPath;
        var position = TimeSpan.Zero;
        try
        {
            position = _reader.CurrentTime;
        }
        catch
        {
            // 读取位置失败时退化为从开头继续。
        }

        var shouldResume = wasPlaying || wasPaused || resumeAfterDeviceLoss;
        TeardownOutput();
        if (!shouldResume)
        {
            return;
        }

        if (wasPaused && !wasPlaying && !resumeAfterDeviceLoss)
        {
            // 暂停状态不立即重建输出：下次 Play() 会按新配置启动。
            // 这样重建期间不会出声，进度也停在暂停点（WASAPI 初始化约 1 秒，
            // 若先 StartOutput 再 Pause，读取器会前移近一秒）。
            _logger.Info("Audio output released while paused; it will be recreated on resume.");
            return;
        }

        var reader = _reader;
        if (reader is null)
        {
            return;
        }

        try
        {
            StartOutput(reader, position, keepReaderPosition: true);
            SetState(AudioPlaybackState.Playing, path);
            _logger.Info($"Audio output rebuilt on {_outputDescription} at {position}.");
        }
        catch (Exception ex)
        {
            _logger.Error($"Rebuilding audio output on {_deviceId ?? "(default)"} failed", ex);
            SetState(AudioPlaybackState.Stopped, path, ex.Message);
        }
    }

    public Task LoadAsync(string path, bool playWhenReady, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                SetState(AudioPlaybackState.Loading, path);
                try
                {
                    TeardownOutput();
                    DisposeReader();
                    DisposeDevice();
                    CurrentTrackPath = path;

                    var reader = new SharedAudioFileReader(path);
                    _reader = reader;

                    if (playWhenReady)
                    {
                        StartOutput(reader, TimeSpan.Zero);
                        SetState(AudioPlaybackState.Playing, path);
                    }
                    else
                    {
                        SetState(AudioPlaybackState.Stopped, path);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error($"Failed to load {path}", ex);
                    TeardownOutput();
                    SetState(AudioPlaybackState.Stopped, path, ex.Message);
                    throw;
                }
            }
        }, cancellationToken);
    }

    public void Play()
    {
        lock (_gate)
        {
            if (_disposed || _reader is null)
            {
                return;
            }

            if (_output is not null)
            {
                _userStopRequested = false;
                _output.Play();
                SetState(AudioPlaybackState.Playing, CurrentTrackPath);
                return;
            }

            try
            {
                StartOutput(_reader, _reader.CurrentTime, keepReaderPosition: true);
                SetState(AudioPlaybackState.Playing, CurrentTrackPath);
            }
            catch (Exception ex)
            {
                _logger.Error("Playback start failed", ex);
                SetState(AudioPlaybackState.Stopped, CurrentTrackPath, ex.Message);
            }
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (_output is null || _state != AudioPlaybackState.Playing)
            {
                return;
            }

            _output.Pause();
            SetState(AudioPlaybackState.Paused, CurrentTrackPath);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _userStopRequested = true;
            TeardownOutput();
            if (_reader is not null)
            {
                try
                {
                    _reader.CurrentTime = TimeSpan.Zero;
                }
                catch
                {
                    // Ignore seek errors on stop.
                }
            }

            SetState(AudioPlaybackState.Stopped, CurrentTrackPath);
        }
    }

    public void Seek(TimeSpan position)
    {
        lock (_gate)
        {
            if (_reader is null)
            {
                return;
            }

            var target = position < TimeSpan.Zero ? TimeSpan.Zero : position;
            if (target > _reader.TotalTime)
            {
                target = _reader.TotalTime;
            }

            var wasPlaying = _state == AudioPlaybackState.Playing;
            _logger.Debug(
                $"Seek request: target={target:mm\\:ss\\.fff} reader={_reader.CurrentTime:mm\\:ss\\.fff} " +
                $"total={_reader.TotalTime:mm\\:ss\\.fff} wasPlaying={wasPlaying} " +
                $"output={(_output is null ? "none" : _output.PlaybackState.ToString())}");
            try
            {
                // Pause/resume keeps the output alive: rebuilding WasapiOut on every seek costs
                // about a second and makes the jump feel like a restart.
                if (wasPlaying && _output is not null)
                {
                    _output.Pause();
                }
                else if (wasPlaying)
                {
                    TeardownOutput();
                }

                _reader.CurrentTime = target;
                _sampleSink?.Reset();
                _logger.Debug(
                    $"Seek reader repositioned: reader={_reader.CurrentTime:mm\\:ss\\.fff} " +
                    $"output={(_output?.PlaybackState.ToString() ?? "none")}");

                if (wasPlaying)
                {
                    if (_output is not null)
                    {
                        _output.Play();
                    }
                    else
                    {
                        StartOutput(_reader, target, keepReaderPosition: true);
                    }

                    SetState(AudioPlaybackState.Playing, CurrentTrackPath);
                    _logger.Debug(
                        $"Seek resume: output={(_output?.PlaybackState.ToString() ?? "none")} " +
                        $"reader={_reader.CurrentTime:mm\\:ss\\.fff}");
                }

                _logger.Info($"Seeked to {target:mm\\:ss\\.fff} (playing={wasPlaying}).");
            }
            catch (Exception ex)
            {
                _logger.Error($"Seek to {target} failed", ex);
                SetState(AudioPlaybackState.Stopped, CurrentTrackPath, ex.Message);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            TeardownOutput();
            DisposeReader();
            DisposeDevice();
        }
    }

    private void StartOutput(SharedAudioFileReader reader, TimeSpan position, bool keepReaderPosition = false)
    {
        if (!keepReaderPosition && position > TimeSpan.Zero)
        {
            reader.CurrentTime = position;
        }

        var device = GetOrCreateDevice();
        var formats = BuildTargetFormats(device, reader.WaveFormat);
        Exception? lastError = null;

        foreach (var (format, description) in formats)
        {
            WasapiOut? output = null;
            try
            {
                var sampleProvider = Convert(reader, format.SampleRate, format.Channels);
                if (_sampleSink is not null)
                {
                    // 可视化抽头（ADR-023 §6）：接在重采样 / 声道转换之后、音量之前，
                    // 静音时仍然有数据（ADR-012 要求自动化测试静音运行）。
                    _sampleSink.Configure(format.SampleRate, format.Channels);
                    sampleProvider = new SampleTapProvider(sampleProvider, _sampleSink);
                }

                _volumeProvider = new VolumeSampleProvider(sampleProvider) { Volume = (float)_volume };
                IWaveProvider waveProvider = format.Encoding == WaveFormatEncoding.IeeeFloat
                    ? new SampleToWaveProvider(_volumeProvider)
                    : new SampleToWaveProvider16(_volumeProvider);

                output = new WasapiOut(device, _mode == OutputMode.Exclusive
                    ? AudioClientShareMode.Exclusive
                    : AudioClientShareMode.Shared, true, 100);
                output.PlaybackStopped += OnPlaybackStopped;
                output.Init(waveProvider);
                output.Play();
                _output = output;
                _userStopRequested = false;
                _activeDeviceId = device.ID;
                _outputDescription = $"{device.FriendlyName} · {(_mode == OutputMode.Exclusive ? "WASAPI 独占" : "共享")} · {description}";
                _logger.Info($"Playback started on {_outputDescription} for {CurrentTrackPath}");
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                if (output is not null)
                {
                    output.PlaybackStopped -= OnPlaybackStopped;
                    try
                    {
                        output.Dispose();
                    }
                    catch
                    {
                        // Ignore cleanup errors.
                    }
                }

                _volumeProvider = null;
                _logger.Debug($"Output format {description} rejected: {ex.Message}");
            }
        }

        throw new InvalidOperationException(
            $"无法使用当前输出设备播放该文件：{lastError?.Message ?? "未知错误"}", lastError);
    }

    private IEnumerable<(WaveFormat Format, string Description)> BuildTargetFormats(MMDevice device, WaveFormat sourceFormat)
    {
        var channels = Math.Clamp(sourceFormat.Channels, 1, 2);
        if (_mode == OutputMode.Shared)
        {
            var mix = device.AudioClient.MixFormat;
            _deviceMixFormat = mix;
            yield return (mix, $"混音格式 {mix.SampleRate / 1000.0:0.#}kHz/{mix.Channels}ch");
            yield break;
        }

        // Exclusive mode: prefer the source rate, then common device rates, float first.
        var rates = new List<int> { sourceFormat.SampleRate };
        foreach (var rate in new[] { 48000, 44100, 96000, 32000 })
        {
            if (!rates.Contains(rate))
            {
                rates.Add(rate);
            }
        }

        foreach (var rate in rates)
        {
            yield return (WaveFormat.CreateIeeeFloatWaveFormat(rate, channels), $"{rate / 1000.0:0.#}kHz float");
        }

        foreach (var rate in rates)
        {
            yield return (new WaveFormat(rate, 16, channels), $"{rate / 1000.0:0.#}kHz 16bit");
        }
    }

    private static ISampleProvider Convert(ISampleProvider input, int sampleRate, int channels)
    {
        var result = input;
        if (result.WaveFormat.SampleRate != sampleRate)
        {
            result = new WdlResamplingSampleProvider(result, sampleRate);
        }

        if (result.WaveFormat.Channels != channels)
        {
            result = new ChannelConversionSampleProvider(result, channels);
        }

        return result;
    }

    private MMDevice GetOrCreateDevice()
    {
        if (_device is not null)
        {
            return _device;
        }

        var enumerator = new MMDeviceEnumerator();
        try
        {
            if (!string.IsNullOrWhiteSpace(_deviceId))
            {
                try
                {
                    _device = enumerator.GetDevice(_deviceId);
                }
                catch (Exception ex)
                {
                    _logger.Warn($"Configured output device not found ({_deviceId}): {ex.Message}. Falling back to default.");
                    _device = null;
                }
            }

            _device ??= enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return _device;
        }
        finally
        {
            enumerator.Dispose();
        }
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        bool raiseEnded;
        string? error = null;
        lock (_gate)
        {
            // 重建输出时旧 WasapiOut 可能迟到回调；只处理当前输出的通知。
            if (!ReferenceEquals(sender, _output))
            {
                return;
            }

            if (_userStopRequested)
            {
                return;
            }

            if (e.Exception is not null)
            {
                // 输出设备被拔出 / 禁用时 WasapiOut 会以错误结束；标记为待恢复，
                // 等设备监视器回调触发重建后用新的默认设备从原位置继续播放。
                _pendingResumeAfterDeviceLoss = _reader is not null &&
                    !WasapiOutputDeviceEnumerator.IsDeviceActive(_activeDeviceId);
                _logger.Error("Playback stopped with an error", e.Exception);
                error = e.Exception.Message;
            }

            _state = AudioPlaybackState.Stopped;
            raiseEnded = e.Exception is null;
        }

        StateChanged?.Invoke(this, new AudioPlaybackStateChangedEventArgs
        {
            State = AudioPlaybackState.Stopped,
            TrackPath = CurrentTrackPath,
            Error = error
        });

        if (raiseEnded)
        {
            PlaybackEnded?.Invoke(this, EventArgs.Empty);
        }
    }

    private void TeardownOutput()
    {
        _sampleSink?.Reset();
        var output = _output;
        _output = null;
        _volumeProvider = null;
        _activeDeviceId = null;
        _pendingResumeAfterDeviceLoss = false;
        if (output is not null)
        {
            output.PlaybackStopped -= OnPlaybackStopped;
            try
            {
                output.Stop();
            }
            catch
            {
                // Ignore stop errors during teardown.
            }

            try
            {
                output.Dispose();
            }
            catch
            {
                // Ignore dispose errors during teardown.
            }
        }

        // NAudio's WasapiOut takes ownership of the MMDevice and releases it on Dispose;
        // a cached device would throw E_NOINTERFACE on the next AudioClient access.
        DisposeDevice();
    }

    private void DisposeReader()
    {
        var reader = _reader;
        _reader = null;
        if (reader is not null)
        {
            try
            {
                reader.Dispose();
            }
            catch
            {
                // Ignore dispose errors.
            }
        }
    }

    private void DisposeDevice()
    {
        if (_device is not null)
        {
            try
            {
                _device.Dispose();
            }
            catch
            {
                // Ignore.
            }

            _device = null;
        }

        _deviceMixFormat = null;
    }

    private void SetState(AudioPlaybackState state, string? path, string? error = null)
    {
        _state = state;
        StateChanged?.Invoke(this, new AudioPlaybackStateChangedEventArgs
        {
            State = state,
            TrackPath = path,
            Error = error
        });
    }

    /// <summary>
    /// Converts any channel count to the requested count (mono upmix, stereo downmix,
    /// or first-channel extraction for multichannel sources).
    /// </summary>
    private sealed class ChannelConversionSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly int _sourceChannels;
        private readonly int _targetChannels;
        private float[] _buffer = [];

        public ChannelConversionSampleProvider(ISampleProvider source, int targetChannels)
        {
            _source = source;
            _sourceChannels = source.WaveFormat.Channels;
            _targetChannels = targetChannels;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, targetChannels);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            var framesRequested = count / _targetChannels;
            if (framesRequested == 0)
            {
                return 0;
            }

            var sourceSamples = framesRequested * _sourceChannels;
            if (_buffer.Length < sourceSamples)
            {
                _buffer = new float[sourceSamples];
            }

            var read = _source.Read(_buffer, 0, sourceSamples);
            var framesRead = read / _sourceChannels;
            var written = 0;
            for (var frame = 0; frame < framesRead; frame++)
            {
                var baseIndex = frame * _sourceChannels;
                if (_targetChannels == 1)
                {
                    if (_sourceChannels == 1)
                    {
                        buffer[offset + written++] = _buffer[baseIndex];
                    }
                    else
                    {
                        var sum = 0f;
                        for (var c = 0; c < _sourceChannels; c++)
                        {
                            sum += _buffer[baseIndex + c];
                        }

                        buffer[offset + written++] = sum / _sourceChannels;
                    }
                }
                else if (_targetChannels == 2)
                {
                    if (_sourceChannels == 1)
                    {
                        var value = _buffer[baseIndex];
                        buffer[offset + written++] = value;
                        buffer[offset + written++] = value;
                    }
                    else
                    {
                        buffer[offset + written++] = _buffer[baseIndex];
                        buffer[offset + written++] = _buffer[baseIndex + 1];
                    }
                }
                else
                {
                    for (var c = 0; c < _targetChannels; c++)
                    {
                        buffer[offset + written++] = c < _sourceChannels ? _buffer[baseIndex + c] : 0f;
                    }
                }
            }

            return written;
        }
    }
}
