using CWriteWatcher.Etw.Native;

namespace CWriteWatcher.Etw.Parsing;

/// <summary>一个 Kernel-File 事件应该触发什么动作。</summary>
internal enum KernelFileAction
{
    /// <summary>不关心的事件。</summary>
    Ignore = 0,

    /// <summary>建立 / 更新 FileKey→路径 映射。</summary>
    NameSet,

    /// <summary>移除映射（名字被移除 / 句柄关闭 / 文件被删）。</summary>
    NameRemove,

    /// <summary>新建文件：建立映射，并标记为"新建"（供模式 A 的"仅提醒新建文件"使用）。</summary>
    NewFile,

    /// <summary>写入事件。</summary>
    Write,

    /// <summary>文件被截断到 0 字节（SetInformation + FileEndOfFileInformation）。</summary>
    Truncate,
}

/// <summary>
/// 一个事件的路由结果：要做什么动作、以及用哪个键去操作映射表。
/// </summary>
/// <remarks>
/// <para><b>为什么需要两个键：</b>Kernel-File 的字段分布很不一致 ——
/// <c>Create(12)</c> 只给 <c>FileObject</c>，<c>NameCreate(10)</c> 只给 <c>FileKey</c>，
/// 而 <c>Write(16)</c> 两个都给。所以映射表会被两个键空间同时填充，
/// 查询时也必须按顺序试两个键。</para>
/// </remarks>
internal readonly struct KernelFileRoute
{
    public readonly KernelFileAction Action;

    /// <summary>首选键。</summary>
    public readonly ulong PrimaryKey;

    /// <summary>备用键；<c>0</c> 表示没有。</summary>
    public readonly ulong SecondaryKey;

    public KernelFileRoute(KernelFileAction action, ulong primaryKey, ulong secondaryKey = 0)
    {
        Action = action;
        PrimaryKey = primaryKey;
        SecondaryKey = secondaryKey;
    }

    public bool HasSecondary => SecondaryKey != 0;

    public static readonly KernelFileRoute Ignore = new(KernelFileAction.Ignore, 0);

    /// <summary>
    /// 两个键（用于建映射、标新建、移除映射）。可直接 <c>foreach</c>，**零分配**。
    /// </summary>
    /// <remarks>
    /// ★ 不要改回 <c>IEnumerable&lt;ulong&gt;</c> + <c>yield return</c>：那样每次 <c>foreach</c>
    /// 都会分配一个迭代器对象，而这条路径在处理**被排除的写入**时每秒会走数万次
    /// （排除路径占写入事件的 90% 以上，设计上要求"零分配丢弃"）。
    /// 结构体枚举器让 <c>foreach</c> 在编译期就走 <c>GetEnumerator/MoveNext/Current</c>，
    /// 不装箱、不分配。
    /// </remarks>
    public KeyPair Keys => new(PrimaryKey, SecondaryKey);

    /// <summary>两个映射键的只读枚举器（结构体，无分配）。</summary>
    public readonly struct KeyPair
    {
        private readonly ulong _primary;
        private readonly ulong _secondary;

        public KeyPair(ulong primary, ulong secondary)
        {
            _primary = primary;
            _secondary = secondary;
        }

        public Enumerator GetEnumerator() => new(_primary, _secondary);

        public struct Enumerator
        {
            // 构造时就把「第一个有效键 / 第二个有效键」定下来：
            // primary 为 0 是常见情况（例如 Write 事件只有 FileKey），
            // 必须在构造阶段就跳到 secondary，否则 MoveNext 会在第一个键上直接返回 false 而漏掉。
            private readonly ulong _first;
            private readonly ulong _second;
            private int _index;

            public Enumerator(ulong primary, ulong secondary)
            {
                _index = -1;

                if (primary != 0)
                {
                    _first = primary;
                    _second = secondary != 0 && secondary != primary ? secondary : 0;
                }
                else
                {
                    _first = secondary;
                    _second = 0;
                }
            }

            public ulong Current => _index == 0 ? _first : _second;

            public bool MoveNext()
            {
                _index++;
                if (_index == 0) return _first != 0;
                if (_index == 1) return _second != 0;
                return false;
            }
        }
    }
}

/// <summary>
/// 把「事件 ID + 已解析字段」映射成「动作 + 键」。纯函数，不碰任何状态，因此可以单元测试。
/// </summary>
/// <remarks>
/// <para><b>这个类存在的唯一理由：</b>项目曾经把这段分发逻辑直接写在 ETW 回调里，
/// 结果出现了一个致命缺陷 —— <c>Create(12)</c> 事件的 payload 里**根本没有 FileKey 字段**，
/// 而代码却拿 <c>FileKey</c> 当键去建映射，于是 <c>key == 0</c> 直接返回，
/// FileKey→路径 映射表**永远是空的**，精确模式下所有写入都变成"未知路径"被丢弃 ——
/// 表现是「事件总数 1677 万、Write 事件 40 万、但一条提醒都不弹」，
/// 而且因为没有任何异常，107 个测试全绿。
/// </para>
/// <para>把这段逻辑变成纯函数之后，上面这个缺陷只需要一条断言就能拦住。</para>
/// </remarks>
internal static class KernelFileEventRouter
{
    public static KernelFileRoute Route(int eventId, in ParsedFields f)
    {
        var fileKey = unchecked((ulong)f.FileKey);
        var fileObject = unchecked((ulong)f.FileObject);

        switch (eventId)
        {
            // FileKey + FileName。这是 FileKey 空间唯一的官方来源。
            case EtwNative.EVENT_ID_NAME_CREATE:
                return new KernelFileRoute(KernelFileAction.NameSet, fileKey);

            // FileKey + FileName。文件的名字被移除（删除 / 改名前的旧名）。
            case EtwNative.EVENT_ID_NAME_DELETE:
                return new KernelFileRoute(KernelFileAction.NameRemove, fileKey);

            // ★ FileObject + FileName，**没有 FileKey**。
            //   必须用 FileObject 当键，用 FileKey 会恒为 0 而静默失效。
            case EtwNative.EVENT_ID_CREATE:
                return new KernelFileRoute(KernelFileAction.NameSet, fileObject, fileKey);

            // ★ 新建文件的权威信号（关键字 0x1000，必须订阅）。
            case EtwNative.EVENT_ID_CREATE_NEW_FILE:
                return new KernelFileRoute(KernelFileAction.NewFile, fileObject, fileKey);

            // 句柄关闭。只移除 FileObject 映射：
            //   FileObject 是内核对象地址，会被**快速回收**，不移除会污染后续文件；
            //   而 FileKey 是文件的持久标识，同一文件可能还有别的句柄在用，不能跟着删。
            case EtwNative.EVENT_ID_CLEANUP:
            case EtwNative.EVENT_ID_CLOSE:
                return new KernelFileRoute(KernelFileAction.NameRemove, fileObject);

            // 改名 / 建链接：更新为新名字。
            case EtwNative.EVENT_ID_RENAME_PATH:
            case EtwNative.EVENT_ID_SET_LINK_PATH:
            case EtwNative.EVENT_ID_SET_LINK:
                return new KernelFileRoute(KernelFileAction.NameSet, fileKey, fileObject);

            // 路径被删除。
            case EtwNative.EVENT_ID_DELETE_PATH:
                return new KernelFileRoute(KernelFileAction.NameRemove, fileKey, fileObject);

            // 写入。两个键都试：FileObject 优先（与 Create 同属 FILEIO 事件族，
            // 生命周期一致），FileKey 兜底（来自 NameCreate）。
            case EtwNative.EVENT_ID_WRITE:
                return new KernelFileRoute(KernelFileAction.Write, fileObject, fileKey);

            // SetInformation 里 InfoClass == FileEndOfFileInformation 表示改文件长度。
            // 截断到 0 说明文件被"清空重写"，此时应取消它的"新建"标记。
            case EtwNative.EVENT_ID_SET_INFORMATION:
                return f.InfoClass == EtwNative.FILE_END_OF_FILE_INFORMATION
                    ? new KernelFileRoute(KernelFileAction.Truncate, fileObject, fileKey)
                    : KernelFileRoute.Ignore;

            default:
                return KernelFileRoute.Ignore;
        }
    }
}
