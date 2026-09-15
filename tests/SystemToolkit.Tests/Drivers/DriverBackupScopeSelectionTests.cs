using SystemToolkit.Core.Drivers;
using SystemToolkit.Modules.DriverManager;

namespace SystemToolkit.Tests.Drivers;

/// <summary>
/// 「备份范围（<see cref="DriverBackupScope"/>）如实落盘」行为锁（B-🟡-3，2026-09-15 补 <c>Selected</c> 档）。
/// <para>
/// 缺陷类：备份向导的两条分支语义不同——「全部第三方驱动（N 个）」与「仅勾选的驱动包（N 个）」——
/// 但 <c>DriverManagerViewModel.RunBackupAsync</c> 解包 <c>BackupWizardRequest</c> 返回值时把第三项
/// <c>AllThirdParty</c> 位写成 <c>_</c> 丢弃，再统一传 <c>ThirdPartyOnly</c> ⇒ **只含 3 个勾选包**的备份
/// 会被标成「全部第三方」。scope 目前只进 manifest 元数据、不参与导出筛选，所以不影响本次导出内容；
/// 但未来恢复向导若按 scope 判断「这份备份是否覆盖全量第三方」就会被误导。
/// </para>
/// <para>
/// 🔴 为什么既有守卫抓不到：Core 侧的 <c>DriverBackupServiceTests</c> 直接调服务并传字面量 scope，
/// 结构性绕开了「VM 如何决定 scope」这条路径；而 <c>DriverManagerViewModel</c> 在此之前除
/// <c>ViewLoadSmokeGuardTests</c> 的 <c>null!</c> 冒烟构造外**从未被真实参数构造过** ⇒ 该路径零回归锁。
/// 故本锁**端到端起跑真实备份**（真落盘 manifest.json），断言落盘标签而非源码文本。
/// </para>
/// </summary>
public class DriverBackupScopeSelectionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"stk_bkscope_{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    /// <summary>模拟 pnputil /export-driver：在 <c>StageDirFor</c> 下建内层目录并写入产物
    /// （与 <c>DriverBackupServiceTests.FakePnpUtil</c> 同构——服务层以「暂存目录内层目录存在」为实证依据）。</summary>
    private sealed class FakePnpUtil : IPnpUtilClient
    {
        public Task<IReadOnlyList<DriverPackage>> EnumDriversAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<DriverPackage>>([]);

        public Task<DriverRunResult> ExportAsync(string publishedName, string destinationDir, CancellationToken ct = default)
        {
            string pkgDir = Path.Combine(destinationDir, "payload");
            Directory.CreateDirectory(pkgDir);
            File.WriteAllText(Path.Combine(pkgDir, "payload.inf"), "x");
            return Task.FromResult(Success);
        }

        public async Task<DriverRunResult> ExportManyAsync(
            IReadOnlyList<string> publishedNames, string destinationDir, CancellationToken ct = default)
        {
            foreach (string name in publishedNames)
            {
                await ExportAsync(name, DriverBackupOrganizer.StageDirFor(destinationDir, name), ct).ConfigureAwait(false);
            }

            return Success;
        }

        public Task<DriverRunResult> DeleteAsync(string publishedName, bool force, CancellationToken ct = default)
            => throw new NotSupportedException("备份测试不触及删除");

        public Task<DriverRunResult> DeleteManyAsync(IReadOnlyList<string> publishedNames, bool force, CancellationToken ct = default)
            => throw new NotSupportedException("备份测试不触及删除");

        public Task<DriverRunResult> AddDriverAsync(string infPath, bool install, CancellationToken ct = default)
            => throw new NotSupportedException("备份测试不触及添加");

        public Task<DriverRunResult> AddManyAsync(IReadOnlyList<string> infPaths, bool install, CancellationToken ct = default)
            => throw new NotSupportedException("备份测试不触及添加");
    }

    private static readonly DriverRunResult Success = new(true, 0, "");

    /// <summary>空的设备绑定源。**必须显式注入**——<c>DriverBackupService</c> 该参数的默认值是
    /// 真实注册表采集（<c>DriverDeviceMapper.CollectDeviceBindings</c>），不注入会读到本机设备。</summary>
    private static IReadOnlyDictionary<string, List<DriverDeviceMapper.DeviceBinding>> EmptyBindings()
        => new Dictionary<string, List<DriverDeviceMapper.DeviceBinding>>(StringComparer.OrdinalIgnoreCase);

    private static DriverPackage Pkg(string published) => new()
    {
        PublishedName = published,
        OriginalName = "origin.inf",
        Provider = "TestCorp",
        Version = "1.2.3.4",
        ClassName = "Net",
    };

    /// <summary>
    /// 端到端跑一次向导（真扫描替身 → 真 DriverBackupService → 真 manifest.json 落盘），返回 manifest 文本。
    /// <c>allThirdParty</c> 即向导第二个单选项：true =「全部第三方驱动」/ false =「仅勾选的驱动包」。
    /// </summary>
    private async Task<string> RunWizardAsync(bool allThirdParty)
    {
        string destDir = Path.Combine(_dir, $"scope_{(allThirdParty ? "all" : "selected")}");
        var pnp = new FakePnpUtil();
        var vm = new DriverManagerViewModel(
            pnp, new DriverScanner(pnp), new DriverBackupService(pnp, EmptyBindings));
        vm.Packages.Add(new DriverPackageVm(Pkg("oem1.inf")));
        vm.BackupWizardRequest = ()
            => (new List<string> { "oem1.inf" }, destDir, allThirdParty);

        await vm.RunBackupCommand.ExecuteAsync(null);

        string manifestPath = Path.Combine(destDir, "manifest.json");
        Assert.True(File.Exists(manifestPath),
            $"备份未落盘 manifest（{manifestPath}）——StatusText={vm.StatusText}。"
            + "若服务层 exported.Count == 0 会提前返回，需检查 Packages 与向导返回的包名是否对位。");
        return File.ReadAllText(manifestPath);
    }

    [Fact]
    public async Task Wizard_AllThirdParty_ManifestRecordsThirdPartyOnly()
    {
        string manifest = await RunWizardAsync(allThirdParty: true);

        Assert.Contains("\"Scope\": \"ThirdPartyOnly\"", manifest);
    }

    [Fact]
    public async Task Wizard_SelectedSubset_ManifestRecordsSelected()
    {
        string manifest = await RunWizardAsync(allThirdParty: false);

        // 🔴 反退化核心断言：勾选子集**不得**被标成「全部第三方」
        Assert.DoesNotContain("\"Scope\": \"ThirdPartyOnly\"", manifest);
        Assert.Contains("\"Scope\": \"Selected\"", manifest);
    }

    /// <summary>
    /// 核心不变量：两条语义不同的分支**必须产出不同的 scope 标签**。
    /// 单独成例是因为「两分支同档」正是本缺陷的原始形态，而上面两例各自只钉一个值——
    /// 若将来有人把两档互换，两例仍会分别失败，但本例能直接点出「两分支塌缩成一档」。
    /// </summary>
    [Fact]
    public async Task Wizard_TwoBranches_MustNotCollapseToSameScope()
    {
        string all = await RunWizardAsync(allThirdParty: true);
        string selected = await RunWizardAsync(allThirdParty: false);

        string allScope = ExtractScope(all);
        string selectedScope = ExtractScope(selected);
        Assert.NotEqual(allScope, selectedScope);
    }

    /// <summary>从 manifest 文本取出 Scope 字段值（不引 System.Text.Json：这是文本级元数据断言）。</summary>
    private static string ExtractScope(string manifest)
    {
        const string key = "\"Scope\": \"";
        int i = manifest.IndexOf(key, StringComparison.Ordinal);
        Assert.True(i >= 0, "manifest 中未找到 Scope 字段——落盘字段名已变，本锁需同步修订");
        int start = i + key.Length;
        int end = manifest.IndexOf('"', start);
        Assert.True(end > start, "manifest 的 Scope 字段值未正确闭合");
        return manifest[start..end];
    }

    /// <summary>
    /// 枚举档位数值锁：<c>Selected</c> 必须是**追加**而非重排（2026-09-15 反向验证实测）。
    /// <para>
    /// 把 <c>Selected = 2</c> 改成 <c>1</c>（与 <c>ThirdPartyOnly</c> 撞值）后，本类**红了 3 条**而非 1 条：
    /// 除本例外的两条 <c>Wizard_*</c> 也红 —— 因为 .NET <c>Enum.ToString()</c> 对**重名值返回最先定义的名字**，
    /// 于是 <c>Selected</c> 落盘直接退化成 <c>"ThirdPartyOnly"</c>。
    /// ⇒ 重排的危害不止「将来按数值落盘的消费者错位」，**今天就**会让 manifest 标签静默说反。
    /// </para>
    /// </summary>
    [Fact]
    public void Enum_SelectedIsAppended_NotReordered()
    {
        Assert.Equal(0, (int)DriverBackupScope.All);
        Assert.Equal(1, (int)DriverBackupScope.ThirdPartyOnly);
        Assert.Equal(2, (int)DriverBackupScope.Selected);
    }

    /// <summary>
    /// <c>All</c> 档在本向导**恒不可达**：UI 只提供「全部第三方 / 仅勾选」两项，
    /// 都不含收件箱驱动。若 VM 回退到 <c>All</c>，会把第三方子集标成「全部驱动包」（比标成
    /// ThirdPartyOnly 错得更远）⇒ 以 fail-safe 全仓扫锁住。
    /// </summary>
    [Fact]
    public void ViewModel_MustNotUseScopeAll()
    {
        string path = Path.Combine(RepoRoot(), "src/SystemToolkit.Modules.DriverManager",
            "DriverManagerViewModel.Operations.cs");
        Assert.True(File.Exists(path), $"未找到 {path}");

        var hits = File.ReadAllLines(path)
            .Select((line, idx) => (line, idx))
            .Where(t => t.line.Contains("DriverBackupScope.All", StringComparison.Ordinal))
            .Select(t => $"  L{t.idx + 1}: {t.line.Trim()}")
            .ToList();

        Assert.True(hits.Count == 0,
            "备份向导不可能覆盖收件箱驱动，VM 不得使用 DriverBackupScope.All（B-🟡-3 回归）："
            + Environment.NewLine + string.Join(Environment.NewLine, hits));
    }

    private static string RepoRoot()
    {
        DirectoryInfo? d = new(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "SystemToolkit.sln")))
        {
            d = d.Parent;
        }

        return d?.FullName ?? throw new InvalidOperationException("未找到仓库根（SystemToolkit.sln）");
    }
}
