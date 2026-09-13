using System.Text.Json;
using SystemToolkit.Core.FileTransfer.Models;
using SystemToolkit.Core.FileTransfer.Services;
using SystemToolkit.Core.FileTransfer.Services.Protocol;

namespace SystemToolkit.Tests;

/// <summary>
/// FT-3 文本通道的**契约与判据**用例（B8a-1）。
/// <para>
/// 本类只测纯函数与序列化契约，**不启动传输服务** —— 服务层往返用例在 B8a-2。
/// 之所以先钉这一层：长度上限与预览截断是发送端、接收端、历史写入三处共用的判据，
/// 判据错了（例如按字符而非字节限长、或按码元硬切 emoji）会在三处同时错。
/// </para>
/// </summary>
public class TransferTextTests
{
    // ===================== 长度校验（按 UTF-8 字节） =====================

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public void Validate_BlankInput_IsRejectedAsEmpty(string? text)
    {
        TextValidation result = TransferText.Validate(text);

        Assert.False(result.IsValid);
        Assert.Equal(TextValidationKind.Empty, result.Kind);
        Assert.NotEmpty(result.ErrorText);
    }

    [Fact]
    public void Validate_ExactlyAtByteLimit_IsValid()
    {
        string text = new('a', TransferText.MaxBytes);

        TextValidation result = TransferText.Validate(text);

        Assert.True(result.IsValid);
        Assert.Equal(TransferText.MaxBytes, result.ByteCount);
    }

    [Fact]
    public void Validate_OneByteOverLimit_IsTooLong()
    {
        string text = new('a', TransferText.MaxBytes + 1);

        TextValidation result = TransferText.Validate(text);

        Assert.Equal(TextValidationKind.TooLong, result.Kind);
        Assert.Equal(TransferText.MaxBytes + 1, result.ByteCount);
    }

    [Fact]
    public void Validate_ChineseText_CountsBytesNotChars()
    {
        // 🔴 本用例钉的是"口径"本身：中文 1 字符 = 3 字节。
        // 87381 字 = 262143 B（≤ 上限）→ 通过；87382 字 = 262146 B → 超限。
        // 两个分支的**字符数都远小于 262144**，所以按字符限长的实现会误放行后者。
        TextValidation justFit = TransferText.Validate(new string('中', 87381));
        TextValidation overByOneChar = TransferText.Validate(new string('中', 87382));

        Assert.True(justFit.IsValid);
        Assert.Equal(262143, justFit.ByteCount);
        Assert.Equal(TextValidationKind.TooLong, overByOneChar.Kind);
        Assert.True(overByOneChar.CharCount < TransferText.MaxBytes, "字符数并未超上限——超的是字节数");
    }

    [Fact]
    public void GetByteCount_MixedScript_MatchesUtf8Encoding()
    {
        const string mixed = "ab中😀";

        // 'a','b' = 1 B each；'中' = 3 B；'😀'(U+1F600) = 4 B
        Assert.Equal(9, TransferText.GetByteCount(mixed));
        Assert.Equal(5, TransferText.GetCharCount(mixed));
        Assert.Equal(0, TransferText.GetByteCount(null));
    }

    // ===================== 历史预览（按字符，不切断代理对） =====================

    [Fact]
    public void Preview_ShorterThanLimit_IsUnchanged()
    {
        string text = new string('x', TransferText.PreviewLength);

        Assert.Equal(text, TransferText.Preview(text));
    }

    [Fact]
    public void Preview_NoArgAndEmpty_ReturnEmpty()
    {
        Assert.Equal(string.Empty, TransferText.Preview(null));
        Assert.Equal(string.Empty, TransferText.Preview(string.Empty));
    }

    [Fact]
    public void Preview_LongerThanLimit_TruncatesToExactlyLimit()
    {
        string text = new('a', TransferText.PreviewLength + 500);

        string preview = TransferText.Preview(text);

        Assert.Equal(TransferText.PreviewLength, preview.Length);
        Assert.Equal(text[..TransferText.PreviewLength], preview);
    }

    [Fact]
    public void Preview_BoundaryFallsOnHighSurrogate_BacksOffOneChar()
    {
        // 119 个 'a' 之后紧跟一个 emoji（占到索引 119=高代理、120=低代理）：
        // 截断点 120 正好落在代理对中间 → 必须回退到 119，否则出现半个代理对（乱码）
        string text = new string('a', 119) + "\U0001F600" + "tail";

        string preview = TransferText.Preview(text);

        Assert.Equal(119, preview.Length);
        Assert.False(HasLoneSurrogate(preview));
    }

    [Fact]
    public void Preview_BoundaryLandsExactlyAfterSurrogatePair_KeepsEmoji()
    {
        // 118 个 'a' 之后是 emoji（索引 118=高代理、119=低代理）：截断点 120 落在 emoji 之后，
        // 完整保留 —— 证明回退逻辑只在该回退时才回退（不是无脑减一）
        string text = new string('a', 118) + "\U0001F600" + "tail";

        string preview = TransferText.Preview(text);

        Assert.Equal(TransferText.PreviewLength, preview.Length);
        Assert.True(char.IsLowSurrogate(preview[^1]));
        Assert.False(HasLoneSurrogate(preview));
    }

    // ===================== 原因码 =====================

    [Fact]
    public void NewReasonCodes_HaveDescriptionsAndAreDistinct()
    {
        string tooLong = TransferReasonCodes.Describe(TransferReasonCodes.TextTooLong);
        string clipboard = TransferReasonCodes.Describe(TransferReasonCodes.ClipboardWriteFailed);

        Assert.NotEmpty(tooLong);
        Assert.NotEmpty(clipboard);
        // Describe 对未知码是"原样返回"——所以码必须有说明，否则等于没有本地化
        Assert.NotEqual(TransferReasonCodes.TextTooLong, tooLong);
        Assert.NotEqual(TransferReasonCodes.ClipboardWriteFailed, clipboard);
        // 两者语义不同（"对面没收下" vs "对面要了但没接住"），不得互相顶替
        Assert.NotEqual(TransferReasonCodes.TextTooLong, TransferReasonCodes.ClipboardWriteFailed);
    }

    // ===================== 协议契约 =====================

    /// <summary>与 <c>FileTransferService.JsonOpts</c> 完全一致（协议层序列化口径的唯一来源）。</summary>
    private static readonly JsonSerializerOptions ProtocolOpts = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public void TransferMessage_TextType_RoundTripsThroughJson()
    {
        var original = new TransferMessage
        {
            Type = TransferMessageType.Text,
            TaskId = "t-1",
            Text = "https://example.com/a?b=1",
        };

        string json = JsonSerializer.Serialize(original, ProtocolOpts);

        // 枚举按字符串上线（JsonStringEnumConverter），不是数字——跨版本可读、可排查
        Assert.Contains("\"Text\"", json);
        TransferMessage? back = JsonSerializer.Deserialize<TransferMessage>(json, ProtocolOpts);
        Assert.NotNull(back);
        Assert.Equal(TransferMessageType.Text, back.Type);
        Assert.Equal("https://example.com/a?b=1", back.Text);
        Assert.Equal("t-1", back.TaskId);
    }

    [Fact]
    public void TransferMessage_FileMessage_LeavesTextNull()
    {
        var file = new TransferMessage
        {
            Type = TransferMessageType.Handshake,
            TaskId = "t-2",
            FileName = "a.bin",
            FileSize = 2048,
            ChunkSize = 2 * 1024 * 1024,
            TotalChunks = 1,
        };

        string json = JsonSerializer.Serialize(file, ProtocolOpts);
        TransferMessage? back = JsonSerializer.Deserialize<TransferMessage>(json, ProtocolOpts);

        Assert.NotNull(back);
        Assert.Null(back.Text);
        Assert.Equal(TransferMessageType.Handshake, back.Type);
    }

    [Fact]
    public void TransferMessage_OldStyleJsonWithoutTextField_DeserializesWithNullText()
    {
        // 旧版本对端发来的报文里没有 Text 字段 —— 必须能解析且 Text 为 null（向后兼容）
        const string oldJson = """
        {"Type":"Complete","TaskId":"t-3","FileName":"a.bin","FileSize":10,"ChunkSize":2097152,
         "ChunkIndex":0,"TotalChunks":1,"Offset":0,"ResumeFrom":0,"FileModifiedAt":0}
        """;

        TransferMessage? back = JsonSerializer.Deserialize<TransferMessage>(oldJson, ProtocolOpts);

        Assert.NotNull(back);
        Assert.Equal(TransferMessageType.Complete, back.Type);
        Assert.Null(back.Text);
    }

    [Fact]
    public void TransferMessage_UnknownTypeString_ThrowsJsonException()
    {
        // 钉住"为什么 ParseMessage 会返回 null"：未知枚举值走的是 JsonException，
        // 而 ParseMessage 把它 catch 掉了 → **静默丢弃**。
        // B8a-2 要给这条路径补一个显式的 Error 回复；本用例是该决策的依据。
        const string futureJson = """{"Type":"SomethingFromTheFuture","TaskId":"t-4"}""";

        Assert.Throws<JsonException>(() =>
        {
            _ = JsonSerializer.Deserialize<TransferMessage>(futureJson, ProtocolOpts);
        });
    }

    // ===================== 确认门契约（种类与原因码） =====================

    [Fact]
    public void TransferRequestEventArgs_DefaultsToFileKind()
    {
        // 位置参数集不变、Kind 默认 File → 既有构造点零改动（兼容性的具体含义）
        var args = new TransferRequestEventArgs("t-5", "a.bin", 1024, "192.168.1.9:9000");

        Assert.Equal(TransferKind.File, args.Kind);
        Assert.Null(args.Text);
        Assert.Equal(0, args.TextLength);
    }

    [Fact]
    public void TransferDecision_Reject_HasNoReasonCode_RejectWithDoes()
    {
        Assert.Null(TransferDecision.Reject.ReasonCode);
        Assert.False(TransferDecision.Reject.Accept);

        var clipboardFailure = TransferDecision.RejectWith(TransferReasonCodes.ClipboardWriteFailed);
        Assert.False(clipboardFailure.Accept);
        Assert.Equal(TransferReasonCodes.ClipboardWriteFailed, clipboardFailure.ReasonCode);

        // 接受时不应带拒绝原因码
        Assert.Null(TransferDecision.AcceptWith(TransferConflictPolicy.Rename).ReasonCode);
    }

    // ===================== 历史条目契约 =====================

    /// <summary>与 <c>TransferHistoryService.JsonOpts</c> 完全一致（历史文件序列化口径的唯一来源）。</summary>
    private static readonly JsonSerializerOptions HistoryOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public void HistoryEntry_OldJsonWithoutKindField_DeserializesAsFile()
    {
        // 旧版本写下的历史文件没有 kind / textLength 字段 —— 必须落到 File 且显示语义不变
        const string oldJson = """
        [{"taskId":"h-1","fileName":"x.txt","fileSize":1024,"direction":0,"peerEndpoint":"1.2.3.4:9000",
          "peerDeviceId":"","status":4,"startedAt":"2026-09-01T00:00:00+08:00",
          "finishedAt":"2026-09-01T00:00:01+08:00","transferredBytes":1024,
          "errorMessage":null,"reasonCode":null}]
        """;

        List<TransferHistoryEntry>? list = JsonSerializer.Deserialize<List<TransferHistoryEntry>>(oldJson, HistoryOpts);

        Assert.NotNull(list);
        Assert.Single(list);
        TransferHistoryEntry entry = list[0];
        Assert.Equal(TransferKind.File, entry.Kind);
        Assert.Equal(0, entry.TextLength);
        Assert.Equal("文件", entry.KindText);
        Assert.DoesNotContain("字", entry.SizeText);
    }

    [Fact]
    public void HistoryEntry_TextKind_ShowsCharCountAndTextLabel()
    {
        var entry = new TransferHistoryEntry
        {
            TaskId = "h-2",
            Kind = TransferKind.Text,
            FileName = "这是一段预览",
            FileSize = 412,
            TextLength = 137,
            Direction = TransferDirection.Send,
            Status = TransferStatus.Completed,
        };

        Assert.Equal("文本", entry.KindText);
        Assert.Equal("137 字", entry.SizeText);
    }

    [Fact]
    public void HistoryEntry_TextKind_RoundTripsThroughHistoryJson()
    {
        var list = new List<TransferHistoryEntry>
        {
            new() { TaskId = "h-3", Kind = TransferKind.Text, TextLength = 12, FileName = "预览" },
        };

        string json = JsonSerializer.Serialize(list, HistoryOpts);
        List<TransferHistoryEntry>? back = JsonSerializer.Deserialize<List<TransferHistoryEntry>>(json, HistoryOpts);

        Assert.NotNull(back);
        Assert.Single(back);
        Assert.Equal(TransferKind.Text, back[0].Kind);
        Assert.Equal(12, back[0].TextLength);
        Assert.Equal("文本", back[0].KindText);
    }

    /// <summary>检测串中是否存在**落单的代理码元**（半个 emoji 的判据）。</summary>
    private static bool HasLoneSurrogate(string value)
    {
        for (int i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]))
            {
                if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1]))
                {
                    return true;
                }
                i++;
            }
            else if (char.IsLowSurrogate(value[i]))
            {
                return true;
            }
        }

        return false;
    }
}
