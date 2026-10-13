using System.Runtime.InteropServices;

namespace CWriteWatcher.Etw.Native;

/// <summary>
/// ETW / TDH 的原生声明。只包含本项目实际用到的部分。
/// </summary>
internal static class EtwNative
{
    // ---------------- 常量 ----------------

    public const int ERROR_SUCCESS = 0;
    public const int ERROR_ACCESS_DENIED = 5;
    public const int ERROR_INSUFFICIENT_BUFFER = 122;
    public const int ERROR_ALREADY_EXISTS = 183;
    public const int ERROR_WMI_INSTANCE_NOT_FOUND = 4201;
    public const int ERROR_NO_MORE_ITEMS = 259;
    public const int ERROR_IO_PENDING = 997;

    // EVENT_TRACE_CONTROL_QUERY —— 刷新会话属性（不改状态）。
    // 实时会话跑起来之后，这是**唯一**能读到 EventsLost / RealTimeBuffersLost 的办法：
    // 这两个计数器只存在于 EVENT_TRACE_PROPERTIES 里，只有 QUERY 会把它们写回来。
    public const int EVENT_TRACE_CONTROL_QUERY = 0;
    public const int EVENT_TRACE_CONTROL_STOP = 1;

    public const int WNODE_FLAG_TRACED_GUID = 0x00020000;
    public const int EVENT_TRACE_REAL_TIME_MODE = 0x00000100;
    public const int EVENT_TRACE_USE_PAGED_MEMORY = 0x01000000;

    public const int PROCESS_TRACE_MODE_REAL_TIME = 0x00000100;
    public const int PROCESS_TRACE_MODE_EVENT_RECORD = 0x10000000;
    public const int PROCESS_TRACE_MODE_RAW_TIMESTAMP = 0x00001000;

    /// <summary>Microsoft-Windows-Kernel-File 的 Provider GUID。</summary>
    public static readonly Guid KernelFileProviderId = new("EDD08927-9CC4-4E65-B970-C2560FB5C289");

    // Kernel-File 的关键字（掩码值来自 `wevtutil gp ... /ge:true /f:xml` 的 <keyword mask=.../>）
    public const ulong KERNEL_FILE_KEYWORD_FILENAME = 0x10;
    public const ulong KERNEL_FILE_KEYWORD_FILEIO = 0x20;
    public const ulong KERNEL_FILE_KEYWORD_OP_END = 0x40;
    public const ulong KERNEL_FILE_KEYWORD_CREATE = 0x80;
    public const ulong KERNEL_FILE_KEYWORD_READ = 0x100;
    public const ulong KERNEL_FILE_KEYWORD_WRITE = 0x200;
    public const ulong KERNEL_FILE_KEYWORD_DELETE_PATH = 0x400;
    public const ulong KERNEL_FILE_KEYWORD_RENAME_SETLINK_PATH = 0x800;
    public const ulong KERNEL_FILE_KEYWORD_CREATE_NEW_FILE = 0x1000;

    /// <summary>
    /// 订阅的关键字。
    /// </summary>
    /// <remarks>
    /// <para><b>CREATE_NEW_FILE(0x1000) 必须订阅</b>：它是唯一能可靠区分"新建文件"与"打开已有文件"的事件。
    /// 少了它，模式 A 的"仅提醒新建文件"会把所有写入都判成"修改已有文件"，
    /// 表现为**一条提醒都不弹**（而且没有任何报错）。</para>
    /// <para>DELETE_PATH(0x400) / RENAME_SETLINK_PATH(0x800) 用于让 FileKey→路径 映射表跟随改名与删除，
    /// 否则改名后的文件会一直显示旧路径。</para>
    /// <para>这三个关键字对应的事件都是低频事件（创建/改名/删除），对 CPU 的影响可以忽略。</para>
    /// </remarks>
    public const ulong SUBSCRIBE_KEYWORDS =
        KERNEL_FILE_KEYWORD_FILENAME
        | KERNEL_FILE_KEYWORD_FILEIO
        | KERNEL_FILE_KEYWORD_CREATE
        | KERNEL_FILE_KEYWORD_DELETE_PATH
        | KERNEL_FILE_KEYWORD_RENAME_SETLINK_PATH
        | KERNEL_FILE_KEYWORD_CREATE_NEW_FILE;

    // ------------------------------------------------------------------ 事件 ID
    //
    // ★ 必须按 ID 判定，不能按事件名判定：
    //   Microsoft-Windows-Kernel-File 的 manifest 不提供事件名，
    //   TDH 返回的 TRACE_EVENT_INFO.EventNameOffset 恒为 0，
    //   所以 schema.Name 永远是空串，按名字 switch 会全部落到 default 分支
    //   （表现为"事件总数 261 万、接收事件 0、且没有任何报错"）。
    //
    // ★ 下表是 `wevtutil gp Microsoft-Windows-Kernel-File /ge:true /f:xml` 导出的
    //   <task> 元素，是本机 Windows 给出的权威对应关系（事件名在系统里一直存在，
    //   只是 TDH 不给）：
    //     NameCreate 10 / NameDelete 11 / Create 12 / Cleanup 13 / Close 14 /
    //     Read 15 / Write 16 / SetInformation 17 / SetDelete 18 / Rename 19 /
    //     DirEnum 20 / Flush 21 / QueryInformation 22 / FSCTL 23 / OperationEnd 24 /
    //     DirNotify 25 / DeletePath 26 / RenamePath 27 / SetLinkPath 28 / SetLink 29 /
    //     CreateNewFile 30 / SetSecurity 31 / QuerySecurity 32 / SetEA 33 / QueryEA 34
    public const int EVENT_ID_NAME_CREATE = 10;   // FileKey + FileName  ← FileKey→路径 的官方来源
    public const int EVENT_ID_NAME_DELETE = 11;   // FileKey + FileName（名字被移除）
    public const int EVENT_ID_CREATE = 12;        // FileObject + FileName（注意：没有 FileKey）
    public const int EVENT_ID_CLEANUP = 13;       // FileObject + FileKey
    public const int EVENT_ID_CLOSE = 14;         // FileObject + FileKey
    public const int EVENT_ID_READ = 15;
    public const int EVENT_ID_WRITE = 16;         // ByteOffset + FileObject + FileKey + IOSize
    public const int EVENT_ID_SET_INFORMATION = 17;
    public const int EVENT_ID_SET_DELETE = 18;
    public const int EVENT_ID_RENAME = 19;
    public const int EVENT_ID_DELETE_PATH = 26;
    public const int EVENT_ID_RENAME_PATH = 27;
    public const int EVENT_ID_SET_LINK_PATH = 28;
    public const int EVENT_ID_SET_LINK = 29;
    public const int EVENT_ID_CREATE_NEW_FILE = 30;   // 新建文件的权威信号

    /// <summary>FILE_INFORMATION_CLASS 中的 FileEndOfFileInformation，用于识别"截断/扩展"。</summary>
    public const int FILE_END_OF_FILE_INFORMATION = 20;

    /// <summary>
    /// 事件 ID → 权威事件名（来自 <c>wevtutil gp ... /f:xml</c> 的 &lt;task&gt; 列表）。
    /// </summary>
    /// <remarks>
    /// 必须查表：TDH 的 <c>TRACE_EVENT_INFO.EventNameOffset</c> 对 Kernel-File 恒为 0，
    /// 拿不到事件名，日志和诊断报告里只能显示空字符串。
    /// </remarks>
    public static string EventIdName(int eventId) => eventId switch
    {
        EVENT_ID_NAME_CREATE => "NameCreate",
        EVENT_ID_NAME_DELETE => "NameDelete",
        EVENT_ID_CREATE => "Create",
        EVENT_ID_CLEANUP => "Cleanup",
        EVENT_ID_CLOSE => "Close",
        EVENT_ID_READ => "Read",
        EVENT_ID_WRITE => "Write",
        EVENT_ID_SET_INFORMATION => "SetInformation",
        EVENT_ID_SET_DELETE => "SetDelete",
        EVENT_ID_RENAME => "Rename",
        EVENT_ID_DELETE_PATH => "DeletePath",
        EVENT_ID_RENAME_PATH => "RenamePath",
        EVENT_ID_SET_LINK_PATH => "SetLinkPath",
        EVENT_ID_SET_LINK => "SetLink",
        EVENT_ID_CREATE_NEW_FILE => "CreateNewFile",
        20 => "DirEnum",
        21 => "Flush",
        22 => "QueryInformation",
        23 => "FSCTL",
        24 => "OperationEnd",
        25 => "DirNotify",
        31 => "SetSecurity",
        32 => "QuerySecurity",
        33 => "SetEA",
        34 => "QueryEA",
        _ => "(未知)",
    };

    // TDH 输入类型
    public const ushort TDH_INTYPE_UNICODESTRING = 1;
    public const ushort TDH_INTYPE_ANSISTRING = 2;
    public const ushort TDH_INTYPE_INT8 = 3;
    public const ushort TDH_INTYPE_UINT8 = 4;
    public const ushort TDH_INTYPE_INT16 = 5;
    public const ushort TDH_INTYPE_UINT16 = 6;
    public const ushort TDH_INTYPE_INT32 = 7;
    public const ushort TDH_INTYPE_UINT32 = 8;
    public const ushort TDH_INTYPE_INT64 = 9;
    public const ushort TDH_INTYPE_UINT64 = 10;
    public const ushort TDH_INTYPE_FLOAT = 11;
    public const ushort TDH_INTYPE_DOUBLE = 12;
    public const ushort TDH_INTYPE_BOOLEAN = 13;
    public const ushort TDH_INTYPE_BINARY = 14;
    public const ushort TDH_INTYPE_GUID = 15;
    public const ushort TDH_INTYPE_POINTER = 16;
    public const ushort TDH_INTYPE_FILETIME = 17;
    public const ushort TDH_INTYPE_SYSTEMTIME = 18;
    public const ushort TDH_INTYPE_SID = 19;
    public const ushort TDH_INTYPE_HEXINT32 = 20;
    public const ushort TDH_INTYPE_HEXINT64 = 21;
    public const ushort TDH_INTYPE_NONNULLTERMINATEDSTRING = 28;
    public const ushort TDH_INTYPE_NONNULLTERMINATEDANSISTRING = 29;
    public const ushort TDH_INTYPE_UNICODECHAR = 30;
    public const ushort TDH_INTYPE_ANSICHAR = 31;
    public const ushort TDH_INTYPE_SIZET = 32;
    public const ushort TDH_INTYPE_HEXDUMP = 33;

    // ---------------- 结构体 ----------------

    /// <summary>
    /// WNODE_HEADER（**48 字节**）。
    /// <para>关键：<c>HistoricalContext</c> 和 <c>KernelHandle</c> 是两个 union（各 8 字节，偏移 8 与 16），
    /// 而 <c>TimeStamp</c> 与 <c>KernelHandle</c> <b>共用同一个 8 字节槽</b>（偏移 16），
    /// 不是第三个独立字段。</para>
    /// <para>把 <c>TimeStamp</c> 单独声明成一个字段会让结构体变成 56 字节，
    /// 后续所有字段偏移 8 字节 → <c>LoggerNameOffset</c> 被写到 128 而 StartTrace 从 116 读 →
    /// 读到 0（无会话名）→ <c>StartTrace</c> 返回 161 ERROR_BAD_PATHNAME。</para>
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct WNODE_HEADER
    {
        public uint BufferSize;          // 0
        public uint ProviderId;          // 4
        public ulong HistoricalContext;  // 8   (union: HistoricalContext / Version+Linkage)
        public IntPtr KernelHandle;      // 16  (union: KernelHandle / TimeStamp —— 共用此槽)
        public Guid Guid;                // 24
        public uint ClientContext;       // 40
        public uint Flags;               // 44
    }                                    // 合计 48

    [StructLayout(LayoutKind.Sequential)]
    public struct EVENT_TRACE_PROPERTIES
    {
        public WNODE_HEADER Wnode;
        public uint BufferSize;
        public uint MinimumBuffers;
        public uint MaximumBuffers;
        public uint MaximumFileSize;
        public uint LogFileMode;
        public uint FlushTimer;
        public uint EnableFlags;
        public int AgeLimit;
        public uint NumberOfBuffers;
        public uint FreeBuffers;
        public uint EventsLost;
        public uint BuffersWritten;
        public uint LogBuffersLost;
        public uint RealTimeBuffersLost;
        public IntPtr LoggerThreadId;
        public uint LogFileNameOffset;
        public uint LoggerNameOffset;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EVENT_DESCRIPTOR
    {
        public ushort Id;
        public byte Version;
        public byte Channel;
        public byte Level;
        public byte Opcode;
        public ushort Task;
        public ulong Keyword;
    }

    /// <summary>
    /// EVENT_HEADER。注意 KernelTime / UserTime 在原生定义里是 union 中的两个 ULONG
    /// （共 8 字节），不是两个 ULONG64 —— 写错会让整个结构偏移 8 字节。
    /// 本结构总长 80 字节。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct EVENT_HEADER
    {
        public ushort Size;
        public ushort HeaderType;
        public ushort Flags;
        public ushort EventProperty;
        public uint ThreadId;
        public uint ProcessId;
        public long TimeStamp;
        public Guid ProviderId;
        public EVENT_DESCRIPTOR EventDescriptor;
        public uint KernelTime;
        public uint UserTime;
        public Guid ActivityId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ETW_BUFFER_CONTEXT
    {
        public byte ProcessorNumber;
        public byte Alignment;
        public ushort LoggerId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EVENT_RECORD
    {
        public EVENT_HEADER EventHeader;
        public ETW_BUFFER_CONTEXT BufferContext;
        public ushort ExtendedDataCount;
        public ushort UserDataLength;
        public IntPtr ExtendedData;
        public IntPtr UserData;
        public IntPtr UserContext;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TRACE_EVENT_INFO_HEADER
    {
        public Guid ProviderGuid;
        public Guid EventGuid;
        public EVENT_DESCRIPTOR EventDescriptor;
        public int DecodingSource;
        public uint ProviderNameOffset;
        public uint LevelNameOffset;
        public uint ChannelNameOffset;
        public uint KeywordsNameOffset;
        public uint TaskNameOffset;
        public uint OpcodeNameOffset;
        public uint EventMessageOffset;
        public uint ProviderMessageOffset;
        public uint BinaryXMLOffset;
        public uint BinaryXMLSize;
        public uint EventNameOffset;
        public uint EventAttributesOffset;
        public uint PropertyCount;
        public uint TopLevelPropertyCount;
        public uint Flags;

        /// <summary>EventPropertyInfoArray 紧随其后（本结构共 112 字节）。</summary>
        public const int PropertyArrayOffset = 112;
    }

    /// <summary>
    /// EVENT_PROPERTY_INFO（**24 字节**）。
    /// </summary>
    /// <remarks>
    /// <para>真实定义（tdh.h）：</para>
    /// <code>
    /// PROPERTY_FLAGS Flags;      // 0
    /// ULONG          NameOffset; // 4
    /// union { USHORT InType; USHORT OutType; ULONG MapNameOffset; };  // 8..15
    /// union { USHORT count; ... };      // 16
    /// union { USHORT length; ... };     // 18
    /// union { ULONG Reserved; ... };    // 20
    /// </code>
    /// <para><b>曾经漏掉了最后三个 union，把结构声明成 16 字节。</b>
    /// 而 <see cref="EventSchemaCache"/> 是用 <c>Marshal.SizeOf&lt;EVENT_PROPERTY_INFO&gt;()</c>
    /// 作为步长遍历 <c>EventPropertyInfoArray</c> 的 —— 步长偏小 8 字节后，
    /// 每读 3 个条目才会正好落到一个真实条目边界（16×3 = 48 = 24×2），
    /// 于是字段名只出现三分之一、其余全是垃圾。这正是诊断报告里
    /// 「每 3 个条目才有一个名字」的原因。</para>
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    public struct EVENT_PROPERTY_INFO
    {
        public uint Flags;          // 0   (PROPERTY_FLAGS)
        public uint NameOffset;     // 4
        public ushort InType;       // 8   (nonStructType.InType)
        public ushort OutType;      // 10
        public uint MapNameOffset;  // 12
        public ushort Count;        // 16  (union: count / countPropertyIndex)
        public ushort Length;       // 18  (union: length / lengthPropertyIndex)
        public uint Reserved;       // 20  (union: Reserved / Tags)
    }                                // 合计 24

    // ---------------- P/Invoke ----------------

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int StartTraceW(
        out ulong sessionHandle,
        string sessionName,
        IntPtr properties);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int ControlTraceW(
        ulong sessionHandle,
        string? sessionName,
        IntPtr properties,
        int controlCode);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern int EnableTraceEx2(
        ulong sessionHandle,
        in Guid providerId,
        uint controlCode,
        byte level,
        ulong matchAnyKeyword,
        ulong matchAllKeyword,
        uint timeout,
        IntPtr enableParameters);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ulong OpenTraceW(ref EVENT_TRACE_LOGFILE logfile);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern int ProcessTrace(
        ulong[] handleArray,
        uint handleCount,
        IntPtr startTime,
        IntPtr endTime);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern int CloseTrace(ulong traceHandle);

    [DllImport("tdh.dll", SetLastError = true)]
    public static extern int TdhGetEventInformation(
        IntPtr eventRecord,
        uint tdhContextCount,
        IntPtr tdhContext,
        IntPtr buffer,
        ref uint bufferSize);

    [DllImport("tdh.dll", SetLastError = true)]
    public static extern int TdhGetProperty(
        IntPtr eventRecord,
        uint tdhContextCount,
        IntPtr tdhContext,
        uint propertyDataCount,
        IntPtr propertyData,
        uint bufferSize,
        IntPtr buffer);

    // ---------------- 回调委托 ----------------

    public delegate void EventRecordCallback(IntPtr eventRecord);

    public delegate uint BufferCallback(IntPtr logfile);

    /// <summary>EVENT_TRACE_HEADER（48 字节）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct EVENT_TRACE_HEADER
    {
        public ushort Size;              // 0
        public ushort FieldTypeFlags;    // 2
        public uint Version;             // 4   (union: Version / Type+Level+Version)
        public uint ThreadId;            // 8
        public uint ProcessId;           // 12
        public long TimeStamp;           // 16
        public Guid Guid;                // 24
        public uint KernelTime;          // 40
        public uint UserTime;            // 44
    }                                    // 合计 48

    /// <summary>EVENT_TRACE（88 字节）。在 EVENT_TRACE_LOGFILE 中是**按值内嵌**的，不能用 IntPtr 占位。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct EVENT_TRACE
    {
        public EVENT_TRACE_HEADER Header;  // 0  (48)
        public uint InstanceId;            // 48
        public uint ParentInstanceId;      // 52
        public Guid ParentGuid;            // 56
        public IntPtr MofData;             // 72
        public uint MofLength;             // 80
        public uint ClientContext;         // 84
    }                                      // 合计 88

    /// <summary>
    /// TRACE_LOGFILE_HEADER（**280 字节**）。
    /// </summary>
    /// <remarks>
    /// <para>本程序<b>不使用它的任何字段</b>（纯输出），但它必须以<b>正确的字节数按值占据空间</b>，
    /// 否则紧随其后的 <c>BufferCallback</c> / <c>EventRecordCallback</c> 会整体错位——
    /// 那样 <c>OpenTrace</c> 会从错误偏移读到一个垃圾函数指针，
    /// <c>ProcessTrace</c> 一调用就 <b>c0000005 访问冲突</b>，且故障模块无法符号化。</para>
    /// <para>真实定义含 <c>TIME_ZONE_INFORMATION</c>（172 字节）等嵌套结构，手写字段极易出错，
    /// 因此这里用 <see cref="StructLayoutAttribute.Size"/> 直接把总长度钉死。
    /// 布局计算（x64）：BufferSize 0 / Version 4 / ProviderVersion 8 / NumberOfProcessors 12 /
    /// EndTime 16 / TimerResolution 24 / MaximumFileSize 28 / LogFileMode 32 / BuffersWritten 36 /
    /// LogInstanceGuid 40 / LoggerName 56 / LogFileName 64 / TimeZone 72(172B) /
    /// BootTime 248 / PerfFreq 256 / StartTime 264 / ReservedFlags 272 / BuffersLost 276。</para>
    /// </remarks>
    [StructLayout(LayoutKind.Sequential, Size = 280)]
    public struct TRACE_LOGFILE_HEADER
    {
        /// <summary>占位字段。真正的大小由 <c>Size = 280</c> 保证。</summary>
        public long Reserved;
    }

    /// <summary>
    /// EVENT_TRACE_LOGFILE（**448 字节**）。字段顺序必须与 evntrace.h 严格一致——
    /// 这里写错一个字段，OpenTrace 就会读到垃圾并静默失败，甚至直接崩溃。
    /// </summary>
    /// <remarks>
    /// 关键点：<c>CurrentEvent</c>（88 字节）与 <c>LogfileHeader</c>（280 字节）都是
    /// <b>按值内嵌</b>，不能用 <c>IntPtr</c> 占位。曾经把 <c>LogfileHeader</c> 写成 <c>IntPtr</c>，
    /// 整个结构只有 176 字节，而原生需要 448 字节：
    /// <c>EventRecordCallback</c> 被写到偏移 152，原生从 <b>424</b> 读 → 读到垃圾指针 → 访问冲突。
    /// </remarks>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct EVENT_TRACE_LOGFILE
    {
        public IntPtr LogFileName;              // 0
        public IntPtr LoggerName;               // 8
        public long CurrentTime;                // 16
        public uint BuffersRead;                // 24
        public uint ProcessTraceMode;           // 28  (union: LogFileMode)
        public EVENT_TRACE CurrentEvent;        // 32  (按值内嵌，88 字节)
        public TRACE_LOGFILE_HEADER LogfileHeader; // 120 (按值内嵌，280 字节)
        public IntPtr BufferCallback;           // 400
        public uint BufferSize;                 // 408
        public uint Filled;                     // 412
        public uint EventsLost;                 // 416
        public IntPtr EventRecordCallback;      // 424
        public uint IsKernelTrace;              // 432
        public IntPtr Context;                  // 440
    }                                           // 合计 448

    // ---------------- 辅助 ----------------

    /// <summary>计算 EVENT_TRACE_PROPERTIES + LoggerName 所需的总字节数。</summary>
    public static int PropertiesSize(string sessionName) =>
        Marshal.SizeOf<EVENT_TRACE_PROPERTIES>() + (sessionName.Length + 1) * 2;

    /// <summary>把托管字符串写入非托管缓冲区。</summary>
    public static void WriteString(IntPtr basePtr, int offset, string value)
    {
        var bytes = System.Text.Encoding.Unicode.GetBytes(value + "\0");
        Marshal.Copy(bytes, 0, basePtr + offset, bytes.Length);
    }

    public static string ReadUnicodeString(IntPtr basePtr, int offset)
    {
        if (offset <= 0) return string.Empty;
        return Marshal.PtrToStringUni(basePtr + offset) ?? string.Empty;
    }
}
