using System.Buffers.Binary;
using System.Text;
using SystemToolkit.Core.GameManager.Models;
using SystemToolkit.Core.Utilities;

namespace SystemToolkit.Core.GameManager.Services;

/// <summary>
/// <c>appinfo.vdf</c>（Steam 的「应用元数据」缓存，**二进制** VDF）解析器。
/// <para>
/// 用途单一：为「有游玩记录但已卸载」的库存条目补出**显示名**——这是本地唯一的名称来源
/// （<c>appmanifest_*.acf</c> 只覆盖已安装游戏，localconfig.vdf 里只有 appid 没有名字）。
/// </para>
/// <para>
/// 🔴 <b>格式已按真实文件核对</b>（2026-09-13 实测本机 <c>d:\steam\appcache\appinfo.vdf</c>，
/// 2,197,260 字节；magic=<c>0x07564429</c> → v41；字符串表 5009 项）：
/// 文件头 = magic(u32) + universe(u32)，v41 再加字符串表偏移(u64)；
/// 条目 = appid(u32) + size(u32) + 60 字节元数据 + 二进制 VDF(size−60)，
/// 下一条目位于「size 字段之后 + size」；appid==0 为文件尾。
/// 首条目 appid=5 的 blob 实测为
/// <c>00 00 00 00 00 | 02 01 00 00 00 05 00 00 00 | 02 02 00 00 00 01 00 00 00 | 08 08</c>
/// ——即 type0+key("appinfo") 包一层，内部是 appid / public_only 两个 int32。
/// </para>
/// <para>
/// 🔴 <b>与参考实现（<c>NexBox/src-tauri/src/steam.rs</c> 的 <c>BvdfReader</c>）的一处关键差异</b>：
/// 参考实现把 type 0（嵌套对象开始）当作「无数据」直接跳过，于是 (a) 嵌套对象被**拍平**进父层、
/// (b) 在**第一个**嵌套对象的结束标记处就终止解析。它在真实文件上"能用"属巧合——
/// <c>name</c>/<c>type</c> 恰好都在 <c>common</c> 里、且位于该结束标记之前。
/// 本实现按格式**正确递归**，据此做两级查找（根级 → <c>common</c> 内），因此不依赖字段顺序；
/// 实测两套布局（新客户端扁平 / 旧客户端 <c>common</c> 子节点）都能取到。
/// </para>
/// <para>
/// 内存策略：<b>不整文件驻留</b>——逐条目读取，峰值 = 单条目 blob（超过 <see cref="MaxEntryBlobBytes"/>
/// 的巨型条目只跳过、不解析）；字符串表单独物化（它本身就是一张字符串列表）。
/// 任何失败一律返回空集、<b>不抛异常</b>（由调用方按「名称缺失」降级并记日志）。
/// </para>
/// </summary>
public static class AppInfoVdfParser
{
    /// <summary>v40 魔数（条目 key 为内联 C 字符串）。</summary>
    public const uint MagicV40 = 0x07564428;

    /// <summary>v41 魔数（头部多一个字符串表偏移，条目 key 为字符串表索引）。</summary>
    public const uint MagicV41 = 0x07564429;

    // ---------------- 二进制 VDF 类型字节（Valve 格式，已按真实文件核对） ----------------

    /// <summary>嵌套对象开始：后跟 key，以 <see cref="TypeEnd"/> 收尾。</summary>
    private const byte TypeObject = 0;

    /// <summary>字符串（UTF-8 + <c>\0</c>）。</summary>
    private const byte TypeString = 1;

    /// <summary>32 位有符号整数。</summary>
    private const byte TypeInt32 = 2;

    /// <summary>32 位浮点（占 4 字节，不解析）。</summary>
    private const byte TypeFloat32 = 3;

    /// <summary>指针（无数据）。</summary>
    private const byte TypePointer = 4;

    /// <summary>宽字符串（UTF-16LE + <c>\0</c>）。</summary>
    private const byte TypeWideString = 5;

    /// <summary>颜色（占 4 字节，不解析）。</summary>
    private const byte TypeColor = 6;

    /// <summary>64 位无符号整数。</summary>
    private const byte TypeUInt64 = 7;

    /// <summary>对象结束。</summary>
    private const byte TypeEnd = 8;

    /// <summary>64 位无符号整数的备用编码（与 <see cref="TypeUInt64"/> 同样布局）。</summary>
    private const byte TypeUInt64Alt = 9;

    // ---------------- 边界上限（防损坏文件把解析器拖死 / 撑爆内存） ----------------

    /// <summary>单条目 60 字节元数据部分。</summary>
    private const int EntryHeaderSize = 60;

    /// <summary>嵌套递归深度上限（真实文件 ≤5 层；超过即视为损坏，停止下潜）。</summary>
    private const int MaxDepth = 16;

    /// <summary>条目数上限（防止损坏文件造成的无界循环）。</summary>
    private const int MaxEntryCount = 500_000;

    /// <summary>单条目 blob 上限：超过则跳过解析但仍正确前进（防止单个巨条目撑爆内存）。</summary>
    private const int MaxEntryBlobBytes = 4 * 1024 * 1024;

    /// <summary>字符串表项数上限。</summary>
    private const int MaxStringTableCount = 4_000_000;

    /// <summary>v41 包裹层的 key（实测为 <c>appinfo</c>）；用它区分「包裹对象」与「首个字段就是嵌套对象」。</summary>
    private const string RootWrapperKey = "appinfo";

    /// <summary>解析结果：appId → 条目（名称 / 类型）。</summary>
    /// <param name="path">appinfo.vdf 路径。</param>
    /// <returns>解析成功的条目；文件不存在、版本不支持、结构损坏时返回空字典（不抛异常）。</returns>
    public static IReadOnlyDictionary<uint, SteamAppInfoEntry> Parse(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return EmptyResult;
        }

        try
        {
            // FileShare.ReadWrite：Steam 客户端运行时会持有该文件，独占打开必然失败
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return ParseSeekable(stream);
        }
        catch (Exception)
        {
            // 不抛：调用方的降级路径是「名称退化为 App {id}」，属可见行为（非静默失败）
            return EmptyResult;
        }
    }

    /// <summary>
    /// 从可定位流解析（<see cref="Stream.CanSeek"/> 必须为 true：字符串表位于文件尾部，
    /// 需先跳到尾部读表、再跳回条目区）。仅用于测试与内部复用。
    /// </summary>
    internal static IReadOnlyDictionary<uint, SteamAppInfoEntry> ParseSeekable(Stream stream)
    {
        try
        {
            if (!stream.CanSeek || stream.Length < 16)
            {
                return EmptyResult;
            }

            stream.Position = 0;
            if (!TryReadU32(stream, out uint magic) || !TryReadU32(stream, out _))
            {
                return EmptyResult;
            }

            IReadOnlyList<string>? stringTable = null;
            long entriesStart;
            if (magic == MagicV41)
            {
                if (!TryReadU64(stream, out ulong tableOffset))
                {
                    return EmptyResult;
                }

                // ⚠️ v41 的字符串表若是读不到（偏移越界/文件被截断），**不能**退回按 C 字符串读 key ——
                // 那会把 4 字节索引当字符串内容，立刻错位并把后续字段全部解错。
                // 用空表代替：key 解析为 ""（丢失名称）但索引仍被正确消费。v40 才传 null（内联 C 字符串）。
                stringTable = ReadStringTable(stream, tableOffset) ?? Array.Empty<string>();
                entriesStart = 16;
            }
            else if (magic == MagicV40)
            {
                entriesStart = 8; // v40 的 key 是内联 C 字符串，无字符串表
            }
            else
            {
                return EmptyResult; // 仅支持 v40 / v41
            }

            return ReadEntries(stream, entriesStart, stringTable);
        }
        catch (Exception)
        {
            return EmptyResult;
        }
    }

    /// <summary>空结果（只读包装，避免调用方把共享实例改坏）。</summary>
    private static readonly IReadOnlyDictionary<uint, SteamAppInfoEntry> EmptyResult =
        new System.Collections.ObjectModel.ReadOnlyDictionary<uint, SteamAppInfoEntry>(
            new Dictionary<uint, SteamAppInfoEntry>());

    // =====================================================================================
    // 字符串表（v41）
    // =====================================================================================

    private static IReadOnlyList<string>? ReadStringTable(Stream stream, ulong offset)
    {
        if (offset > (ulong)stream.Length || offset + 4 > (ulong)stream.Length)
        {
            return null;
        }

        stream.Position = (long)offset;
        if (!TryReadU32(stream, out uint count) || count > MaxStringTableCount)
        {
            return null;
        }

        // 表体一次性读入——它本身就是要物化成字符串列表的数据，逐条 Read 反而更慢
        long bodyStart = stream.Position;
        long bodyLength = stream.Length - bodyStart;
        if (bodyLength <= 0)
        {
            return null;
        }

        byte[] body = new byte[bodyLength];
        if (!ReadExactly(stream, body))
        {
            return null;
        }

        var list = new List<string>((int)Math.Min(count, 1_000_000));
        int pos = 0;
        for (uint i = 0; i < count && pos < body.Length; i++)
        {
            int end = Array.IndexOf(body, (byte)0, pos);
            if (end < 0)
            {
                break; // 无终止符：截断，停止（已读到的仍可用）
            }

            list.Add(Encoding.UTF8.GetString(body, pos, end - pos));
            pos = end + 1;
        }

        return list;
    }

    // =====================================================================================
    // 条目循环
    // =====================================================================================

    private static IReadOnlyDictionary<uint, SteamAppInfoEntry> ReadEntries(
        Stream stream,
        long entriesStart,
        IReadOnlyList<string>? stringTable)
    {
        var result = new Dictionary<uint, SteamAppInfoEntry>();
        long length = stream.Length;
        long pos = entriesStart;

        while (pos + 8 <= length && result.Count < MaxEntryCount)
        {
            stream.Position = pos;
            if (!TryReadU32(stream, out uint appId) || !TryReadU32(stream, out uint size))
            {
                break;
            }

            if (appId == 0)
            {
                break; // 文件尾哨兵
            }

            if (size < EntryHeaderSize)
            {
                break; // 结构上不可能小于 60 → 视为损坏，停止（不猜）
            }

            long entryStart = pos + 8;              // 60 字节元数据起点
            long blobStart = entryStart + EntryHeaderSize;
            long entryEnd = entryStart + size;
            if (entryEnd > length)
            {
                break; // 截断
            }

            int blobLength = (int)(size - EntryHeaderSize);
            if (blobLength > 0 && blobLength <= MaxEntryBlobBytes)
            {
                byte[] blob = new byte[blobLength];
                stream.Position = blobStart;
                if (ReadExactly(stream, blob))
                {
                    Capture(blob, stringTable, out string name, out string type);
                    result[appId] = new SteamAppInfoEntry { Name = name, Type = type };
                }
            }

            pos = entryEnd;
        }

        return result;
    }

    // =====================================================================================
    // 单条目 blob → (名称, 类型)
    // =====================================================================================

    private static void Capture(
        ReadOnlySpan<byte> blob,
        IReadOnlyList<string>? stringTable,
        out string name,
        out string type)
    {
        name = string.Empty;
        type = string.Empty;

        var reader = new BlobReader(blob);

        // 真实文件的 blob 以 type=0 + key("appinfo") 包一层，字段在其内部。
        // 但若首个字段本身就是嵌套对象（如直接以 common 开头），不能误吞它 ——
        // 故只有在 key 确为包裹键时才剥掉这层，否则回退到起点按字段序列解析。
        int mark = reader.Position;
        if (reader.TryReadByte(out byte first) && first == TypeObject)
        {
            string wrapper = reader.ReadKey(stringTable);
            if (!string.Equals(wrapper, RootWrapperKey, StringComparison.OrdinalIgnoreCase))
            {
                reader.Position = mark;
            }
        }
        else
        {
            reader.Position = mark;
        }

        var capture = default(CaptureState);

        // 返回值有意忽略：即使中途判定「该条目不可信」，已经捕获到的字段仍然可用（少给数据优于不给数据）
        Walk(ref reader, stringTable, level: 0, parentIsCommon: false, ref capture, depth: 0);

        name = TextSanitizer.StripInvisible(capture.Name) ?? string.Empty;
        type = capture.Type ?? string.Empty;
    }

    /// <summary>捕获状态（名称优先取根级，其次取 <c>common</c> 内；都只取第一次命中）。</summary>
    private struct CaptureState
    {
        public string? Name;
        public string? Type;
    }

    /// <summary>
    /// 顺序遍历一个对象体（读到 type=8 结束）。
    /// <para>
    /// <paramref name="level"/>：0 = <c>appinfo</c> 的直接字段层；1 = 某个子对象的字段层。
    /// 只有「level 0」与「level 1 且父键为 <c>common</c>」才参与捕获——更深的层级仍需递归
    /// （必须消费其字节才能定位下一个兄弟字段），但不取用。
    /// </para>
    /// <para>
    /// 🔴 返回值 = 「本次遍历是否正常走完」。<c>false</c> 表示**该条目已不可信**，调用方必须停止：
    /// 遇到越界、未知类型或超出 <see cref="MaxDepth"/> 时，**不能"就地返回"**——那会让父层
    /// 把尚未消费的嵌套内容当成兄弟字段继续读，从那一刻起全部字段错位（比"少一个名字"严重得多）。
    /// 超深情形放弃整个条目：既不错位，也不冒栈溢出的风险（真实文件深度 ≤5，上限 16 有充足余量）。
    /// </para>
    /// </summary>
    private static bool Walk(
        ref BlobReader reader,
        IReadOnlyList<string>? stringTable,
        int level,
        bool parentIsCommon,
        ref CaptureState capture,
        int depth)
    {
        if (depth > MaxDepth)
        {
            return false;
        }

        bool canCapture = level == 0 || parentIsCommon;

        while (true)
        {
            if (!reader.TryReadByte(out byte type))
            {
                return false; // 越界/截断
            }

            if (type == TypeEnd)
            {
                return true; // 本层正常结束
            }

            string key = reader.ReadKey(stringTable);
            bool isCommon = level == 0
                && string.Equals(key, "common", StringComparison.OrdinalIgnoreCase);

            switch (type)
            {
                case TypeObject:
                    if (!Walk(ref reader, stringTable, level + 1, isCommon, ref capture, depth + 1))
                    {
                        return false;
                    }

                    break;

                case TypeString:
                    ApplyCapture(ref capture, canCapture, key, reader.ReadCString());
                    break;

                case TypeWideString:
                    ApplyCapture(ref capture, canCapture, key, reader.ReadWideString());
                    break;

                case TypeInt32:
                case TypeFloat32:
                case TypeColor:
                    if (!reader.TrySkip(4))
                    {
                        return false;
                    }

                    break;

                case TypeUInt64:
                case TypeUInt64Alt:
                    if (!reader.TrySkip(8))
                    {
                        return false;
                    }

                    break;

                case TypePointer:
                    break; // 无数据

                default:
                    // 未知类型：继续读必然错位 → 放弃本条目（宁可少一个名字，不可乱解）
                    return false;
            }
        }
    }

    /// <summary>把「根级 / <c>common</c> 内」的 <c>name</c>、<c>type</c> 收进捕获状态（各只取第一次命中）。</summary>
    private static void ApplyCapture(ref CaptureState capture, bool canCapture, string key, string value)
    {
        if (!canCapture)
        {
            return;
        }

        if (capture.Name is null && key == "name")
        {
            capture.Name = value;
        }
        else if (capture.Type is null && key == "type")
        {
            capture.Type = value;
        }
    }

    // =====================================================================================
    // 底层读取工具
    // =====================================================================================

    private static bool TryReadU32(Stream stream, out uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        if (!ReadExactly(stream, buffer))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        return true;
    }

    private static bool TryReadU64(Stream stream, out ulong value)
    {
        Span<byte> buffer = stackalloc byte[8];
        if (!ReadExactly(stream, buffer))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt64LittleEndian(buffer);
        return true;
    }

    private static bool ReadExactly(Stream stream, Span<byte> buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = stream.Read(buffer[read..]);
            if (n <= 0)
            {
                return false;
            }

            read += n;
        }

        return true;
    }

    private static bool ReadExactly(Stream stream, byte[] buffer) => ReadExactly(stream, buffer.AsSpan());

    /// <summary>单个条目 blob 的顺序读取器（零分配：直接切片 <see cref="ReadOnlySpan{T}"/>）。</summary>
    private ref struct BlobReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _pos;

        public BlobReader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _pos = 0;
        }

        /// <summary>当前位置（用于「试读失败则回退」）。</summary>
        public int Position
        {
            get => _pos;
            set => _pos = value < 0 ? 0 : value;
        }

        public bool TryReadByte(out byte value)
        {
            if (_pos >= _data.Length)
            {
                value = 0;
                return false;
            }

            value = _data[_pos++];
            return true;
        }

        public bool TrySkip(int count)
        {
            if (count < 0 || _pos + count > _data.Length)
            {
                return false;
            }

            _pos += count;
            return true;
        }

        /// <summary>读取 C 风格字符串（UTF-8 + <c>\0</c>）；无终止符时取到末尾。</summary>
        public string ReadCString()
        {
            int end = _data[_pos..].IndexOf((byte)0);
            if (end < 0)
            {
                string tail = Encoding.UTF8.GetString(_data[_pos..]);
                _pos = _data.Length;
                return tail;
            }

            string value = Encoding.UTF8.GetString(_data.Slice(_pos, end));
            _pos += end + 1;
            return value;
        }

        /// <summary>读取宽字符串（UTF-16LE + <c>\0</c>）；无终止符时取到末尾。</summary>
        public string ReadWideString()
        {
            int p = _pos;
            while (p + 1 < _data.Length)
            {
                if (_data[p] == 0 && _data[p + 1] == 0)
                {
                    string value = Encoding.Unicode.GetString(_data.Slice(_pos, p - _pos));
                    _pos = p + 2;
                    return value;
                }

                p += 2;
            }

            int remaining = _data.Length - _pos;
            if (remaining % 2 != 0)
            {
                remaining--; // 奇数尾字节不构成一个 UTF-16 码元，丢弃
            }

            string rest = remaining > 0
                ? Encoding.Unicode.GetString(_data.Slice(_pos, remaining))
                : string.Empty;
            _pos = _data.Length;
            return rest;
        }

        /// <summary>
        /// 读取 key：v41 为字符串表索引(u32)，v40 为内联 C 字符串。
        /// 索引越界返回空串（不抛、不伪造 key）。
        /// </summary>
        public string ReadKey(IReadOnlyList<string>? stringTable)
        {
            if (stringTable is null)
            {
                return ReadCString();
            }

            if (_pos + 4 > _data.Length)
            {
                _pos = _data.Length;
                return string.Empty;
            }

            uint index = BinaryPrimitives.ReadUInt32LittleEndian(_data[_pos..]);
            _pos += 4;
            return index < (uint)stringTable.Count ? stringTable[(int)index] : string.Empty;
        }
    }
}
