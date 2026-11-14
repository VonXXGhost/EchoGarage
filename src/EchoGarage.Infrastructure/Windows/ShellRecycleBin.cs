using System.Runtime.InteropServices;
using EchoGarage.Core.Abstractions;

namespace EchoGarage.Infrastructure.Windows;

/// <summary>
/// 用 Shell 的 SHFileOperation(FO_DELETE + FOF_ALLOWUNDO) 把文件移到回收站（ADR-021）。
/// 不引入额外依赖；失败时返回 false，调用方不会退化成永久删除。
/// </summary>
public sealed class ShellRecycleBin : IRecycleBin
{
    private const uint FoDelete = 0x0003;
    private const ushort FofSilent = 0x0004;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoErrorUi = 0x0400;

    private readonly IAppLogger _logger;

    public ShellRecycleBin(IAppLogger logger) => _logger = logger;

    public bool TryDelete(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (!File.Exists(path))
        {
            _logger.Warn($"回收站删除跳过：文件不存在 {path}");
            return false;
        }

        try
        {
            // pFrom 必须是双 null 结尾的路径列表；string 封送时尾部再加一个 null。
            var operation = new ShFileOpStruct
            {
                wFunc = FoDelete,
                pFrom = path + "\0",
                fFlags = (ushort)(FofAllowUndo | FofNoConfirmation | FofSilent | FofNoErrorUi)
            };

            var result = SHFileOperation(ref operation);
            if (result != 0 || operation.fAnyOperationsAborted != 0 || File.Exists(path))
            {
                _logger.Warn($"回收站删除失败 path={path} result=0x{result:X4} aborted={operation.fAnyOperationsAborted}");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.Warn($"回收站删除异常 path={path} error={ex.Message}");
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SHFileOperation(ref ShFileOpStruct lpFileOp);
}
