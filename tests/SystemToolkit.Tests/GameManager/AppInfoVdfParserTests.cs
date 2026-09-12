using System.Buffers.Binary;
using System.Text;
using SystemToolkit.Core.GameManager.Models;
using SystemToolkit.Core.GameManager.Services;

namespace SystemToolkit.Tests.GameManager;

/// <summary>
/// B1 二进制 appinfo.vdf 解析器（2026-09-13 游戏管理试点批次 3）的回归。
/// <para>
/// 全部用例用**手工构造的字节流**（构造器在文件底部），不依赖本机是否装了 Steam——
/// 真机文件的核对结果见 <c>AppInfoVdfParser</c> 的类注释与变更记录。
/// </para>
/// <para>
/// 重点钉住三件"手点验不出、且参考实现恰好做错"的事：
/// ① <b>type 0 必须递归</b>——参考实现把它当无数据跳过，于是嵌套对象被拍平、并在第一个嵌套对象的
/// 结束标记处就终止解析；② 因此**不能依赖字段顺序**（本实现取根级，其次取 <c>common</c> 内）；
/// ③ 任何损坏输入都必须"少给数据"而不是"抛异常/错位乱解"。
/// </para>
/// </summary>
public class AppInfoVdfParserTests
{
    // ==================== 基本解析 ====================

    [Fact]
    public void Parse_V41_NestedCommon_ExtractsNameAndType()
    {
        byte[] file = new AppInfoFileBuilder(AppInfoVdfParser.MagicV41)
            .AddGame(1245620, "ELDEN RING", "Game")
            .AddGame(570, "Dota 2", "Game")
            .Build();

        IReadOnlyDictionary<uint, SteamAppInfoEntry> result = Parse(file);

        Assert.Equal(2, result.Count);
        Assert.Equal("ELDEN RING", result[1245620u].Name);
        Assert.Equal("Game", result[1245620u].Type);
        Assert.Equal("Dota 2", result[570u].Name);
    }

    [Fact]
    public void Parse_V41_LocalizedNamesAndHeaderImages_AreCaptured()
    {
        // 真机形态（2026-09-13 从本机 appinfo.vdf 实测）：中文名在 common.name_localized.schinese，
        // 头图在 common.header_image.<lang>，且新式头图带 hash 子目录。
        // ⚠️ blob 与文件**必须用同一个 builder**：v41 的 key 是字符串表索引，
        // 两个 builder 各持一张表 → 索引错位、字段全部读错（本用例首版就踩了这个）。
        var builder = new AppInfoFileBuilder(AppInfoVdfParser.MagicV41);
        byte[] blob = builder.Blob()
            .Object("appinfo")
            .Object("common")
            .Str("name", "Black Myth: Wukong")
            .Str("type", "Game")
            .Object("name_localized")
            .Str("english", "Black Myth: Wukong")
            .Str("schinese", "黑神话：悟空")
            .Str("tchinese", "黑神話：悟空")
            .End()
            .Object("header_image")
            .Str("english", "header.jpg")
            .Str("schinese", "523c28b76572f3ea7dd6decd94e0333c2502c26a/header_schinese.jpg")
            .End()
            .End()
            .End()
            .ToArray();

        byte[] file = builder.Add(2358720, blob).Build();

        SteamAppInfoEntry e = Parse(file)[2358720u];

        Assert.Equal("Black Myth: Wukong", e.Name); // name 恒为英文原名
        Assert.Equal("Game", e.Type);
        Assert.Equal("黑神话：悟空", e.NameSchinese);
        Assert.Equal("黑神話：悟空", e.NameTchinese);
        Assert.Equal("黑神话：悟空", e.ChineseName);
        // 头图优先简中（带 hash 子目录），英文那份也留着
        Assert.Equal("523c28b76572f3ea7dd6decd94e0333c2502c26a/header_schinese.jpg", e.PreferredHeaderImage);
        Assert.Equal("header.jpg", e.HeaderImageEnglish);
    }

    [Fact]
    public void Parse_V41_WithoutLocalizedFields_ChineseNameAndHeaderAreEmpty()
    {
        // Kingdom Rush 实测就没有 name_localized / header_image（只有 name）——
        // ChineseName 必须是空串而**不能回退成英文**，否则界面会把英文名当"中文名"用
        byte[] file = new AppInfoFileBuilder(AppInfoVdfParser.MagicV41)
            .AddGame(246420, "Kingdom Rush", "Game")
            .Build();

        SteamAppInfoEntry e = Parse(file)[246420u];

        Assert.Equal("Kingdom Rush", e.Name);
        Assert.Equal(string.Empty, e.ChineseName);
        Assert.Equal(string.Empty, e.PreferredHeaderImage);
    }

    [Fact]
    public void Parse_V41_FlatRootLevelNameAndType_AlsoWorks()
    {
        // 新客户端把 name/type 直接放根级（不经 common）
        byte[] file = new AppInfoFileBuilder(AppInfoVdfParser.MagicV41)
            .AddGame(440, "Team Fortress 2", "Game", nestedInCommon: false)
            .Build();

        IReadOnlyDictionary<uint, SteamAppInfoEntry> result = Parse(file);

        Assert.Equal("Team Fortress 2", result[440u].Name);
        Assert.Equal("Game", result[440u].Type);
    }

    [Fact]
    public void Parse_V40_InlineStringKeys_WorksWithoutStringTable()
    {
        // v40 的 key 是内联 C 字符串，无字符串表——与 v41 的分支必须都能走通
        byte[] file = new AppInfoFileBuilder(AppInfoVdfParser.MagicV40)
            .AddGame(220, "Half-Life 2", "Game")
            .Build();

        IReadOnlyDictionary<uint, SteamAppInfoEntry> result = Parse(file);

        Assert.Equal("Half-Life 2", result[220u].Name);
        Assert.Equal("Game", result[220u].Type);
    }

    // ==================== 与参考实现的关键差异（本批次的真正增量） ====================

    [Fact]
    public void Parse_RootLevelNameAfterNestedCommon_IsStillFound()
    {
        // 🔴 判别性用例：根级 name 排在嵌套 common **之后**。
        // 参考实现（把 type 0 当无数据）会在 common 的结束标记处就 break → 永远看不到这个 name。
        // 本实现正确递归，回到根字段层继续读 → 必须拿到它。
        var builder = new AppInfoFileBuilder(AppInfoVdfParser.MagicV41);
        byte[] blob = builder.Blob()
            .Object("appinfo")
                .Int("appid", 42)
                .Object("common").Str("type", "Game").End() // common 内只有 type
                .Str("name", "RootAfterCommon")             // 根级 name 在 common 之后
                .Int("public_only", 1)
            .End()                                          // 结束 appinfo
            .End()                                          // 真实文件同款的尾随结束标记
            .ToArray();
        byte[] file = builder.Add(42, blob).Build();

        IReadOnlyDictionary<uint, SteamAppInfoEntry> result = Parse(file);

        Assert.Equal("RootAfterCommon", result[42u].Name);
        Assert.Equal("Game", result[42u].Type); // common 内的 type 也要取到（两级查找）
    }

    [Fact]
    public void Parse_NameInsideUnrelatedObject_IsNotPickedUp()
    {
        // 只认「根级」与「common 内」两级——下游对象（如 extended）里的同名键不得冒领
        var builder = new AppInfoFileBuilder(AppInfoVdfParser.MagicV41);
        byte[] blob = builder.Blob()
            .Object("appinfo")
                .Int("appid", 43)
                .Object("common").Str("type", "Tool").End()
                .Object("extended").Str("name", "NotTheDisplayName").End()
            .End()
            .End()
            .ToArray();
        byte[] file = builder.Add(43, blob).Build();

        IReadOnlyDictionary<uint, SteamAppInfoEntry> result = Parse(file);

        Assert.Equal(string.Empty, result[43u].Name); // 宁可空（调用方退化为 App {id}），不取错名字
        Assert.Equal("Tool", result[43u].Type);
    }

    // ==================== 损坏/边界输入：少给数据，不抛异常、不错位 ====================

    [Fact]
    public void Parse_UnsupportedMagic_ReturnsEmpty()
    {
        byte[] file = new AppInfoFileBuilder(0x07564427).AddGame(1, "X", "Game").Build();

        Assert.Empty(Parse(file));
    }

    [Fact]
    public void Parse_TooShort_ReturnsEmpty()
    {
        Assert.Empty(Parse([1, 2, 3]));
        Assert.Empty(Parse(Array.Empty<byte>()));
    }

    [Fact]
    public void Parse_StringTableOffsetOutOfRange_KeepsEntriesButDropsNames()
    {
        // 表读不到时：条目仍要出来（AppID 是硬信息），名称退化为空——
        // 且**绝不能**把 4 字节索引当 C 字符串读（那会立刻错位）
        byte[] file = new AppInfoFileBuilder(AppInfoVdfParser.MagicV41)
            .AddGame(1245620, "ELDEN RING", "Game")
            .Build(strtabOffsetOverride: 999_999_999);

        IReadOnlyDictionary<uint, SteamAppInfoEntry> result = Parse(file);

        Assert.Single(result);
        Assert.True(result.ContainsKey(1245620u));
        Assert.Equal(string.Empty, result[1245620u].Name);
    }

    [Fact]
    public void Parse_TruncatedMidEntry_StopsAtLastCompleteEntry()
    {
        AppInfoFileBuilder builder = new AppInfoFileBuilder(AppInfoVdfParser.MagicV41)
            .AddGame(100, "First", "Game")
            .AddGame(200, "Second", "Game");
        byte[] full = builder.Build();
        byte[] cut = full[..(full.Length / 2)]; // 从中间砍断：第二个条目 + 字符串表都没了

        IReadOnlyDictionary<uint, SteamAppInfoEntry> result = Parse(cut);

        Assert.Single(result); // 只有第一个完整条目
        Assert.True(result.ContainsKey(100u));
    }

    [Fact]
    public void Parse_EntrySizeSmallerThanHeader_StopsCleanly()
    {
        AppInfoFileBuilder builder = new AppInfoFileBuilder(AppInfoVdfParser.MagicV41).AddGame(100, "X", "Game");
        byte[] file = builder.Build();
        // 把首个条目的 size 字段（偏移 16+4）改成 10（< 60，结构上不可能）
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(20, 4), 10);

        Assert.Empty(Parse(file));
    }

    [Fact]
    public void Parse_UnknownTypeByte_DoesNotThrowAndKeepsOtherEntries()
    {
        var builder = new AppInfoFileBuilder(AppInfoVdfParser.MagicV41);
        byte[] broken = builder.Blob()
            .Object("appinfo")
                .Int("appid", 55)
                .Raw(0x0F, "weird") // 未知类型：继续读必然错位 → 本条目应被放弃
            .End()
            .End()
            .ToArray();
        byte[] file = builder
            .Add(55, broken)
            .AddGame(56, "Healthy", "Game")
            .Build();

        IReadOnlyDictionary<uint, SteamAppInfoEntry> result = Parse(file);

        Assert.Equal(2, result.Count);
        Assert.Equal(string.Empty, result[55u].Name);   // 损坏条目：只丢名字，不丢 AppID
        Assert.Equal("Healthy", result[56u].Name);      // 后续条目不受影响
    }

    [Fact]
    public void Parse_DeeplyNestedObjects_DoesNotStackOverflow()
    {
        // 递归深度上限（MaxDepth=16）——损坏文件里出现超深嵌套时不得炸栈
        var builder = new AppInfoFileBuilder(AppInfoVdfParser.MagicV41);
        BlobBuilder blob = builder.Blob().Object("appinfo").Int("appid", 77);
        for (int i = 0; i < 300; i++)
        {
            blob = blob.Object("n" + i);
        }

        blob.Str("name", "TooDeep").End();
        for (int i = 0; i < 300; i++)
        {
            blob = blob.End();
        }

        byte[] file = builder.Add(77, blob.End().End().ToArray()).Build();

        IReadOnlyDictionary<uint, SteamAppInfoEntry> result = Parse(file);

        Assert.Single(result);
        Assert.Equal(string.Empty, result[77u].Name); // 超深层的 name 不参与捕获（职责边界）
    }

    [Fact]
    public void Parse_WideStringName_IsDecoded()
    {
        // type 5（UTF-16LE）也必须能取——真实文件里 name 是 type 1，
        // 但格式允许宽字符串，解析器不能只会一种
        var builder = new AppInfoFileBuilder(AppInfoVdfParser.MagicV41);
        byte[] blob = builder.Blob()
            .Object("appinfo")
                .Int("appid", 88)
                .Str("type", "Game")
                .WideStr("name", "宽字符 名称")
            .End()
            .End()
            .ToArray();
        byte[] file = builder.Add(88, blob).Build();

        Assert.Equal("宽字符 名称", Parse(file)[88u].Name);
    }

    [Fact]
    public void Parse_MissingFile_ReturnsEmptyWithoutThrowing()
    {
        Assert.Empty(AppInfoVdfParser.Parse(@"Z:\definitely\not\here\appinfo.vdf"));
        Assert.Empty(AppInfoVdfParser.Parse(string.Empty));
    }

    // ==================== 测试用二进制 VDF 构造器 ====================

    private static IReadOnlyDictionary<uint, SteamAppInfoEntry> Parse(byte[] data)
    {
        using var stream = new MemoryStream(data, writable: false);
        return AppInfoVdfParser.ParseSeekable(stream);
    }

    /// <summary>
    /// 按真实文件布局拼一个 appinfo.vdf：头(magic+universe[+strtab偏移]) + 条目 + 尾部 0 + 字符串表。
    /// </summary>
    private sealed class AppInfoFileBuilder
    {
        private readonly uint _magic;
        private readonly bool _v41;
        private readonly List<string> _stringTable = new();
        private readonly List<(uint AppId, byte[] Blob)> _entries = new();

        public AppInfoFileBuilder(uint magic)
        {
            _magic = magic;
            _v41 = magic == AppInfoVdfParser.MagicV41;
        }

        public BlobBuilder Blob() => new(_v41, _stringTable);

        public AppInfoFileBuilder Add(uint appId, byte[] blob)
        {
            _entries.Add((appId, blob));
            return this;
        }

        /// <summary>加一个「正常」条目（appinfo 包裹 + appid + name/type，可嵌套在 common 内）。</summary>
        public AppInfoFileBuilder AddGame(uint appId, string name, string type, bool nestedInCommon = true)
        {
            BlobBuilder blob = Blob().Object("appinfo").Int("appid", (int)appId);
            blob = nestedInCommon
                ? blob.Object("common").Str("name", name).Str("type", type).End()
                : blob.Str("name", name).Str("type", type);
            return Add(appId, blob.End().End().ToArray());
        }

        public byte[] Build(long? strtabOffsetOverride = null)
        {
            var file = new List<byte>();
            WriteU32(file, _magic);
            WriteU32(file, 1u); // universe
            if (_v41)
            {
                WriteU64(file, 0UL); // 占位，稍后回填
            }

            foreach ((uint appId, byte[] blob) in _entries)
            {
                WriteU32(file, appId);
                WriteU32(file, (uint)(60 + blob.Length)); // size = 60 字节元数据 + blob
                file.AddRange(new byte[60]);              // 元数据内容不参与解析
                file.AddRange(blob);
            }

            WriteU32(file, 0u); // 文件尾哨兵（appid == 0）

            if (_v41)
            {
                long tableOffset = file.Count;
                Span<byte> buffer = stackalloc byte[8];
                BinaryPrimitives.WriteUInt64LittleEndian(buffer, (ulong)(strtabOffsetOverride ?? tableOffset));
                for (int i = 0; i < 8; i++)
                {
                    file[8 + i] = buffer[i];
                }
            }

            WriteU32(file, (uint)_stringTable.Count);
            foreach (string item in _stringTable)
            {
                file.AddRange(Encoding.UTF8.GetBytes(item));
                file.Add(0);
            }

            return file.ToArray();
        }

        private static void WriteU32(List<byte> target, uint value)
        {
            Span<byte> buffer = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
            target.AddRange(buffer.ToArray());
        }

        private static void WriteU64(List<byte> target, ulong value)
        {
            Span<byte> buffer = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
            target.AddRange(buffer.ToArray());
        }
    }

    /// <summary>单个条目的二进制 VDF 体。</summary>
    private sealed class BlobBuilder
    {
        private readonly bool _v41;
        private readonly List<string> _stringTable;
        private readonly List<byte> _bytes = new();

        public BlobBuilder(bool v41, List<string> stringTable)
        {
            _v41 = v41;
            _stringTable = stringTable;
        }

        public BlobBuilder Object(string key)
        {
            _bytes.Add(0);
            WriteKey(key);
            return this;
        }

        public BlobBuilder End()
        {
            _bytes.Add(8);
            return this;
        }

        public BlobBuilder Int(string key, int value)
        {
            _bytes.Add(2);
            WriteKey(key);
            Span<byte> buffer = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)value);
            _bytes.AddRange(buffer.ToArray());
            return this;
        }

        public BlobBuilder Str(string key, string value)
        {
            _bytes.Add(1);
            WriteKey(key);
            WriteCString(value);
            return this;
        }

        public BlobBuilder WideStr(string key, string value)
        {
            _bytes.Add(5);
            WriteKey(key);
            foreach (char c in value)
            {
                _bytes.AddRange(BitConverter.GetBytes((ushort)c));
            }

            _bytes.Add(0);
            _bytes.Add(0);
            return this;
        }

        /// <summary>只写 type + key，不写值（用于构造"未知类型"这类损坏输入）。</summary>
        public BlobBuilder Raw(byte type, string key)
        {
            _bytes.Add(type);
            WriteKey(key);
            return this;
        }

        public byte[] ToArray() => _bytes.ToArray();

        private void WriteKey(string key)
        {
            if (!_v41)
            {
                WriteCString(key);
                return;
            }

            int index = _stringTable.IndexOf(key);
            if (index < 0)
            {
                _stringTable.Add(key);
                index = _stringTable.Count - 1;
            }

            Span<byte> buffer = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)index);
            _bytes.AddRange(buffer.ToArray());
        }

        private void WriteCString(string value)
        {
            _bytes.AddRange(Encoding.UTF8.GetBytes(value));
            _bytes.Add(0);
        }
    }
}
