using System.Diagnostics;
using EchoGarage.Core.Abstractions;
using EchoGarage.Core.Models;
using EchoGarage.Core.Services;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace EchoGarage.Infrastructure.Audio;

/// <summary>
/// 管理 Beat This! ONNX Runtime 会话生命周期（ADR-034 §6）：
/// <list type="bullet">
/// <item>严格懒加载：构造时不接触任何 ONNX / DirectML 原生 DLL，启动耗时与非 BPM 内存/显存增量为 0。</item>
/// <item>DirectML GPU 优先（DX12 全厂商 N/A/I），锁定静态时间维 <c>frames=1500</c> 避免反复编译 Shader；
/// 不可用或运行时异常自动无缝回退 CPU EP。</item>
/// <item>空闲自动卸载：测完后默认 60 秒无新请求自动 <c>Dispose()</c> 释放显存与原生堆；设为 0 时测完立即释放。</item>
/// </list>
/// </summary>
internal sealed class OnnxBeatThisSessionHost : IDisposable
{
    public const int ChunkFrames = 1500; // 30.0 s @ 50 fps（Beat This! 官方最大静态块）
    private const string InputTensorName = "mel";
    private const string OutputBeatName = "beat_logits";
    private const string DynamicFramesDimName = "frames";

    private readonly IAppLogger _logger;
    private readonly object _gate = new();
    private InferenceSession? _session;
    private string? _loadedModelPath;
    private bool _loadedWithGpu;
    private string _executionProvider = "CPU";
    private Timer? _idleTimer;
    private bool _disposed;

    public OnnxBeatThisSessionHost(IAppLogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string CurrentExecutionProvider
    {
        get
        {
            lock (_gate)
            {
                return _executionProvider;
            }
        }
    }

    public bool IsLoaded
    {
        get
        {
            lock (_gate)
            {
                return _session is not null;
            }
        }
    }

    /// <summary>
    /// 对单个固定 1500×128（float32）梅尔谱块执行推理，返回 1500 帧 <c>beat_logits</c>。
    /// 调用方已由单飞锁串行化；DirectML 运行期偶发驱动异常会自动销毁会话并以 CPU EP 重试一次。
    /// </summary>
    public (float[] BeatLogits, string ExecutionProvider) RunChunk(
        string modelPath,
        bool preferGpu,
        float[] melBuffer1500x128)
    {
        ArgumentNullException.ThrowIfNull(melBuffer1500x128);
        if (melBuffer1500x128.Length != ChunkFrames * MelSpectrogramMath.NumMels)
        {
            throw new ArgumentException($"梅尔块大小必须为 {ChunkFrames * MelSpectrogramMath.NumMels}。", nameof(melBuffer1500x128));
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            CancelIdleTimerLocked();
            EnsureSessionLocked(modelPath, preferGpu);

            try
            {
                var logits = ExecuteLocked(_session!, melBuffer1500x128);
                return (logits, _executionProvider);
            }
            catch (Exception ex) when (_loadedWithGpu)
            {
                // 显卡驱动 TDR / 设备重置或算子在特定旧驱动下不支持：回退 CPU 重试一次。
                _logger.Warn($"DirectML BPM inference failed ({ex.Message}); retrying on CPU EP.");
                DisposeSessionLocked();
                EnsureSessionLocked(modelPath, preferGpu: false);
                var logits = ExecuteLocked(_session!, melBuffer1500x128);
                return (logits, _executionProvider);
            }
        }
    }

    /// <summary>一次完整曲目分析结束后调度空闲释放（0 秒表示立刻 Dispose 释放显存/内存）。</summary>
    public void NotifyAnalysisFinished(int idleUnloadSeconds)
    {
        lock (_gate)
        {
            if (_disposed || _session is null)
            {
                return;
            }

            if (idleUnloadSeconds <= 0)
            {
                DisposeSessionLocked();
                _logger.Debug("BPM ONNX session released immediately after analysis.");
                return;
            }

            CancelIdleTimerLocked();
            _idleTimer = new Timer(
                static state => ((OnnxBeatThisSessionHost)state!).OnIdleTimerElapsed(),
                this,
                TimeSpan.FromSeconds(idleUnloadSeconds),
                Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>主动释放当前会话（设置切换模型/关闭 GPU 或用户手动释放时调用）。</summary>
    public void Unload()
    {
        lock (_gate)
        {
            CancelIdleTimerLocked();
            if (_session is not null)
            {
                DisposeSessionLocked();
                _logger.Info("BPM ONNX session unloaded.");
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
            CancelIdleTimerLocked();
            DisposeSessionLocked();
        }
    }

    private void OnIdleTimerElapsed()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            CancelIdleTimerLocked();
            if (_session is not null)
            {
                DisposeSessionLocked();
                _logger.Info("BPM ONNX session auto-unloaded after idle timeout (RAM/VRAM released).");
            }
        }
    }

    private void EnsureSessionLocked(string modelPath, bool preferGpu)
    {
        if (_session is not null &&
            string.Equals(_loadedModelPath, modelPath, StringComparison.OrdinalIgnoreCase) &&
            _loadedWithGpu == preferGpu)
        {
            return;
        }

        DisposeSessionLocked();

        var sw = Stopwatch.StartNew();
        if (preferGpu)
        {
            try
            {
                using var options = CreateBaseOptions();
                options.AppendExecutionProvider_DML(0);
                options.AppendExecutionProvider_CPU();
                _session = new InferenceSession(modelPath, options);
                _loadedModelPath = modelPath;
                _loadedWithGpu = true;
                _executionProvider = "DirectML (GPU)";
                _logger.Info(
                    $"BPM ONNX session initialized: model={Path.GetFileName(modelPath)}, ep={_executionProvider}, loadMs={sw.ElapsedMilliseconds}.");
                return;
            }
            catch (Exception ex)
            {
                _logger.Warn($"DirectML GPU initialization failed for BPM ({ex.Message}); falling back to CPU EP.");
                DisposeSessionLocked();
            }
        }

        using var cpuOptions = CreateBaseOptions();
        cpuOptions.IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
        cpuOptions.AppendExecutionProvider_CPU();
        _session = new InferenceSession(modelPath, cpuOptions);
        _loadedModelPath = modelPath;
        _loadedWithGpu = false;
        _executionProvider = "CPU";
        _logger.Info(
            $"BPM ONNX session initialized: model={Path.GetFileName(modelPath)}, ep={_executionProvider}, loadMs={sw.ElapsedMilliseconds}.");
    }

    private static SessionOptions CreateBaseOptions()
    {
        var options = new SessionOptions
        {
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            EnableMemoryPattern = false,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING
        };
        // 锁定静态时间维 frames=1500，确保 DirectML 仅编译一次 Compute Shader 且无显存碎片。
        options.AddFreeDimensionOverrideByName(DynamicFramesDimName, ChunkFrames);
        return options;
    }

    private static float[] ExecuteLocked(InferenceSession session, float[] melBuffer)
    {
        var tensor = new DenseTensor<float>(melBuffer, [1, ChunkFrames, MelSpectrogramMath.NumMels]);
        var inputs = new[] { NamedOnnxValue.CreateFromTensor(InputTensorName, tensor) };
        using var outputs = session.Run(inputs);

        var beatValue = outputs.FirstOrDefault(o => string.Equals(o.Name, OutputBeatName, StringComparison.Ordinal))
            ?? outputs.First();
        var beatTensor = beatValue.AsTensor<float>();
        var result = new float[ChunkFrames];
        var span = beatTensor is DenseTensor<float> dense ? dense.Buffer.Span : default;
        if (!span.IsEmpty && span.Length >= ChunkFrames)
        {
            span[..ChunkFrames].CopyTo(result);
        }
        else
        {
            var count = Math.Min(ChunkFrames, (int)beatTensor.Length);
            for (var i = 0; i < count; i++)
            {
                result[i] = beatTensor.GetValue(i);
            }
        }

        return result;
    }

    private void DisposeSessionLocked()
    {
        if (_session is not null)
        {
            try
            {
                _session.Dispose();
            }
            catch
            {
                // Ignore dispose errors on native teardown.
            }

            _session = null;
        }

        _loadedModelPath = null;
        _loadedWithGpu = false;
    }

    private void CancelIdleTimerLocked()
    {
        if (_idleTimer is not null)
        {
            try
            {
                _idleTimer.Dispose();
            }
            catch
            {
                // Ignore.
            }

            _idleTimer = null;
        }
    }
}
