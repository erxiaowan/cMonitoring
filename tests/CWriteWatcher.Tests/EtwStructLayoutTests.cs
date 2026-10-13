using System.Runtime.InteropServices;
using CWriteWatcher.Etw;
using Xunit;

namespace CWriteWatcher.Tests;

/// <summary>
/// ETW 原生结构体的布局回归测试。
/// </summary>
/// <remarks>
/// <para>ETW 的 P/Invoke 结构体一旦多一个或少一个字段，<b>后续所有字段都会整体偏移</b>，
/// 而编译器与常规单元测试都发现不了——只会在真实调用时返回一个看不懂的错误码。</para>
/// <para>真实案例：<c>WNODE_HEADER</c> 里 <c>TimeStamp</c> 与 <c>KernelHandle</c> 在 Win32 中
/// <b>共用同一个 8 字节 union 槽</b>，但被误声明成两个独立字段，结构体从 48 字节变成 56 字节。
/// 结果 <c>LoggerNameOffset</c> 被写到偏移 128，而 <c>StartTrace</c> 从偏移 116 读 → 读到 0
/// （等于没有会话名）→ <b>StartTrace 返回 161 ERROR_BAD_PATHNAME</b>，精确模式直接起不来。</para>
/// <para>这个缺陷只在「以管理员身份运行精确模式」时才会暴露，普通单元测试永远碰不到。
/// 因此这里把与 Win32 SDK 对齐的尺寸和偏移直接钉死。</para>
/// </remarks>
public class EtwStructLayoutTests
{
    private static Type T(string nestedName) =>
        typeof(EtwKernelFileCollector).Assembly
            .GetType("CWriteWatcher.Etw.Native.EtwNative+" + nestedName, throwOnError: true)!;

    [Fact]
    public void WNODE_HEADER_必须是48字节()
    {
        Assert.Equal(48, Marshal.SizeOf(T("WNODE_HEADER")));
    }

    [Fact]
    public void WNODE_HEADER_各字段偏移必须与Win32一致()
    {
        var t = T("WNODE_HEADER");

        Assert.Equal(0, (int)Marshal.OffsetOf(t, "BufferSize"));
        Assert.Equal(4, (int)Marshal.OffsetOf(t, "ProviderId"));
        Assert.Equal(8, (int)Marshal.OffsetOf(t, "HistoricalContext"));
        Assert.Equal(16, (int)Marshal.OffsetOf(t, "KernelHandle")); // 与 TimeStamp 共用此槽
        Assert.Equal(24, (int)Marshal.OffsetOf(t, "Guid"));
        Assert.Equal(40, (int)Marshal.OffsetOf(t, "ClientContext"));
        Assert.Equal(44, (int)Marshal.OffsetOf(t, "Flags"));
    }

    [Fact]
    public void WNODE_HEADER_不得单独声明TimeStamp字段()
    {
        // TimeStamp 是 KernelHandle 那个 union 槽的另一种解释，不是独立字段。
        // 单独声明它会让结构体变成 56 字节，后续字段全部错位。
        Assert.Null(T("WNODE_HEADER").GetField("TimeStamp"));
    }

    [Fact]
    public void EVENT_TRACE_PROPERTIES_必须是120字节且关键偏移正确()
    {
        var t = T("EVENT_TRACE_PROPERTIES");

        Assert.Equal(120, Marshal.SizeOf(t));
        Assert.Equal(48, (int)Marshal.OffsetOf(t, "BufferSize"));
        Assert.Equal(64, (int)Marshal.OffsetOf(t, "LogFileMode"));
        Assert.Equal(112, (int)Marshal.OffsetOf(t, "LogFileNameOffset"));
        Assert.Equal(116, (int)Marshal.OffsetOf(t, "LoggerNameOffset"));

        // ★ 这两个是"丢失事件"监测要读的字段，偏移错了会静默读到垃圾值
        //   （表现为丢失计数乱跳或永远是 0），所以必须钉死。
        //   它们排在 FreeBuffers 之后、LoggerThreadId（8 字节对齐）之前。
        Assert.Equal(88, (int)Marshal.OffsetOf(t, "EventsLost"));
        Assert.Equal(100, (int)Marshal.OffsetOf(t, "RealTimeBuffersLost"));
    }

    [Fact]
    public void LoggerNameOffset必须是结构体的最后一个字段()
    {
        // StartTrace 靠这个偏移去缓冲区里读会话名。约定是：
        // 会话名紧跟在 EVENT_TRACE_PROPERTIES 之后，所以代码把 LoggerNameOffset
        // 设为 sizeof(EVENT_TRACE_PROPERTIES)。
        // 因此它必须是结构体里最后一个 4 字节字段，即 size - 4。
        // 一旦结构体多出字段，这个关系被破坏，StartTrace 就会读错位置 → 161 ERROR_BAD_PATHNAME。
        var props = T("EVENT_TRACE_PROPERTIES");
        var size = Marshal.SizeOf(props);
        var loggerNameOffset = (int)Marshal.OffsetOf(props, "LoggerNameOffset");

        Assert.Equal(size - 4, loggerNameOffset);
        Assert.Equal(120, size);
    }

    // ---------------------------------------------------------------- 事件消费侧

    [Fact]
    public void EVENT_DESCRIPTOR与EVENT_HEADER尺寸必须正确()
    {
        Assert.Equal(16, Marshal.SizeOf(T("EVENT_DESCRIPTOR")));
        Assert.Equal(80, Marshal.SizeOf(T("EVENT_HEADER")));
    }

    [Fact]
    public void EVENT_RECORD必须112字节且UserData偏移96()
    {
        var t = T("EVENT_RECORD");

        Assert.Equal(112, Marshal.SizeOf(t));
        Assert.Equal(0, (int)Marshal.OffsetOf(t, "EventHeader"));
        Assert.Equal(80, (int)Marshal.OffsetOf(t, "BufferContext"));
        Assert.Equal(84, (int)Marshal.OffsetOf(t, "ExtendedDataCount"));
        Assert.Equal(86, (int)Marshal.OffsetOf(t, "UserDataLength"));
        Assert.Equal(88, (int)Marshal.OffsetOf(t, "ExtendedData"));
        Assert.Equal(96, (int)Marshal.OffsetOf(t, "UserData"));
        Assert.Equal(104, (int)Marshal.OffsetOf(t, "UserContext"));
    }

    [Fact]
    public void EVENT_TRACE必须88字节_且内嵌EVENT_TRACE_HEADER()
    {
        Assert.Equal(48, Marshal.SizeOf(T("EVENT_TRACE_HEADER")));

        var t = T("EVENT_TRACE");
        Assert.Equal(88, Marshal.SizeOf(t));
        Assert.Equal(0, (int)Marshal.OffsetOf(t, "Header"));
    }

    [Fact]
    public void TRACE_LOGFILE_HEADER必须280字节()
    {
        // 含 TIME_ZONE_INFORMATION(172B) 等嵌套结构。
        // 用 StructLayout(Size=280) 钉死；写小一个字节都会让后面的回调指针错位。
        Assert.Equal(280, Marshal.SizeOf(T("TRACE_LOGFILE_HEADER")));
    }

    [Fact]
    public void EVENT_TRACE_LOGFILE必须448字节且回调指针偏移424()
    {
        // ★ 这里曾经把 LogfileHeader 声明成 IntPtr（8 字节）而不是按值内嵌的
        //   TRACE_LOGFILE_HEADER（280 字节），导致整个结构只有 176 字节。
        //   OpenTrace 从偏移 424 读 EventRecordCallback，读到垃圾指针，
        //   ProcessTrace 一调用就 c0000005 访问冲突。
        var t = T("EVENT_TRACE_LOGFILE");

        Assert.Equal(448, Marshal.SizeOf(t));

        Assert.Equal(0, (int)Marshal.OffsetOf(t, "LogFileName"));
        Assert.Equal(8, (int)Marshal.OffsetOf(t, "LoggerName"));
        Assert.Equal(28, (int)Marshal.OffsetOf(t, "ProcessTraceMode"));
        Assert.Equal(32, (int)Marshal.OffsetOf(t, "CurrentEvent"));
        Assert.Equal(120, (int)Marshal.OffsetOf(t, "LogfileHeader"));
        Assert.Equal(400, (int)Marshal.OffsetOf(t, "BufferCallback"));
        Assert.Equal(424, (int)Marshal.OffsetOf(t, "EventRecordCallback"));
        Assert.Equal(440, (int)Marshal.OffsetOf(t, "Context"));
    }

    [Fact]
    public void LogfileHeader必须是值类型而不是指针()
    {
        // 用 IntPtr 占位是最容易犯的错——它"能编译、能跑"，只是回调指针会错位。
        var field = T("EVENT_TRACE_LOGFILE").GetField("LogfileHeader")!;
        Assert.True(field.FieldType.IsValueType,
            $"LogfileHeader 必须是 TRACE_LOGFILE_HEADER 值类型，实际是 {field.FieldType.Name}");
        Assert.Equal("TRACE_LOGFILE_HEADER", field.FieldType.Name);
    }

    // ---------------------------------------------------------------- TDH schema 侧

    [Fact]
    public void TRACE_EVENT_INFO_HEADER必须112字节()
    {
        var t = T("TRACE_EVENT_INFO_HEADER");

        Assert.Equal(112, Marshal.SizeOf(t));
        Assert.Equal(100, (int)Marshal.OffsetOf(t, "PropertyCount"));
        Assert.Equal(104, (int)Marshal.OffsetOf(t, "TopLevelPropertyCount"));
        Assert.Equal(108, (int)Marshal.OffsetOf(t, "Flags"));
        Assert.Equal(112, (int)t.GetField("PropertyArrayOffset")!.GetRawConstantValue()!);
    }

    [Fact]
    public void EVENT_PROPERTY_INFO必须24字节()
    {
        // ★ 曾经漏掉 count / length / Reserved 三个 union，只声明成 16 字节。
        //   而 EventSchemaCache 是用 Marshal.SizeOf<EVENT_PROPERTY_INFO>() 当步长
        //   遍历 EventPropertyInfoArray 的 —— 步长偏小 8 字节后，
        //   每读 3 个条目才会正好落到一个真实条目边界（16×3 = 48 = 24×2），
        //   于是字段名只出现三分之一、其余全是垃圾（InType 变成 0/1）。
        var t = T("EVENT_PROPERTY_INFO");

        Assert.Equal(24, Marshal.SizeOf(t));
        Assert.Equal(0, (int)Marshal.OffsetOf(t, "Flags"));
        Assert.Equal(4, (int)Marshal.OffsetOf(t, "NameOffset"));
        Assert.Equal(8, (int)Marshal.OffsetOf(t, "InType"));
        Assert.Equal(10, (int)Marshal.OffsetOf(t, "OutType"));
        Assert.Equal(12, (int)Marshal.OffsetOf(t, "MapNameOffset"));
        Assert.Equal(16, (int)Marshal.OffsetOf(t, "Count"));
        Assert.Equal(18, (int)Marshal.OffsetOf(t, "Length"));
        Assert.Equal(20, (int)Marshal.OffsetOf(t, "Reserved"));
    }
}
