using System.Reflection;
using System.Text.Json;
using SystemToolkit.Core.Contracts;
using SystemToolkit.Core.Logging;
using SystemToolkit.Shell;

namespace SystemToolkit.Tests;

/// <summary>
/// 日志系统守卫（2026-09-06 L1：总线 + 滚动 + 保留期 + 限流 + 转发）。
/// <para>
/// 覆盖：<see cref="AppLog"/> 级别过滤/分发/sink 故障隔离；
/// <see cref="SerilogSink"/> Serilog 落盘（分模块/汇总文本/JSONL 八字段/按量续号滚动，LOG-1）；
/// <see cref="LogMaintenance"/> 保留期与总量封顶；
/// <see cref="ExceptionLogThrottle"/> 同异常去重 + 计数汇总；
/// <see cref="FileLogger"/> 透过总线；<see cref="LogScope"/> 关联作用域；
/// <see cref="LogEntry"/> 文本/JSON 渲染；<see cref="DiagnosticsExporter"/> 环境报告。
/// </para>
/// <para>
/// 🔴 每个测试前后都 <see cref="AppLog.Reset"/> —— 全局静态总线必须在用例间复位，
/// 否则顺序敏感：用例 A 装的 sink 会「污染」用例 B。
/// </para>
/// </summary>
public class LoggingGuardTests : IDisposable
{
    public LoggingGuardTests()
    {
        // 测试隔离：每个用例在干净的总线上跑（不污染用户的 AppData）
        AppLog.Reset();
        LogMaintenance.ResetPruneMemory();
    }

    public void Dispose()
    {
        AppLog.Reset();
        LogMaintenance.ResetPruneMemory();
    }

    /* ====================================================================
     * 1. AppLog 级别过滤
     * ==================================================================== */

    [Fact]
    public void Write_BelowMinimumLevel_IsDropped()
    {
        AppLog.MinimumLevel = LogLevel.Warn;
        var sink = new ListSink();
        AppLog.AddSink(sink);

        AppLog.Write(new LogEntry { Level = LogLevel.Info, Source = "x", Message = "should-not-pass" });
        AppLog.Write(new LogEntry { Level = LogLevel.Warn, Source = "x", Message = "warn-passes" });
        AppLog.Write(new LogEntry { Level = LogLevel.Error, Source = "x", Message = "error-passes" });

        Assert.Equal(2, sink.Entries.Count);
        Assert.Equal("warn-passes", sink.Entries[0].Message);
        Assert.Equal("error-passes", sink.Entries[1].Message);
    }

    [Fact]
    public void Write_TraceByDefault_IsDropped()
    {
        // 默认 MinimumLevel = Info，Trace 必须被丢弃（验证「--diag 默认关」的基线）
        Assert.Equal(LogLevel.Info, AppLog.MinimumLevel);
        var sink = new ListSink();
        AppLog.AddSink(sink);

        AppLog.Write(new LogEntry { Level = LogLevel.Trace, Source = "x", Message = "trace" });

        Assert.Empty(sink.Entries);
    }

    [Fact]
    public void Write_AtMinLevel_IsKept()
    {
        AppLog.MinimumLevel = LogLevel.Debug;
        var sink = new ListSink();
        AppLog.AddSink(sink);

        AppLog.Write(new LogEntry { Level = LogLevel.Debug, Source = "x", Message = "ok" });

        Assert.Single(sink.Entries);
    }

    /* ====================================================================
     * 2. AppLog 分发契约：每条记录给每个 sink 一份
     * ==================================================================== */

    [Fact]
    public void Write_FansOut_ToAllSinks()
    {
        var a = new ListSink();
        var b = new ListSink();
        AppLog.AddSink(a);
        AppLog.AddSink(b);

        AppLog.Write(new LogEntry { Level = LogLevel.Info, Source = "x", Message = "fanout" });

        Assert.Single(a.Entries);
        Assert.Single(b.Entries);
        Assert.Equal("fanout", a.Entries[0].Message);
        Assert.Equal("fanout", b.Entries[0].Message);
    }

    [Fact]
    public void Write_DuplicateSink_IsNotAddedTwice()
    {
        var sink = new ListSink();
        AppLog.AddSink(sink);
        AppLog.AddSink(sink);
        AppLog.AddSink(sink);

        AppLog.Write(new LogEntry { Level = LogLevel.Info, Source = "x", Message = "once" });

        Assert.Single(sink.Entries);
    }

    [Fact]
    public void Write_NoSinksRegistered_DoesNotThrow()
    {
        // 没装 sink 时调用 Write 必须静默通过（不然启动早期写日志就 NRE）
        AppLog.Write(new LogEntry { Level = LogLevel.Info, Source = "x", Message = "orphan" });
    }

    [Fact]
    public void Write_SinkThrows_DoesNotBreakOthers()
    {
        var good = new ListSink();
        AppLog.AddSink(new ThrowingSink());
        AppLog.AddSink(good);

        AppLog.Write(new LogEntry { Level = LogLevel.Info, Source = "x", Message = "survives" });

        Assert.Single(good.Entries); // 坏 sink 抛了不影响好 sink
    }

    [Fact]
    public void Reset_ClearsSinksAndResetsLevel()
    {
        AppLog.AddSink(new ListSink());
        AppLog.MinimumLevel = LogLevel.Trace;
        AppLog.Reset();

        var sink = new ListSink();
        AppLog.AddSink(sink);
        Assert.Equal(LogLevel.Info, AppLog.MinimumLevel);

        AppLog.Write(new LogEntry { Level = LogLevel.Trace, Source = "x", Message = "after-reset" });
        Assert.Empty(sink.Entries); // 级别回到默认 Info，Trace 被丢
    }

    /* ====================================================================
     * 3. SerilogSink + LogMaintenance：Serilog 落盘（LOG-1，2026-09-11）
     * ==================================================================== */

    [Fact]
    public void SerilogSink_Emit_CreatesModuleAppTextAndJsonlFiles()
    {
        string root = TempDir();
        try
        {
            var sink = new SerilogSink(root);
            sink.Emit(new LogEntry { Level = LogLevel.Info, Source = "x", Message = "hi" });
            sink.Dispose();

            string[] moduleLogs = Directory.GetFiles(root, "x-*.log");
            Assert.NotEmpty(moduleLogs);
            Assert.NotEmpty(Directory.GetFiles(root, "app-*.log"));
            Assert.NotEmpty(Directory.GetFiles(root, "app-*.jsonl"));

            // 人读行格式沿用 ToLine：[HH:mm:ss.fff] [LEVEL] [source] message
            string text = File.ReadAllText(moduleLogs[0]);
            Assert.Contains("[x] hi", text);
        }
        finally
        {
            SafeDelete(root);
        }
    }

    [Fact]
    public void SerilogSink_BracesInMessage_AreWrittenLiterallyNotAsTemplate()
    {
        string root = TempDir();
        try
        {
            var sink = new SerilogSink(root);
            sink.Emit(new LogEntry { Level = LogLevel.Info, Source = "b", Message = "{NotATemplate} {0} {{x}}" });
            sink.Dispose();

            string text = File.ReadAllText(Directory.GetFiles(root, "b-*.log")[0]);
            Assert.Contains("{NotATemplate} {0} {{x}}", text);
        }
        finally
        {
            SafeDelete(root);
        }
    }

    [Fact]
    public void SerilogSink_Jsonl_CarriesEightFieldsAsStructuredProperties()
    {
        string root = TempDir();
        try
        {
            var sink = new SerilogSink(root);
            sink.Emit(new LogEntry
            {
                Level = LogLevel.Warn,
                Source = "net",
                Message = "dns degraded",
                CorrelationId = "cid-77",
                Action = "ChangeDns",
                Outcome = LogResult.Failed,
                DurationMs = 184,
                Exception = new InvalidOperationException("no-route"),
            });
            sink.Dispose();

            string jsonl = Directory.GetFiles(root, "app-*.jsonl")[0];
            string raw = File.ReadAllText(jsonl).Trim();
            using var doc = JsonDocument.Parse(raw);
            JsonElement rootEl = doc.RootElement;

            // Compact JSON 实测形状：@t/@l/@x + 八个业务字段平铺（Message/Source/CorrelationId/Action/Result/DurationMs）
            Assert.Equal("Warning", rootEl.GetProperty("@l").GetString());
            Assert.True(rootEl.TryGetProperty("@t", out _), "Compact JSON 必须含时间戳 @t");
            Assert.Equal("net", rootEl.GetProperty("Source").GetString());
            Assert.Equal("dns degraded", rootEl.GetProperty("Message").GetString());
            Assert.Equal("cid-77", rootEl.GetProperty("CorrelationId").GetString());
            Assert.Equal("ChangeDns", rootEl.GetProperty("Action").GetString());
            Assert.Equal("Failed", rootEl.GetProperty("Result").GetString());
            Assert.Equal(184, rootEl.GetProperty("DurationMs").GetInt64());
            Assert.Contains("no-route", raw); // 异常经 @x 落进机读流
        }
        finally
        {
            SafeDelete(root);
        }
    }

    [Fact]
    public void SerilogSink_OverMaxBytes_RollsToNumberedSuffix()
    {
        string root = TempDir();
        try
        {
            var sink = new SerilogSink(root, maxBytes: 512);
            for (int i = 0; i < 40; i++)
            {
                sink.Emit(new LogEntry { Level = LogLevel.Info, Source = "z", Message = new string('a', 64) });
            }

            sink.Dispose();

            // 超限后续号产物 z-YYYYMMDD_001.log —— 每个文件都被压在合理上限附近
            string[] logs = Directory.GetFiles(root, "z-*.log");
            Assert.True(logs.Length > 1, $"按量滚动应产生续号文件，实际 {logs.Length} 个");
            foreach (string f in logs)
            {
                Assert.True(new FileInfo(f).Length <= 512 + 256, $"文件 {Path.GetFileName(f)} 超限");
            }
        }
        finally
        {
            SafeDelete(root);
        }
    }

    [Fact]
    public void LogMaintenance_Prune_RemovesExpiredKeepsRecentIncludingLegacyOldArchives()
    {
        string root = TempDir();
        try
        {
            string expired = $"app-{DateTime.Today.AddYears(-7):yyyyMMdd}.log";
            string expiredJsonl = $"app-{DateTime.Today.AddYears(-7):yyyyMMdd}.jsonl";
            string expiredLegacyOld = $"app-{DateTime.Today.AddYears(-7):yyyyMMdd}.log.old";
            string recent = $"app-{DateTime.Today.AddDays(-1):yyyyMMdd}.log";
            foreach (string n in new[] { expired, expiredJsonl, expiredLegacyOld, recent })
            {
                File.WriteAllText(Path.Combine(root, n), "x");
            }

            LogMaintenance.Prune(root, retainDays: 30);

            Assert.False(File.Exists(Path.Combine(root, expired)));
            Assert.False(File.Exists(Path.Combine(root, expiredJsonl)));
            Assert.False(File.Exists(Path.Combine(root, expiredLegacyOld)), "旧 RollingFileSink 时代的 .old 归档也要能被清掉");
            Assert.True(File.Exists(Path.Combine(root, recent)));
        }
        finally
        {
            SafeDelete(root);
        }
    }

    [Fact]
    public void LogMaintenance_Prune_EnforcesTotalCapByDeletingOldestFirst()
    {
        string root = TempDir();
        try
        {
            string older = $"app-{DateTime.Today.AddDays(-3):yyyyMMdd}.log";
            string newer = $"app-{DateTime.Today.AddDays(-1):yyyyMMdd}.log";
            File.WriteAllText(Path.Combine(root, older), new string('o', 400));
            File.WriteAllText(Path.Combine(root, newer), new string('n', 100));

            LogMaintenance.Prune(root, retainDays: 30, maxTotalBytes: 450);

            Assert.False(File.Exists(Path.Combine(root, older)), "超总量上限先删最旧");
            Assert.True(File.Exists(Path.Combine(root, newer)));
        }
        finally
        {
            SafeDelete(root);
        }
    }

    [Fact]
    public void LogMaintenance_PruneOncePerDay_RunsOnlyOnceThenResets()
    {
        string root = TempDir();
        try
        {
            string expired = $"p-{DateTime.Today.AddYears(-7):yyyyMMdd}.log";
            File.WriteAllText(Path.Combine(root, expired), "x");

            LogMaintenance.PruneOncePerDay(root, retainDays: 30);
            Assert.False(File.Exists(Path.Combine(root, expired)), "每天一次的首次调用必须执行清理");

            File.WriteAllText(Path.Combine(root, expired), "x");
            LogMaintenance.PruneOncePerDay(root, retainDays: 30);
            Assert.True(File.Exists(Path.Combine(root, expired)), "同日第二次调用应被记忆短路（避免每条日志枚举目录）");

            LogMaintenance.ResetPruneMemory();
            LogMaintenance.PruneOncePerDay(root, retainDays: 30);
            Assert.False(File.Exists(Path.Combine(root, expired)));
        }
        finally
        {
            SafeDelete(root);
        }
    }

    /* ====================================================================
     * 4. ExceptionLogThrottle：同异常去重 + 计数汇总
     * ==================================================================== */

    [Fact]
    public void Throttle_SameKey_ReturnsTrueOnceThenFalse()
    {
        var capture = new ListSink();
        var throttle = new ExceptionLogThrottle(capture, maxDistinct: 100);

        var ex = new InvalidOperationException("boom");

        Assert.True(throttle.ShouldLog(ex));   // 首次
        Assert.False(throttle.ShouldLog(ex));  // 重复 1
        Assert.False(throttle.ShouldLog(ex));  // 重复 2
        Assert.False(throttle.ShouldLog(ex));  // 重复 3
    }

    [Fact]
    public void Throttle_DifferentFirstFrame_AreDistinct()
    {
        // 同一个异常实例的 StackTrace 不可写，但 throttle 的 key 取首帧；
        // 用两个独立方法分别 throw/catch 制造「首帧不同」的异常。
        Exception e1 = CapturedAtFrameA();
        Exception e2 = CapturedAtFrameB();
        Assert.NotEqual(FirstFrame(e1), FirstFrame(e2)); // 防御性断言：确保测试设置正确

        var capture = new ListSink();
        var throttle = new ExceptionLogThrottle(capture, maxDistinct: 100);

        Assert.True(throttle.ShouldLog(e1));
        Assert.True(throttle.ShouldLog(e2));  // 不同首帧 → 不同 key → 首次
        Assert.False(throttle.ShouldLog(e1)); // e1 重复
    }

    [Fact]
    public void Throttle_DifferentType_AreDistinct()
    {
        var capture = new ListSink();
        var throttle = new ExceptionLogThrottle(capture, maxDistinct: 100);

        Assert.True(throttle.ShouldLog(new InvalidOperationException("x")));
        Assert.True(throttle.ShouldLog(new ArgumentException("y")));
    }

    [Fact]
    public void Throttle_FlushSummary_ReportsSuppressedCounts()
    {
        var capture = new ListSink();
        var throttle = new ExceptionLogThrottle(capture, maxDistinct: 100);

        var ex = new InvalidOperationException("storm");
        Assert.True(throttle.ShouldLog(ex));
        for (int i = 0; i < 5; i++)
        {
            throttle.ShouldLog(ex);
        }

        throttle.FlushSummary();

        // FlushSummary 只对被吞（Count > 1）的 bucket 输出 Warn；首条没单独走过 BusLogger。
        Assert.Contains(capture.Entries, e =>
            e.Level == LogLevel.Warn && e.Message.Contains("共 6 次"));
    }

    [Fact]
    public void Throttle_FlushSummary_SkipsBucketsWithSingleOccurrence()
    {
        var capture = new ListSink();
        var throttle = new ExceptionLogThrottle(capture, maxDistinct: 100);

        throttle.ShouldLog(new InvalidOperationException("once"));

        throttle.FlushSummary();

        Assert.Empty(capture.Entries); // 只 1 次的不汇报（避免噪音）
    }

    [Fact]
    public void Throttle_Overflow_ReportsOnceAndStopsTracking()
    {
        var capture = new ListSink();
        // maxDistinct = 2 让我们能快速触发「已达上限」。
        // 用三个**首帧不同**的异常（throttle key = 类型+首帧）才会被算作新种类；
        // 直接 `new InvalidOperationException("x")` 栈为 null → key 全相同 → 永远不会触发超限。
        var throttle = new ExceptionLogThrottle(capture, maxDistinct: 2);

        Assert.True(throttle.ShouldLog(CapturedAtFrameA())); // 第 1 种
        Assert.True(throttle.ShouldLog(CapturedAtFrameB())); // 第 2 种
        // 第 3 种 → 超限，应拒绝并报一次溢出
        Assert.False(throttle.ShouldLog(MakeCapturedAt("c")));
        // 第 4 次：溢出已报，不再重复报
        Assert.False(throttle.ShouldLog(MakeCapturedAt("d")));

        Assert.Contains(capture.Entries, e =>
            e.Level == LogLevel.Warn && e.Message.Contains("已达上限 2"));
    }

    /* ====================================================================
     * 5. FileLogger / BusLogger 透过总线（兼容旧用法）
     * ==================================================================== */

    [Fact]
    public void FileLogger_Info_WritesToBusWithSourceAsPrefix()
    {
        var sink = new ListSink();
        AppLog.AddSink(sink);

        var logger = new FileLogger("appmanager");
        logger.Info("hello");

        Assert.Single(sink.Entries);
        Assert.Equal("appmanager", sink.Entries[0].Source);
        Assert.Equal("hello", sink.Entries[0].Message);
    }

    [Fact]
    public void FileLogger_Error_IncludesException()
    {
        var sink = new ListSink();
        AppLog.AddSink(sink);

        var ex = new InvalidOperationException("oops");
        new FileLogger("net").Error("net failed", ex);

        Assert.Single(sink.Entries);
        Assert.Equal(LogLevel.Error, sink.Entries[0].Level);
        Assert.Same(ex, sink.Entries[0].Exception);
    }

    [Fact]
    public void BusLogger_IsEnabled_TracksMinimumLevel()
    {
        AppLog.MinimumLevel = LogLevel.Warn;
        var logger = new BusLogger("x");

        Assert.False(logger.IsEnabled(LogLevel.Trace));
        Assert.False(logger.IsEnabled(LogLevel.Info));
        Assert.True(logger.IsEnabled(LogLevel.Warn));
        Assert.True(logger.IsEnabled(LogLevel.Error));

        AppLog.MinimumLevel = LogLevel.Trace;
        Assert.True(logger.IsEnabled(LogLevel.Trace));
    }

    [Fact]
    public void ILoggerDefaultImpl_DispatchesByLevel()
    {
        // 验证 ILogger.Log 默认实现按级别分发（Trace/Debug 被 IsEnabled 过滤；
        // Warn/Error/Fatal 落到对应 Info/Warn/Error）
        var capture = new ListSink();
        AppLog.AddSink(capture);

        // 默认 IsEnabled = level >= LogLevel.Info：Trace/Debug 直接被丢
        var logger = new DefaultMappingLogger();
        logger.Log(LogLevel.Trace, "t");
        logger.Log(LogLevel.Debug, "d");
        logger.Log(LogLevel.Info, "i");
        logger.Log(LogLevel.Warn, "w");
        logger.Log(LogLevel.Error, "e", new InvalidOperationException("e!"));
        logger.Log(LogLevel.Fatal, "f", new InvalidOperationException("f!"));

        // Trace/Debug 被 IsEnabled 拦掉，Info/Warn/Error/Fatal 都分发（Fatal→Error）
        Assert.Equal(4, capture.Entries.Count);
        Assert.Equal("i", capture.Entries[0].Message);
        Assert.Equal("w", capture.Entries[1].Message);
        Assert.Equal("e", capture.Entries[2].Message);
        Assert.Equal("f", capture.Entries[3].Message);
    }

    /* ====================================================================
     * 6. LogScope：关联作用域嵌套
     * ==================================================================== */

    [Fact]
    public void LogScope_Begin_SetsCorrelationId()
    {
        LogScope.CorrelationId = null;
        using (LogScope.Begin("op-1"))
        {
            Assert.Equal("op-1", LogScope.CorrelationId);
        }

        Assert.Null(LogScope.CorrelationId);
    }

    [Fact]
    public void LogScope_NestedBegin_RestoresParent()
    {
        LogScope.CorrelationId = null;
        using (LogScope.Begin("outer"))
        {
            Assert.Equal("outer", LogScope.CorrelationId);
            using (LogScope.Begin("inner"))
            {
                Assert.Equal("inner", LogScope.CorrelationId);
            }

            Assert.Equal("outer", LogScope.CorrelationId);
        }

        Assert.Null(LogScope.CorrelationId);
    }

    [Fact]
    public void LogScope_NewId_ProducesEightHexChars()
    {
        string id = LogScope.NewId();
        Assert.Equal(8, id.Length);
        Assert.True(int.TryParse(id, System.Globalization.NumberStyles.HexNumber, null, out _),
            $"LogScope.NewId 应是 8 位 hex，实得 {id}");
    }

    [Fact]
    public async Task LogScope_FlowsAcrossAwait()
    {
        // AsyncLocal 跨 await 保留；这是它优于 ThreadLocal 的关键（实测异步代码会丢上下文）。
        LogScope.CorrelationId = null;
        using (LogScope.Begin("async-op"))
        {
            Assert.Equal("async-op", LogScope.CorrelationId);
            await AwaitAndCheckAsync();
        }
    }

    private static async Task AwaitAndCheckAsync()
    {
        await Task.Yield();
        Assert.Equal("async-op", LogScope.CorrelationId);
    }

    /* ====================================================================
     * 7. LogEntry 渲染
     * ==================================================================== */

    [Fact]
    public void LogEntry_ToLine_IncludesTimeLevelAndBody()
    {
        var entry = new LogEntry
        {
            Timestamp = new DateTime(2026, 9, 6, 12, 34, 56, 789),
            Level = LogLevel.Warn,
            Source = "overview",
            Message = "cpu hot",
            CorrelationId = null,
        };

        string line = entry.ToLine();
        Assert.Contains("12:34:56.789", line);
        Assert.Contains("WARN", line);
        Assert.Contains("overview", line);
        Assert.Contains("cpu hot", line);

        // 无 cid 时整行只有 3 个开括号：时间 / 级别 / 来源
        Assert.Equal(3, line.Count(c => c == '['));
        Assert.Equal(3, line.Count(c => c == ']'));
    }

    [Fact]
    public void LogEntry_ToLine_WithCorrelationId_AppendsBracket()
    {
        var entry = new LogEntry
        {
            Level = LogLevel.Info,
            Source = "x",
            Message = "m",
            CorrelationId = "abc12345",
        };

        Assert.Contains("[abc12345]", entry.ToLine());
    }

    [Fact]
    public void LogEntry_ToLine_WithException_AppendsStack()
    {
        var entry = new LogEntry
        {
            Level = LogLevel.Error,
            Source = "x",
            Message = "m",
            Exception = new InvalidOperationException("boom"),
        };

        string line = entry.ToLine();
        Assert.Contains("m", line);
        Assert.Contains("InvalidOperationException", line);
        Assert.Contains("boom", line);
    }

    [Fact]
    public void LogEntry_ToJson_ProducesParseableJsonlWithExceptionFields()
    {
        var entry = new LogEntry
        {
            Level = LogLevel.Error,
            Source = "net",
            Message = "timeout",
            Exception = new InvalidOperationException("no-route"),
            CorrelationId = "x-1",
        };

        using var doc = JsonDocument.Parse(entry.ToJson());
        JsonElement root = doc.RootElement;
        Assert.Equal("net", root.GetProperty("source").GetString());
        Assert.Equal("timeout", root.GetProperty("message").GetString());
        Assert.Equal("x-1", root.GetProperty("cid").GetString());
        Assert.Equal("System.InvalidOperationException",
            root.GetProperty("exType").GetString());
        Assert.Equal("no-route", root.GetProperty("exMessage").GetString());
    }

    /* ====================================================================
     * 8. DiagnosticsExporter 环境报告（不打包 zip — 那是手测）
     * ==================================================================== */

    [Fact]
    public void EnvironmentReport_IncludesVersionRuntimeAndLogDir()
    {
        // 把日志目录换个临时值，免得把真实路径写进失败信息里
        string prevDir = AppLog.LogDirectory;
        try
        {
            AppLog.LogDirectory = @"C:\fake\system\toolkit\logs";
            string report = DiagnosticsExporter.BuildEnvironmentReport();

            Assert.Contains("应用版本", report);
            Assert.Contains(".NET 运行时", report);
            Assert.Contains(@"C:\fake\system\toolkit\logs", report);
            Assert.Contains("日志目录", report);
            Assert.Contains("— 说明 —", report);
        }
        finally
        {
            AppLog.LogDirectory = prevDir;
        }
    }

    /* ====================================================================
     * 9. LoggerExtensions.Time：Action/Result/Duration 三件套（LOG-1）
     * ==================================================================== */

    [Fact]
    public void Time_Complete_WritesEntryWithActionOutcomeAndDuration()
    {
        var capture = new ListSink();
        AppLog.AddSink(capture);
        var logger = new BusLogger("t");

        using (LogTiming t = logger.Time("t", "RunRule"))
        {
            t.Complete();
        }

        LogEntry entry = Assert.Single(capture.Entries);
        Assert.Equal("RunRule", entry.Action);
        Assert.Equal(LogResult.Success, entry.Outcome);
        Assert.NotNull(entry.DurationMs);
        Assert.Contains("RunRule 完成", entry.Message);
    }

    [Fact]
    public void Time_DisposeWithoutComplete_RecordsCancelledWarn()
    {
        var capture = new ListSink();
        AppLog.AddSink(capture);
        var logger = new BusLogger("t");

        using (logger.Time("t", "RunRule"))
        {
            // 模拟提前 return：不显式 Complete
        }

        LogEntry entry = Assert.Single(capture.Entries);
        Assert.Equal(LogLevel.Warn, entry.Level);
        Assert.Equal(LogResult.Cancelled, entry.Outcome);
    }

    /* ====================================================================
     * 测试辅助：内存 sink / 临时目录
     * ==================================================================== */

    private sealed class ListSink : ILogSink, ILogger
    {
        public List<LogEntry> Entries { get; } = new();

        public void Emit(LogEntry entry) => Entries.Add(entry);

        public void Info(string message) => Entries.Add(new LogEntry { Level = LogLevel.Info, Source = "list-sink", Message = message });
        public void Warn(string message) => Entries.Add(new LogEntry { Level = LogLevel.Warn, Source = "list-sink", Message = message });
        public void Error(string message, Exception? ex = null) => Entries.Add(new LogEntry { Level = LogLevel.Error, Source = "list-sink", Message = message, Exception = ex });
        public void Log(LogLevel level, string message, Exception? ex = null)
        {
            // 把 Log 调用按默认契约转回 Info/Warn/Error，方便总线统一观察
            if (level >= LogLevel.Error)
            {
                Error(message, ex);
                return;
            }

            if (level == LogLevel.Warn)
            {
                Warn(message);
                return;
            }

            Info(message);
        }
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Info;
    }

    private sealed class ThrowingSink : ILogSink
    {
        public void Emit(LogEntry entry) => throw new InvalidOperationException("sink boom");
    }

    /// <summary>
    /// 故意覆写 Log 的「老风格」实现——验证默认 Log 映射按级别分发到 Info/Warn/Error，
    /// 并被 IsEnabled 过滤 Trace/Debug。
    /// </summary>
    private sealed class DefaultMappingLogger : ILogger
    {
        public void Info(string message) => AppLog.Write(new LogEntry { Level = LogLevel.Info, Source = "map", Message = message });
        public void Warn(string message) => AppLog.Write(new LogEntry { Level = LogLevel.Warn, Source = "map", Message = message });
        public void Error(string message, Exception? ex = null) => AppLog.Write(new LogEntry { Level = LogLevel.Error, Source = "map", Message = message, Exception = ex });

        public void Log(LogLevel level, string message, Exception? ex = null)
        {
            if (!IsEnabled(level))
            {
                return;
            }

            if (level >= LogLevel.Error)
            {
                Error(message, ex);
            }
            else if (level == LogLevel.Warn)
            {
                Warn(message);
            }
            else
            {
                Info(message);
            }
        }

        public bool IsEnabled(LogLevel level) => level >= LogLevel.Info;
    }

    private static string TempDir()
    {
        string p = Path.Combine(Path.GetTempPath(), "sys-toolkit-log-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(p);
        return p;
    }

    private static void SafeDelete(string root)
    {
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch
        {
            // 沙箱偶尔文件锁，留着下次测试自己清理
        }
    }

    /* ====================================================================
     * 同异常 key 不同首帧的 helper：抛/接/返 → 不同方法名 → 不同首帧
     * ==================================================================== */

    private static Exception CapturedAtFrameA()
    {
        try
        {
            throw new InvalidOperationException("a");
        }
        catch (InvalidOperationException e)
        {
            return e;
        }
    }

    private static Exception CapturedAtFrameB()
    {
        try
        {
            throw new InvalidOperationException("b");
        }
        catch (InvalidOperationException e)
        {
            return e;
        }
    }

    /// <summary>在独立方法中 throw/catch，让 StackTrace 首帧带方法名 → throttle key 不同。</summary>
    private static Exception MakeCapturedAt(string marker)
    {
        try
        {
            throw new InvalidOperationException(marker);
        }
        catch (InvalidOperationException e)
        {
            return e;
        }
    }

    private static string FirstFrame(Exception ex) =>
        ex.StackTrace?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "<none>";
}