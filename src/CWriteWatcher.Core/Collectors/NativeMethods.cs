using System.Runtime.InteropServices;
using System.Text;

namespace CWriteWatcher.Core.Collectors;

/// <summary>标准模式所需的 Win32 声明。</summary>
internal static class NativeMethods
{
    public const int FILE_LIST_DIRECTORY = 0x0001;
    public const int FILE_SHARE_READ = 0x00000001;
    public const int FILE_SHARE_WRITE = 0x00000002;
    public const int FILE_SHARE_DELETE = 0x00000004;
    public const int OPEN_EXISTING = 3;
    public const int FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    public const int FILE_FLAG_OVERLAPPED = 0x40000000;

    public const int FILE_NOTIFY_CHANGE_FILE_NAME = 0x00000001;
    public const int FILE_NOTIFY_CHANGE_DIR_NAME = 0x00000002;
    public const int FILE_NOTIFY_CHANGE_ATTRIBUTES = 0x00000004;
    public const int FILE_NOTIFY_CHANGE_SIZE = 0x00000008;
    public const int FILE_NOTIFY_CHANGE_LAST_WRITE = 0x00000010;
    public const int FILE_NOTIFY_CHANGE_CREATION = 0x00000040;

    /// <summary>我们要的过滤位：文件名 / 目录名 / 大小 / 最后写入 / 创建。</summary>
    public const int WATCH_FILTER =
        FILE_NOTIFY_CHANGE_FILE_NAME | FILE_NOTIFY_CHANGE_DIR_NAME |
        FILE_NOTIFY_CHANGE_SIZE | FILE_NOTIFY_CHANGE_LAST_WRITE |
        FILE_NOTIFY_CHANGE_CREATION;

    public const int ERROR_OPERATION_ABORTED = 995;
    public const int ERROR_NOTIFY_ENUM_DIR = 1022;

    public const int FILE_ACTION_ADDED = 1;
    public const int FILE_ACTION_REMOVED = 2;
    public const int FILE_ACTION_MODIFIED = 3;
    public const int FILE_ACTION_RENAMED_OLD_NAME = 4;
    public const int FILE_ACTION_RENAMED_NEW_NAME = 5;

    public const int INVALID_HANDLE_VALUE = -1;

    [StructLayout(LayoutKind.Sequential)]
    public struct FILE_NOTIFY_INFORMATION
    {
        public int NextEntryOffset;
        public int Action;
        public int FileNameLength;
        // WCHAR FileName[1]; 紧随其后，不定长
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct WIN32_FILE_ATTRIBUTE_DATA
    {
        public uint dwFileAttributes;
        public long ftCreationTime;
        public long ftLastAccessTime;
        public long ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;

        public readonly long Size => ((long)nFileSizeHigh << 32) | nFileSizeLow;
    }

    public const int FILE_ATTRIBUTE_DIRECTORY = 0x00000010;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    public static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(
        string lpFileName,
        int dwDesiredAccess,
        int dwShareMode,
        IntPtr lpSecurityAttributes,
        int dwCreationDisposition,
        int dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern unsafe bool ReadDirectoryChangesW(
        Microsoft.Win32.SafeHandles.SafeFileHandle hDirectory,
        byte* lpBuffer,
        int nBufferLength,
        [MarshalAs(UnmanagedType.Bool)] bool bWatchSubtree,
        int dwNotifyFilter,
        out int lpBytesReturned,
        NativeOverlapped* lpOverlapped,
        IntPtr lpCompletionRoutine);

    // ★ 必须显式 CharSet.Unicode：DllImport 的默认 CharSet 是 Ansi，
    //   而 GetFileAttributesExW 是宽字符版本。若按 ANSI 编组，传入的路径会被当成
    //   UTF-16 解析成乱码，调用恒返回 FALSE（LastError=2 文件找不到），
    //   结果是 TryGetFileInfo 永远失败 → 标准模式收不到任何事件。
    //   ExactSpelling 保证入口点名不被追加 A/W 后缀。
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetFileAttributesExW(
        string lpFileName,
        int fInfoLevelId,
        out WIN32_FILE_ATTRIBUTE_DATA lpFileInformation);

    public const int GetFileExInfoStandard = 0;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern unsafe bool CancelIoEx(
        Microsoft.Win32.SafeHandles.SafeFileHandle hFile,
        NativeOverlapped* lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint GetProcessHandleCount(IntPtr hProcess, out uint pdwHandleCount);

    /// <summary>查询文件属性；文件不存在或无权访问时返回 false。</summary>
    public static bool TryGetFileInfo(string path, out WIN32_FILE_ATTRIBUTE_DATA data, out bool isDirectory)
    {
        if (GetFileAttributesExW(path, GetFileExInfoStandard, out data))
        {
            isDirectory = (data.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0;
            return true;
        }

        isDirectory = false;
        return false;
    }

    /// <summary>从 FILE_NOTIFY_INFORMATION 结构中读取文件名。</summary>
    public static unsafe string ReadFileName(byte* basePtr, int offset, int nameLength)
    {
        if (nameLength <= 0) return string.Empty;
        var p = basePtr + offset;
        return Encoding.Unicode.GetString(p, nameLength);
    }
}
