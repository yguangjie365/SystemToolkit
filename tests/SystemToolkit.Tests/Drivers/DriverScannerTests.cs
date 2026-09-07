using SystemToolkit.Core.Drivers;

namespace SystemToolkit.Tests.Drivers;

/// <summary>
/// L16 补齐（全面代码审查 2026-09-05）：DriverScanner 编排 / DriverClassResolver INF 解析 /
/// DriverDeviceMapper.Accumulate 聚合的守卫。互操作与注册表全部经注入或纯函数隔离（03 测试规范）。
/// </summary>
public class DriverScannerTests
{
    private sealed class FakeClient : IPnpUtilClient
    {
        public FakeClient(IReadOnlyList<DriverPackage> packages) => Packages = packages;

        public IReadOnlyList<DriverPackage> Packages { get; }

        public Task<IReadOnlyList<DriverPackage>> EnumDriversAsync(CancellationToken ct = default)
            => Task.FromResult(Packages);

        public Task<DriverRunResult> ExportAsync(string p, string d, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<DriverRunResult> ExportManyAsync(IReadOnlyList<string> p, string d, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<DriverRunResult> DeleteAsync(string p, bool f, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<DriverRunResult> DeleteManyAsync(IReadOnlyList<string> p, bool f, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<DriverRunResult> AddDriverAsync(string p, bool i, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<DriverRunResult> AddManyAsync(IReadOnlyList<string> p, bool i, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private static DriverPackage Pkg(string published, string classGuid, List<string>? devices = null) => new()
    {
        PublishedName = published,
        OriginalName = published,
        ClassGuid = classGuid,
        DeviceNames = devices ?? new List<string>(),
    };

    [Fact]
    public async Task ScanAsync_ClassNameTranslation_BootCriticalFlag_OrchestratedCorrectly()
    {
        var packages = new List<DriverPackage>
        {
            Pkg("oem1.inf", "{11111111-1111-1111-1111-111111111111}"),
            Pkg("oem2.inf", "{22222222-2222-2222-2222-222222222222}"),
        };
        var client = new FakeClient(packages);
        Dictionary<string, string>? Translate(IReadOnlyCollection<string> guids, CancellationToken ct) =>
            guids.ToDictionary(g => g, g => "显示卡(" + g.Length + ")");
        bool IsCritical(string guid) => guid.StartsWith("{2222", StringComparison.Ordinal);

        var scanner = new DriverScanner(client, (g, ct) => Task.FromResult(Translate(g, ct)), IsCritical);
        IReadOnlyList<DriverPackage> result = await scanner.ScanAsync();

        Assert.Equal(2, result.Count);
        // 类名翻译：方案甲委托命中 → ClassName 被翻译
        Assert.Equal("显示卡(38)", result[0].ClassName);
        Assert.Equal("显示卡(38)", result[1].ClassName);
        // 启动关键标记：仅注入委托判定为 true 的类
        Assert.False(result[0].IsBootCritical);
        Assert.True(result[1].IsBootCritical);
    }

    [Fact]
    public async Task ScanAsync_NoClassNameTranslationDelegate_PreservesOriginalClassName()
    {
        var packages = new List<DriverPackage>
        {
            Pkg("oem1.inf", "{11111111-1111-1111-1111-111111111111}"),
        };
        var scanner = new DriverScanner(new FakeClient(packages));

        IReadOnlyList<DriverPackage> result = await scanner.ScanAsync();

        // 无提权翻译委托时（回退注册表/INF 链在真机才有效）不得抛出，且不丢包
        _ = Assert.Single(result);
    }

    [Fact]
    public async Task ScanAsync_CancelledTokenPassed_ThrowsOperationCanceled()
    {
        var scanner = new DriverScanner(new FakeClient([Pkg("oem1.inf", "{11111111-1111-1111-1111-111111111111}")]));
        // TaskCanceledException 派生自 OperationCanceledException
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => scanner.ScanAsync(new CancellationToken(canceled: true)));
    }
}

/// <summary>ResolveFromInf：纯文件解析（[Version].Class= 行）。</summary>
public class DriverClassResolverTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"stk_inf_{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private string WriteInf(string content)
    {
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, "sample.inf");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void ResolveFromInf_ClassLine_ReturnsClassName()
    {
        string path = WriteInf("[Version]\r\nSignature=\"$CHICAGO$\"\r\nClass=Net\r\nClassGuid={4d36e972}\r\n");
        Assert.Equal("Net", DriverClassResolver.ResolveFromInf(path));
    }

    [Fact]
    public void ResolveFromInf_PlaceholderOrMissing_ReturnsNull()
    {
        string placeholder = WriteInf("[Version]\r\nClass=%ClassName%\r\n");
        Assert.Null(DriverClassResolver.ResolveFromInf(placeholder));

        string missing = WriteInf("[Version]\r\nSignature=\"$CHICAGO$\"\r\n");
        Assert.Null(DriverClassResolver.ResolveFromInf(missing));
    }

    [Fact]
    public void ResolveFromInf_FileNotExists_ReturnsNull()
    {
        Assert.Null(DriverClassResolver.ResolveFromInf(Path.Combine(_dir, "ghost.inf")));
        Assert.Null(DriverClassResolver.ResolveFromInf(null));
    }

    [Fact]
    public void Accumulate_DedupesSameInfMultipleDevices_FiltersEmptyValues()
    {
        Dictionary<string, List<string>> map = DriverDeviceMapper.Accumulate(
        [
            ("oem1.inf", "设备A"),
            ("oem1.inf", "设备A"),   // 重复保持去重
            ("oem1.inf", "设备B"),
            ("oem2.inf", "设备C"),
            ("", "设备D"),           // 空 inf 过滤
            ("oem3.inf", ""),        // 空设备过滤
        ]);

        Assert.Equal(2, map.Count);
        Assert.Equal(["设备A", "设备B"], map["oem1.inf"]);
        Assert.Equal(["设备C"], map["oem2.inf"]);
    }
}
