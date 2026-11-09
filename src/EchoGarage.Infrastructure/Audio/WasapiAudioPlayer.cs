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

    private WasapiOut? _output;
    private AudioFileReader? _reader;
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

    public WasapiAudioPlayer(IAppLogger logger)
    {
        _logger = logger;
    }

    public event EventHandler<AudioPlaybackStateChangedEventArgs>? StateChanged;
    public event EventHandler? PlaybackEnded;

    public AudioPlaybackState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
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
        var list = new List<AudioDeviceInfo>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var defaultId = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID;
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                list.Add(new AudioDeviceInfo(device.ID, device.FriendlyName,
                    string.Equals(device.ID, defaultId, StringComparison.OrdinalIgnoreCase)));
                device.Dispose();
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Enumerating audio devices failed", ex);
        }

        return list;
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
        lock (_gate)
        {
            _deviceId = deviceId;
            _mode = mode;
            DisposeDevice();
        }

        _logger.Info($"Audio output configured: device={deviceId ?? "(default)"}, mode={mode}");
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

                    var reader = new AudioFileReader(path);
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
            try
            {
                if (wasPlaying)
                {
                    // Rebuild the graph so already-buffered audio does not replay after the jump.
                    TeardownOutput();
                }

                _reader.CurrentTime = target;

                if (wasPlaying)
                {
                    StartOutput(_reader, target, keepReaderPosition: true);
                    SetState(AudioPlaybackState.Playing, CurrentTrackPath);
                }
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

    private void StartOutput(AudioFileReader reader, TimeSpan position, bool keepReaderPosition = false)
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
            if (_userStopRequested)
            {
                return;
            }

            if (e.Exception is not null)
            {
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
        var output = _output;
        _output = null;
        _volumeProvider = null;
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
