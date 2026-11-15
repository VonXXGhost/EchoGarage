using System.Diagnostics;
using EchoGarage.Core.Abstractions;
using EchoGarage.Core.Models;
using EchoGarage.Core.Services;

namespace EchoGarage.Infrastructure.Audio;

/// <summary>
/// 播放可视化的数据源（ADR-023 §6）：音频线程把交错样本下混成单声道塞进环形缓冲，
/// UI 渲染线程每帧调用 <see cref="NextFrame"/> 取最新一段做 FFT，再交给 <see cref="SpectrumBallistics"/>
/// 推进弹道。整条路径没有后台线程、没有消息队列：窗口被遮挡 / 最小化时 WPF 不再出渲染帧，
/// 分析也随之停摆，不会白烧 CPU。
/// </summary>
public sealed class SpectrumAnalyzer : IAudioSampleSink, ISpectrumSource, IDisposable
{
    /// <summary>环形缓冲容量（单声道样本，2 的幂）：8192 点 FFT 的 4 倍余量，44.1 kHz 下约 0.74 s。</summary>
    private const int RingCapacity = 1 << 15;

    private const int RingMask = RingCapacity - 1;

    /// <summary>超过这个时间没有新样本（暂停 / 输出被释放）就按静音处理，让柱体自然落下去。</summary>
    private const double StaleSeconds = 0.15;

    private readonly object _gate = new();
    private readonly IAppLogger _logger;
    private readonly float[] _ring = new float[RingCapacity];
    private readonly SpectrumBallistics _ballistics = new();

    private float[] _fftInput = [];
    private double[] _levels = [];
    private double[] _peaks = [];
    private double[] _silentBands = [];
    private SpectrumProcessor? _processor;
    private SpectrumOptions _options = SpectrumOptions.Default;

    /// <summary>累计写入的单声道样本数（音频线程递增，UI 线程只读）。</summary>
    private long _written;

    private long _frames;
    private double _totalMs;
    private double _maxMs;
    private long _frameId;
    private long _lastWritten;
    private double _staleSeconds;
    private int _sampleRate;
    private int _channels = 2;
    private bool _disposed;

    public SpectrumAnalyzer(IAppLogger logger)
    {
        _logger = logger;
        var processor = EnsureProcessor();
        _logger.Debug(
            $"Spectrum analyzer ready: fft={processor.FftSize}, bands={processor.BandCount}, rate={processor.SampleRate}Hz.");
    }

    /// <summary>运行统计（帧数 / 平均与最大分析耗时）。</summary>
    public SpectrumStats Stats => new(
        Interlocked.Read(ref _frames),
        Volatile.Read(ref _totalMs),
        Volatile.Read(ref _maxMs),
        _sampleRate,
        _levels.Length);

    /// <summary>输出链建立时由播放器调用（不在音频回调线程上）。</summary>
    public void Configure(int sampleRate, int channels)
    {
        var rate = sampleRate > 0 ? sampleRate : 48000;
        lock (_gate)
        {
            if (_sampleRate != rate)
            {
                _sampleRate = rate;
                _processor = null; // 采样率变了，频段 → bin 的映射要重算
            }

            _channels = Math.Max(1, channels);
        }

        Reset();
    }

    /// <summary>音频线程回调：下混 + 写环形缓冲。只做算术与一次 Volatile 写，不分配也不加锁。</summary>
    public void Write(float[] samples, int offset, int count)
    {
        if (_disposed || count <= 0)
        {
            return;
        }

        var channels = _channels;
        var frames = count / channels;
        if (frames <= 0)
        {
            return;
        }

        var ring = _ring;
        var position = Volatile.Read(ref _written);
        var index = (int)(position & RingMask);
        if (channels == 1)
        {
            for (var frame = 0; frame < frames; frame++)
            {
                ring[index] = samples[offset + frame];
                index = (index + 1) & RingMask;
            }
        }
        else
        {
            var inverse = 1f / channels;
            for (var frame = 0; frame < frames; frame++)
            {
                var sampleOffset = offset + frame * channels;
                var sum = 0f;
                for (var channel = 0; channel < channels; channel++)
                {
                    sum += samples[sampleOffset + channel];
                }

                ring[index] = sum * inverse;
                index = (index + 1) & RingMask;
            }
        }

        Volatile.Write(ref _written, position + frames);
    }

    /// <summary>丢弃已缓冲的旧样本并复位弹道（跳转 / 停止 / 换曲）。可在 UI 或工作线程调用。</summary>
    public void Reset()
    {
        Volatile.Write(ref _written, 0);
        _lastWritten = 0;
        _staleSeconds = 0;
        lock (_gate)
        {
            _ballistics.Reset();
        }
    }

    /// <summary>整体替换分析参数（设置页 / 启动时调用），下一帧生效。</summary>
    public void Apply(VisualizationSettings settings)
    {
        var options = SpectrumOptions.From(settings.Spectrum);
        lock (_gate)
        {
            _options = options;
            _processor = null;
            EnsureBuffersLocked(options.BandCount);
            _ballistics.Configure(options);
        }

        _logger.Debug(
            $"Spectrum options: bands={options.BandCount}, fft={options.FftSize}, " +
            $"range={options.MinFrequency:0.#}-{options.MaxFrequency:0.#}Hz, floor={options.FloorDb:0.#}dB, " +
            $"slope={options.SlopeDbPerOctave:0.#}dB/oct, summation={options.Summation}, peaks={options.ShowPeaks}.");
    }

    /// <summary>
    /// 计算一帧（在 UI 渲染线程调用）：取最新 FFT 长度样本 → 频段 dB → 弹道 → 归一化柱高。
    /// 没有新样本（暂停 / 未播放）时按静音推进，柱体会在释放速度内落回底部。
    /// </summary>
    public SpectrumFrame NextFrame(double dtSeconds)
    {
        var dt = double.IsFinite(dtSeconds) ? Math.Clamp(dtSeconds, 0.001, 0.5) : 1.0 / 60.0;
        var started = Stopwatch.GetTimestamp();

        var processor = EnsureProcessor();
        var levels = _levels;
        var peaks = _peaks;
        var written = Volatile.Read(ref _written);
        double[] measured;

        if (written < processor.FftSize || _channels <= 0)
        {
            measured = _silentBands;
            _staleSeconds += dt;
        }
        else
        {
            if (written == _lastWritten)
            {
                _staleSeconds += dt;
            }
            else
            {
                _staleSeconds = 0;
            }

            _lastWritten = written;
            if (_staleSeconds > StaleSeconds)
            {
                measured = _silentBands;
            }
            else
            {
                var start = written - processor.FftSize;
                var input = _fftInput;
                for (var i = 0; i < input.Length; i++)
                {
                    input[i] = _ring[(int)((start + i) & RingMask)];
                }

                var bandDb = processor.ComputeBandDb(input);
                measured = bandDb;
            }
        }

        lock (_gate)
        {
            _ballistics.Update(measured, dt, levels, peaks);
        }

        var frame = new SpectrumFrame((double[])levels.Clone(), (double[])peaks.Clone(), ++_frameId, measured == _silentBands);

        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        Interlocked.Increment(ref _frames);
        Volatile.Write(ref _totalMs, Volatile.Read(ref _totalMs) + elapsed);
        if (elapsed > Volatile.Read(ref _maxMs))
        {
            Volatile.Write(ref _maxMs, elapsed);
        }

        return frame;
    }

    public void Dispose()
    {
        _disposed = true;
        Volatile.Write(ref _written, 0);
    }

    private SpectrumProcessor EnsureProcessor()
    {
        var rate = _sampleRate > 0 ? _sampleRate : 48000;
        var options = _options;
        var processor = _processor;
        if (processor is not null && processor.SampleRate == rate && ReferenceEquals(processor.Options, options))
        {
            return processor;
        }

        lock (_gate)
        {
            processor = _processor;
            if (processor is not null && processor.SampleRate == rate && ReferenceEquals(processor.Options, options))
            {
                return processor;
            }

            processor = new SpectrumProcessor(options, rate);
            _processor = processor;
            _fftInput = new float[processor.FftSize];
            EnsureBuffersLocked(options.BandCount);
            _ballistics.Configure(options);
            return processor;
        }
    }

    private void EnsureBuffersLocked(int bandCount)
    {
        if (_levels.Length != bandCount)
        {
            _levels = new double[bandCount];
            _peaks = new double[bandCount];
            _silentBands = new double[bandCount];
        }

        // “静音测量值”取显示下限本身：弹道会从当前高度按释放速度平滑落下去，而不是突然清零。
        Array.Fill(_silentBands, _options.FloorDb);
    }
}
