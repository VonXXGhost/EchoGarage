namespace EchoGarage.Infrastructure.Audio;

/// <summary>
/// 响度分析失败时抛出的异常（ADR-028 §3.3）：携带源文件路径与可直接展示给用户的中文说明。
/// 文件缺失、解码器无法打开、读取过程中解码失败等情况都会包装成该异常，
/// 调用方（视图模型）无需再区分底层异常类型。
/// </summary>
public sealed class LoudnessAnalysisException : Exception
{
    /// <summary>创建响度分析异常。</summary>
    /// <param name="path">分析失败的音频文件路径。</param>
    /// <param name="message">可直接展示给用户的错误说明。</param>
    public LoudnessAnalysisException(string path, string message)
        : base(message)
    {
        Path = path;
    }

    /// <summary>创建响度分析异常并保留底层原因。</summary>
    /// <param name="path">分析失败的音频文件路径。</param>
    /// <param name="message">可直接展示给用户的错误说明。</param>
    /// <param name="innerException">导致分析失败的底层异常。</param>
    public LoudnessAnalysisException(string path, string message, Exception? innerException)
        : base(message, innerException)
    {
        Path = path;
    }

    /// <summary>分析失败的音频文件路径（原样保留，便于调用方定位）。</summary>
    public string Path { get; }
}
