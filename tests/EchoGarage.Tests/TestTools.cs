using System.Diagnostics;
using NAudio.Wave;

namespace EchoGarage.Tests;

/// <summary>
/// 共享测试工具：定位并运行仓库内置编码器、生成真实 AAC 容器。
/// </summary>
internal static class TestTools
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, string?> ToolCopies = new(StringComparer.OrdinalIgnoreCase);

    private static string ToolsDirectory =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tools"));

    /// <summary>
    /// Returns a runnable copy of a bundled tool. Some restricted environments (for example
    /// sandboxed agent sessions) let child processes create files only when the executable
    /// itself lives outside the workspace, so the tools are copied to the user temp folder
    /// before running. Returns <c>null</c> when the tool is not bundled (source-only checkout).
    /// </summary>
    public static string? ResolveTool(string name)
    {
        lock (Sync)
        {
            if (ToolCopies.TryGetValue(name, out var cached))
            {
                return cached;
            }

            var source = Path.Combine(ToolsDirectory, name);
            if (!File.Exists(source))
            {
                ToolCopies[name] = null;
                return null;
            }

            try
            {
                var directory = Path.Combine(Path.GetTempPath(), "EchoGarage.Tests", "tools");
                Directory.CreateDirectory(directory);
                var target = Path.Combine(directory, name);
                if (!File.Exists(target) || new FileInfo(target).Length != new FileInfo(source).Length)
                {
                    File.Copy(source, target, overwrite: true);
                }

                ToolCopies[name] = target;
                return target;
            }
            catch (IOException)
            {
                ToolCopies[name] = source;
                return source;
            }
            catch (UnauthorizedAccessException)
            {
                ToolCopies[name] = source;
                return source;
            }
        }
    }

    public static async Task<bool> RunAsync(string executable, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return false;
        }

        // 必须同时消费 stdout/stderr，否则管道写满后子进程会卡死。
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await stdout;
        await stderr;
        return process.ExitCode == 0;
    }

    /// <summary>
    /// 用 Media Foundation 把 PCM WAV 编码成真实的 AAC-in-M4A 容器（供 Apple 标签路径测试）。
    /// 部分环境（Windows N 版、服务会话等）没有 AAC 编码器，此时返回 false，由调用方跳过测试。
    /// </summary>
    public static bool TryCreateM4a(string wavPath, string m4aPath)
    {
        try
        {
            using var reader = new WaveFileReader(wavPath);
            MediaFoundationEncoder.EncodeToAac(reader, m4aPath, 128000);
            return File.Exists(m4aPath) && new FileInfo(m4aPath).Length > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
