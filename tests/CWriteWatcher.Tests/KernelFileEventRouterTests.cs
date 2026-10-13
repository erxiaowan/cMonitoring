using CWriteWatcher.Etw.Native;
using CWriteWatcher.Etw.Parsing;
using Xunit;

namespace CWriteWatcher.Tests;

/// <summary>
/// Kernel-File 事件分发逻辑的回归测试。
/// </summary>
/// <remarks>
/// <para><b>这个测试类存在的理由</b>：项目曾有一个让精确模式<b>完全失效</b>的缺陷 ——
/// <c>Create(12)</c> 事件的 payload 里<b>只有 <c>FileObject</c>，没有 <c>FileKey</c></b>
/// （这是本机 manifest 的事实，见下），而代码却统一用 <c>fields.FileKey</c> 当映射表的键，
/// 于是 <c>key == 0</c> 在第一行就 return，<b>FileKey→路径 映射表永远是空的</b>。</para>
/// <para>后果：所有 Write 事件都解析不出路径 → 全部被当作"未知路径"丢弃。
/// 诊断报告上的表现是「事件总数 1677 万 / Write 事件 40 万 / 接收事件里 100% 是未知路径 /
/// 一条提醒都不弹」，而且<b>没有任何异常、没有任何日志</b>。</para>
/// <para>当时 107 个测试全部通过 —— 因为这段分发逻辑埋在 ETW 回调里，无法被单元测试触及。
/// 把它抽成 <see cref="KernelFileEventRouter"/> 这个纯函数之后，一条断言就能拦住。</para>
/// </remarks>
public class KernelFileEventRouterTests
{
    // 本机 Windows 通过 `wevtutil gp Microsoft-Windows-Kernel-File /ge:true /f:xml`
    // 导出的 <task> 列表 —— 事件 ID ↔ 名称的权威对应关系。
    [Theory]
    [InlineData(10, "NameCreate")]
    [InlineData(11, "NameDelete")]
    [InlineData(12, "Create")]
    [InlineData(13, "Cleanup")]
    [InlineData(14, "Close")]
    [InlineData(15, "Read")]
    [InlineData(16, "Write")]
    [InlineData(17, "SetInformation")]
    [InlineData(18, "SetDelete")]
    [InlineData(19, "Rename")]
    [InlineData(20, "DirEnum")]
    [InlineData(21, "Flush")]
    [InlineData(22, "QueryInformation")]
    [InlineData(23, "FSCTL")]
    [InlineData(24, "OperationEnd")]
    [InlineData(25, "DirNotify")]
    [InlineData(26, "DeletePath")]
    [InlineData(27, "RenamePath")]
    [InlineData(28, "SetLinkPath")]
    [InlineData(29, "SetLink")]
    [InlineData(30, "CreateNewFile")]
    [InlineData(31, "SetSecurity")]
    [InlineData(32, "QuerySecurity")]
    [InlineData(33, "SetEA")]
    [InlineData(34, "QueryEA")]
    public void 事件ID与名称的对应关系必须与系统manifest一致(int eventId, string expected)
    {
        Assert.Equal(expected, EtwNative.EventIdName(eventId));
    }

    [Fact]
    public void 事件ID常量不得与manifest冲突()
    {
        Assert.Equal(10, EtwNative.EVENT_ID_NAME_CREATE);
        Assert.Equal(11, EtwNative.EVENT_ID_NAME_DELETE);
        Assert.Equal(12, EtwNative.EVENT_ID_CREATE);
        Assert.Equal(13, EtwNative.EVENT_ID_CLEANUP);
        Assert.Equal(14, EtwNative.EVENT_ID_CLOSE);
        Assert.Equal(15, EtwNative.EVENT_ID_READ);
        Assert.Equal(16, EtwNative.EVENT_ID_WRITE);
        Assert.Equal(17, EtwNative.EVENT_ID_SET_INFORMATION);
        Assert.Equal(18, EtwNative.EVENT_ID_SET_DELETE);
        Assert.Equal(19, EtwNative.EVENT_ID_RENAME);
        Assert.Equal(26, EtwNative.EVENT_ID_DELETE_PATH);
        Assert.Equal(27, EtwNative.EVENT_ID_RENAME_PATH);
        Assert.Equal(28, EtwNative.EVENT_ID_SET_LINK_PATH);
        Assert.Equal(29, EtwNative.EVENT_ID_SET_LINK);
        Assert.Equal(30, EtwNative.EVENT_ID_CREATE_NEW_FILE);
    }

    [Fact]
    public void 订阅关键字必须包含CreateNewFile()
    {
        // ★ 少了 CREATE_NEW_FILE(0x1000)，CreateNewFile(30) 事件根本不会到达，
        //   模式 A 的"仅提醒新建文件"就会把每一次写入都判成"修改已有文件"，
        //   结果是**一条提醒都不弹**（且没有报错）。
        Assert.NotEqual(0UL, EtwNative.SUBSCRIBE_KEYWORDS & EtwNative.KERNEL_FILE_KEYWORD_CREATE_NEW_FILE);

        // 改名/删除路径同样必须订阅，否则改名后映射表里还是旧路径。
        Assert.NotEqual(0UL, EtwNative.SUBSCRIBE_KEYWORDS & EtwNative.KERNEL_FILE_KEYWORD_RENAME_SETLINK_PATH);
        Assert.NotEqual(0UL, EtwNative.SUBSCRIBE_KEYWORDS & EtwNative.KERNEL_FILE_KEYWORD_DELETE_PATH);

        // 基础三件套。
        Assert.NotEqual(0UL, EtwNative.SUBSCRIBE_KEYWORDS & EtwNative.KERNEL_FILE_KEYWORD_FILENAME);
        Assert.NotEqual(0UL, EtwNative.SUBSCRIBE_KEYWORDS & EtwNative.KERNEL_FILE_KEYWORD_FILEIO);
        Assert.NotEqual(0UL, EtwNative.SUBSCRIBE_KEYWORDS & EtwNative.KERNEL_FILE_KEYWORD_CREATE);
    }

    // ---------------------------------------------------------------- 映射表的键

    [Fact]
    public void Create事件必须用FileObject当键_因为它根本没有FileKey()
    {
        // ★★ 这就是那个让精确模式完全失效的缺陷 ★★
        //   实测本机 manifest：Create(12) 的字段是
        //     Irp, FileObject, IssuingThreadId, CreateOptions, CreateAttributes, ShareAccess, FileName
        //   —— 没有 FileKey。所以这里 FileKey 必须是 0，FileObject 有值。
        var f = new ParsedFields
        {
            FileKey = 0,
            FileObject = 0x0000_7FFE_1234_5678,
            FileName = @"\Device\HarddiskVolume3\Users\me\a.bin",
        };

        var route = KernelFileEventRouter.Route(EtwNative.EVENT_ID_CREATE, in f);

        Assert.Equal(KernelFileAction.NameSet, route.Action);
        Assert.Equal(0x0000_7FFE_1234_5678UL, route.PrimaryKey);   // 必须是 FileObject
        Assert.NotEqual(0UL, route.PrimaryKey);                    // 绝不能是 0（否则静默失效）
    }

    [Fact]
    public void Create事件若误用FileKey会导致键为0()
    {
        // 反证：如果按"直觉"用 FileKey 当键，键就是 0，映射表永远建不起来。
        // 这条断言把"为什么不能用 FileKey"固化下来。
        var f = new ParsedFields { FileKey = 0, FileObject = 0xABCD, FileName = @"\Device\X\a.bin" };

        Assert.Equal(0L, f.FileKey);                               // Create 事件里 FileKey 恒为 0
        Assert.Equal(KernelFileAction.NameSet, KernelFileEventRouter.Route(12, in f).Action);
        Assert.NotEqual(unchecked((ulong)f.FileKey), KernelFileEventRouter.Route(12, in f).PrimaryKey);
    }

    [Fact]
    public void NameCreate事件必须用FileKey当键()
    {
        // NameCreate(10) 的字段是 FileKey + FileName —— 这是 FileKey 空间唯一的官方来源。
        var f = new ParsedFields
        {
            FileKey = 0x0000_0000_0000_1234,
            FileObject = 0,
            FileName = @"\Device\HarddiskVolume3\Users\me\b.bin",
        };

        var route = KernelFileEventRouter.Route(EtwNative.EVENT_ID_NAME_CREATE, in f);

        Assert.Equal(KernelFileAction.NameSet, route.Action);
        Assert.Equal(0x1234UL, route.PrimaryKey);
    }

    [Fact]
    public void Write事件必须同时给出两个候选键()
    {
        // Write(16) 同时带 FileObject 和 FileKey，两个键空间都要试。
        var f = new ParsedFields { FileKey = 0x11, FileObject = 0x22 };

        var route = KernelFileEventRouter.Route(EtwNative.EVENT_ID_WRITE, in f);

        Assert.Equal(KernelFileAction.Write, route.Action);
        Assert.Equal(0x22UL, route.PrimaryKey);     // FileObject 优先（与 Create 同属 FILEIO 事件族）
        Assert.Equal(0x11UL, route.SecondaryKey);   // FileKey 兜底（来自 NameCreate）
        Assert.Equal(new ulong[] { 0x22, 0x11 }, KeysOf(route));
    }

    [Fact]
    public void Write事件缺一个键时不应重复计数()
    {
        var f = new ParsedFields { FileKey = 0x33, FileObject = 0 };
        var route = KernelFileEventRouter.Route(EtwNative.EVENT_ID_WRITE, in f);

        Assert.Equal(new ulong[] { 0x33 }, KeysOf(route));
    }

    // ---------------------------------------------------------------- 新建文件

    [Fact]
    public void CreateNewFile必须产生NewFile动作()
    {
        // 模式 A 的"仅提醒新建文件"完全依赖这个动作去设置新建标记。
        var f = new ParsedFields { FileObject = 0x555, FileKey = 0x666, FileName = @"\Device\X\new.bin" };

        var route = KernelFileEventRouter.Route(EtwNative.EVENT_ID_CREATE_NEW_FILE, in f);

        Assert.Equal(KernelFileAction.NewFile, route.Action);
        Assert.Equal(0x555UL, route.PrimaryKey);
        Assert.Equal(0x666UL, route.SecondaryKey);
    }

    [Fact]
    public void 普通Create不应被当作新建文件()
    {
        // Create(12) 对每一次 open 都会触发，不能据此判定"新建"。
        var f = new ParsedFields { FileObject = 0x777, FileName = @"\Device\X\old.bin" };

        Assert.Equal(KernelFileAction.NameSet, KernelFileEventRouter.Route(EtwNative.EVENT_ID_CREATE, in f).Action);
    }

    // ---------------------------------------------------------------- 移除

    [Fact]
    public void Close只移除FileObject映射()
    {
        // FileObject 是内核对象地址，会被快速回收，必须移除，否则会污染后续文件。
        // 而 FileKey 是文件的持久标识，同一文件可能还有别的句柄在用，不能跟着删。
        var f = new ParsedFields { FileKey = 0xAA, FileObject = 0xBB };

        foreach (var id in new[] { EtwNative.EVENT_ID_CLEANUP, EtwNative.EVENT_ID_CLOSE })
        {
            var route = KernelFileEventRouter.Route(id, in f);
            Assert.Equal(KernelFileAction.NameRemove, route.Action);
            Assert.Equal(0xBBUL, route.PrimaryKey);
            Assert.Equal(0UL, route.SecondaryKey);          // 不能带 FileKey
            Assert.Equal(new ulong[] { 0xBB }, KeysOf(route));
        }
    }

    [Fact]
    public void NameDelete与DeletePath必须移除映射()
    {
        var f = new ParsedFields { FileKey = 0xCC, FileObject = 0xDD };

        Assert.Equal(KernelFileAction.NameRemove,
            KernelFileEventRouter.Route(EtwNative.EVENT_ID_NAME_DELETE, in f).Action);
        Assert.Equal(KernelFileAction.NameRemove,
            KernelFileEventRouter.Route(EtwNative.EVENT_ID_DELETE_PATH, in f).Action);
    }

    // ---------------------------------------------------------------- 改名 / 截断

    [Theory]
    [InlineData(27)] // RenamePath
    [InlineData(28)] // SetLinkPath
    [InlineData(29)] // SetLink
    public void 改名类事件必须更新映射(int eventId)
    {
        var f = new ParsedFields { FileKey = 0xE1, FileObject = 0xE2, FileName = @"\Device\X\renamed.bin" };
        Assert.Equal(KernelFileAction.NameSet, KernelFileEventRouter.Route(eventId, in f).Action);
    }

    [Fact]
    public void SetInformation只有InfoClass为文件长度时才产生截断动作()
    {
        var endOfFile = new ParsedFields { FileKey = 1, FileObject = 2, InfoClass = EtwNative.FILE_END_OF_FILE_INFORMATION };
        Assert.Equal(KernelFileAction.Truncate,
            KernelFileEventRouter.Route(EtwNative.EVENT_ID_SET_INFORMATION, in endOfFile).Action);

        var other = new ParsedFields { FileKey = 1, FileObject = 2, InfoClass = 3 /* FileBasicInformation */ };
        Assert.Equal(KernelFileAction.Ignore,
            KernelFileEventRouter.Route(EtwNative.EVENT_ID_SET_INFORMATION, in other).Action);
    }

    [Fact]
    public void 无关事件应当被忽略()
    {
        var f = new ParsedFields { FileKey = 1, FileObject = 2, FileName = @"\Device\X\a.bin" };

        Assert.Equal(KernelFileAction.Ignore, KernelFileEventRouter.Route(EtwNative.EVENT_ID_READ, in f).Action);
        Assert.Equal(KernelFileAction.Ignore, KernelFileEventRouter.Route(20, in f).Action); // DirEnum
        Assert.Equal(KernelFileAction.Ignore, KernelFileEventRouter.Route(24, in f).Action); // OperationEnd
        Assert.Equal(KernelFileAction.Ignore, KernelFileEventRouter.Route(999, in f).Action);
    }

    /// <summary>把路由的两个键物化成数组，便于断言（生产代码里 Keys 是零分配的 struct 枚举器）。</summary>
    private static ulong[] KeysOf(KernelFileRoute route)
    {
        var list = new List<ulong>();
        foreach (var k in route.Keys) list.Add(k);
        return list.ToArray();
    }

    [Fact]
    public void 遍历路由键不得产生堆分配()
    {
        // Write 事件的键（FileObject 优先、FileKey 兜底），两个键都有值。
        var route = KernelFileEventRouter.Route(
            EtwNative.EVENT_ID_WRITE, new ParsedFields { FileKey = 0x11, FileObject = 0x22 });

        // 充分预热，确保已进入 Tier1 JIT（否则 JIT 自身的分配会污染测量）。
        for (var i = 0; i < 200_000; i++)
        {
            foreach (var k in route.Keys) _ = k;
        }

        const int iterations = 100_000;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++)
        {
            foreach (var k in route.Keys) _ = k;
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // ★ 这里曾经是 IEnumerable<ulong> + yield return —— 每次 foreach 分配一个迭代器，
        //   10 万次循环就是约 240 KB 垃圾。而这条路径在处理「被排除的写入」时每秒走数万次
        //   （排除路径占写入事件的 90% 以上），设计上要求零分配。
        Assert.True(allocated <= 64,
            $"{iterations} 次遍历分配了 {allocated} 字节，应当接近 0 —— " +
            "Keys 被改回 IEnumerable/迭代器了？");
    }

    [Fact]
    public void DirEnum与DirNotify不得用于建立映射()
    {
        // DirEnum(20) / DirNotify(25) 的 FileName 是**目录名或搜索模式**，不是被写入的文件。
        // 用它们建映射会把错误路径写进映射表。
        var f = new ParsedFields { FileKey = 1, FileObject = 2, FileName = @"\Device\X\*.tmp" };

        Assert.Equal(KernelFileAction.Ignore, KernelFileEventRouter.Route(20, in f).Action);
        Assert.Equal(KernelFileAction.Ignore, KernelFileEventRouter.Route(25, in f).Action);
    }
}
