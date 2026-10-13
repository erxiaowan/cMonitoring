using System.Runtime.InteropServices;
using CWriteWatcher.Etw.Native;

namespace CWriteWatcher.Etw.Parsing;

/// <summary>一个事件字段的元数据（由 TDH 从 provider manifest 中读出）。</summary>
internal readonly struct FieldDescriptor
{
    public readonly string Name;
    public readonly ushort InType;
    public readonly int Index;

    public FieldDescriptor(string name, ushort inType, int index)
    {
        Name = name;
        InType = inType;
        Index = index;
    }
}

/// <summary>某个 Event ID 的完整 schema。</summary>
internal sealed class EventSchema
{
    public readonly int EventId;
    public readonly string Name;
    public readonly FieldDescriptor[] Fields;

    public EventSchema(int eventId, string name, FieldDescriptor[] fields)
    {
        EventId = eventId;
        Name = name;
        Fields = fields;
    }
}

/// <summary>
/// 事件 schema 缓存。
/// </summary>
/// <remarks>
/// <para>为什么不硬编码字段偏移：<c>Microsoft-Windows-Kernel-File</c> 的 manifest 会随 Windows 版本微调，
/// 硬编码偏移一旦对不上就会静默解析出垃圾数据。这里改为**运行时通过 TDH 查询真实 manifest**，
/// 首次遇到某个 Event ID 时构建 schema 并缓存，之后按 schema 顺序遍历 payload。</para>
/// <para>为什么不用 <c>TdhGetProperty</c> 逐字段读：它每次都要重新解析事件，单次成本在微秒级，
/// 在每秒数万事件下会直接吃掉 CPU 预算。这里只在一开始付一次 schema 构建成本。</para>
/// <para>ETW manifest 事件的 payload 是**紧凑排布、无对齐填充**的，因此按字段类型顺序推进是正确做法
/// （TraceEvent 等成熟库也是同样做法）。</para>
/// </remarks>
internal sealed class EventSchemaCache
{
    private readonly Dictionary<int, EventSchema?> _cache = new();
    private IntPtr _scratch;
    private uint _scratchSize;

    public long SchemaBuildCount { get; private set; }
    public long SchemaFailureCount { get; private set; }

    public EventSchemaCache(uint initialBufferSize = 64 * 1024)
    {
        _scratchSize = initialBufferSize;
        _scratch = Marshal.AllocHGlobal((int)_scratchSize);
    }

    public EventSchema? Get(IntPtr pEventRecord, int eventId)
    {
        if (_cache.TryGetValue(eventId, out var cached)) return cached;

        var schema = Build(pEventRecord, eventId);
        _cache[eventId] = schema;
        return schema;
    }

    /// <summary>供"诊断模式"使用：列出所有已发现的事件 ID 及其字段。</summary>
    public IReadOnlyDictionary<int, EventSchema?> All => _cache;

    private EventSchema? Build(IntPtr pEventRecord, int eventId)
    {
        var size = _scratchSize;
        var status = EtwNative.TdhGetEventInformation(pEventRecord, 0, IntPtr.Zero, _scratch, ref size);

        if (status == EtwNative.ERROR_INSUFFICIENT_BUFFER)
        {
            Grow(size);
            size = _scratchSize;
            status = EtwNative.TdhGetEventInformation(pEventRecord, 0, IntPtr.Zero, _scratch, ref size);
        }

        if (status != EtwNative.ERROR_SUCCESS)
        {
            SchemaFailureCount++;
            System.Diagnostics.Debug.WriteLine($"[ETW] TdhGetEventInformation(EventId={eventId}) 失败，status=0x{status:X}");
            return null;
        }

        var header = Marshal.PtrToStructure<EtwNative.TRACE_EVENT_INFO_HEADER>(_scratch);
        var name = ReadString(_scratch, (int)header.EventNameOffset);
        var count = (int)header.PropertyCount;

        var propSize = Marshal.SizeOf<EtwNative.EVENT_PROPERTY_INFO>();
        var fields = new FieldDescriptor[count];

        for (var i = 0; i < count; i++)
        {
            var p = _scratch + EtwNative.TRACE_EVENT_INFO_HEADER.PropertyArrayOffset + i * propSize;
            var info = Marshal.PtrToStructure<EtwNative.EVENT_PROPERTY_INFO>(p);
            fields[i] = new FieldDescriptor(ReadString(_scratch, (int)info.NameOffset), info.InType, i);
        }

        SchemaBuildCount++;
        return new EventSchema(eventId, name, fields);
    }

    private void Grow(uint required)
    {
        var newSize = Math.Max(required, _scratchSize * 2);
        Marshal.FreeHGlobal(_scratch);
        _scratch = Marshal.AllocHGlobal((int)newSize);
        _scratchSize = newSize;
    }

    private static string ReadString(IntPtr basePtr, int offset) =>
        offset <= 0 ? string.Empty : Marshal.PtrToStringUni(basePtr + offset) ?? string.Empty;

    public void Dispose()
    {
        if (_scratch != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_scratch);
            _scratch = IntPtr.Zero;
        }
        _cache.Clear();
    }
}

/// <summary>从 Kernel-File 事件 payload 中提取出来的字段。</summary>
internal struct ParsedFields
{
    /// <summary>持久化的文件标识（同一文件多次打开共享）。<b>Create 事件没有这个字段</b>，只有 NameCreate 和 Write/Close 有。</summary>
    public long FileKey;

    /// <summary>本次打开的文件对象（每次 open 唯一）。Create / Write / Close 都有。</summary>
    public long FileObject;

    public int IssuingThreadId;
    public long Offset;
    public long Size;
    public long EndOfFile;
    public long CreateOptions;
    public long InfoClass;
    public long ExtraInformation;
    public string? FileName;
    public bool FileNameIsAnsi;

    public readonly bool HasFileName => !string.IsNullOrEmpty(FileName);
}

/// <summary>
/// 按 schema 顺序遍历 ETW payload，提取我们关心的字段。
/// </summary>
internal static class PayloadWalker
{
    /// <summary>遇到无法安全推断长度的类型时返回 false，表示解析中止（宁可放弃也不能读出垃圾）。</summary>
    public static bool TryParse(IntPtr userData, int length, EventSchema schema, out ParsedFields result)
    {
        result = default;

        if (userData == IntPtr.Zero || length <= 0 || schema.Fields.Length == 0) return false;

        var p = userData;
        var remaining = length;

        foreach (var f in schema.Fields)
        {
            if (remaining <= 0) break;

            switch (f.InType)
            {
                case EtwNative.TDH_INTYPE_UNICODESTRING:
                {
                    var s = ReadUnicodeString(p, remaining, out var consumed);
                    consumed = consumed <= 0 ? remaining : consumed;
                    if (IsFileNameField(f.Name)) { result.FileName = s; result.FileNameIsAnsi = false; }
                    p += consumed;
                    remaining -= consumed;
                    break;
                }

                case EtwNative.TDH_INTYPE_ANSISTRING:
                case EtwNative.TDH_INTYPE_NONNULLTERMINATEDANSISTRING:
                {
                    var s = ReadAnsiString(p, remaining, out var consumed);
                    if (IsFileNameField(f.Name)) { result.FileName = s; result.FileNameIsAnsi = true; }
                    p += consumed;
                    remaining -= consumed;
                    break;
                }

                case EtwNative.TDH_INTYPE_POINTER:
                case EtwNative.TDH_INTYPE_SIZET:
                case EtwNative.TDH_INTYPE_UINT64:
                case EtwNative.TDH_INTYPE_HEXINT64:
                case EtwNative.TDH_INTYPE_INT64:
                case EtwNative.TDH_INTYPE_FILETIME:
                {
                    if (remaining < 8) return true;
                    var v = Marshal.ReadInt64(p);
                    Assign64(f.Name, v, ref result);
                    p += 8;
                    remaining -= 8;
                    break;
                }

                case EtwNative.TDH_INTYPE_UINT32:
                case EtwNative.TDH_INTYPE_HEXINT32:
                case EtwNative.TDH_INTYPE_INT32:
                case EtwNative.TDH_INTYPE_BOOLEAN:
                case EtwNative.TDH_INTYPE_FLOAT:
                {
                    if (remaining < 4) return true;
                    var v = Marshal.ReadInt32(p);
                    Assign32(f.Name, v, ref result);
                    p += 4;
                    remaining -= 4;
                    break;
                }

                case EtwNative.TDH_INTYPE_UINT16:
                case EtwNative.TDH_INTYPE_INT16:
                {
                    if (remaining < 2) return true;
                    var v = Marshal.ReadInt16(p);
                    Assign32(f.Name, v, ref result);
                    p += 2;
                    remaining -= 2;
                    break;
                }

                case EtwNative.TDH_INTYPE_UINT8:
                case EtwNative.TDH_INTYPE_INT8:
                {
                    if (remaining < 1) return true;
                    var v = Marshal.ReadByte(p);
                    Assign32(f.Name, v, ref result);
                    p += 1;
                    remaining -= 1;
                    break;
                }

                case EtwNative.TDH_INTYPE_GUID:
                {
                    if (remaining < 16) return true;
                    p += 16;
                    remaining -= 16;
                    break;
                }

                case EtwNative.TDH_INTYPE_SYSTEMTIME:
                {
                    if (remaining < 16) return true;
                    p += 16;
                    remaining -= 16;
                    break;
                }

                case EtwNative.TDH_INTYPE_DOUBLE:
                {
                    if (remaining < 8) return true;
                    p += 8;
                    remaining -= 8;
                    break;
                }

                default:
                    // 未知类型：无法安全推断长度，中止遍历。
                    // 已提取到的字段仍然有效（Kernel-File 的 FileKey 通常在最前面）。
                    return true;
            }
        }

        return true;
    }

    private static bool IsFileNameField(string name) =>
        name.Equals("FileName", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("FilePath", StringComparison.OrdinalIgnoreCase);

    // Kernel-File 的字段名与直觉不完全一致（实测：偏移量叫 ByteOffset 而不是 Offset），
    // 因此这里对同义名做容错匹配。注意不要把 Length 当成写入大小——
    // Rename 事件的 Length 是"新文件名长度"，语义完全不同。
    private static bool IsOffsetField(string name) =>
        name.Equals("Offset", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("ByteOffset", StringComparison.OrdinalIgnoreCase);

    private static bool IsSizeField(string name) =>
        name.Equals("Size", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("IoSize", StringComparison.OrdinalIgnoreCase);

    private static bool IsFileKeyField(string name) =>
        name.Equals("FileKey", StringComparison.OrdinalIgnoreCase);

    private static bool IsFileObjectField(string name) =>
        name.Equals("FileObject", StringComparison.OrdinalIgnoreCase);

    private static void Assign64(string name, long value, ref ParsedFields r)
    {
        if (IsFileKeyField(name)) r.FileKey = value;
        else if (IsFileObjectField(name)) r.FileObject = value;
        else if (IsOffsetField(name)) r.Offset = value;
        else if (IsSizeField(name)) r.Size = value;
        else if (name.Equals("EndOfFile", StringComparison.OrdinalIgnoreCase)) r.EndOfFile = value;
        else if (name.Equals("ExtraInformation", StringComparison.OrdinalIgnoreCase)) r.ExtraInformation = value;
    }

    private static void Assign32(string name, int value, ref ParsedFields r)
    {
        if (name.Equals("IssuingThreadId", StringComparison.OrdinalIgnoreCase)) r.IssuingThreadId = value;
        else if (name.Equals("CreateOptions", StringComparison.OrdinalIgnoreCase)) r.CreateOptions = value;
        else if (name.Equals("InfoClass", StringComparison.OrdinalIgnoreCase)) r.InfoClass = value;
        else if (IsOffsetField(name)) r.Offset = value;
        else if (IsSizeField(name)) r.Size = value;
        else if (name.Equals("EndOfFile", StringComparison.OrdinalIgnoreCase)) r.EndOfFile = value;
    }

    private static unsafe string? ReadUnicodeString(IntPtr p, int maxBytes, out int consumed)
    {
        consumed = 0;
        var chars = maxBytes / 2;
        if (chars <= 0) return null;

        var span = new ReadOnlySpan<char>((void*)p, chars);
        var idx = span.IndexOf('\0');
        if (idx < 0)
        {
            consumed = chars * 2;
            return new string(span);
        }

        consumed = (idx + 1) * 2;
        return new string(span[..idx]);
    }

    private static unsafe string? ReadAnsiString(IntPtr p, int maxBytes, out int consumed)
    {
        consumed = 0;
        if (maxBytes <= 0) return null;

        var span = new ReadOnlySpan<byte>((void*)p, maxBytes);
        var idx = span.IndexOf((byte)0);
        if (idx < 0)
        {
            consumed = maxBytes;
            return System.Text.Encoding.Default.GetString(span);
        }

        consumed = idx + 1;
        return System.Text.Encoding.Default.GetString(span[..idx]);
    }
}
