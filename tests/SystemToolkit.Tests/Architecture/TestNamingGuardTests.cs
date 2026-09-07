using System.Text.RegularExpressions;

namespace SystemToolkit.Tests.Architecture;

/// <summary>
/// 测试命名守卫（L15 裁定落地的机器强制，2026-09-05）：
/// 测试方法标识符必须为 ASCII（01 分册 §2.1 英文命名）——中文方法名曾三次混入
/// （FIX-2 批次两文件 + 本守卫撰写时自查又现两文件），人工复检不可靠，故机器化。
/// 范围：tests/ 全部 .cs 的 public 测试入口（Task/void 声明行）。
/// </summary>
public class TestNamingGuardTests
{
    private static readonly Regex TestMethodDecl = new(
        @"public\s+(?:static\s+)?(?:async\s+)?(?:Task|void)\s+\S+", RegexOptions.Compiled);
    private static readonly Regex Cjk = new(@"[\u4e00-\u9fff]", RegexOptions.Compiled);

    [Fact]
    public void TestNamingGuard_TestMethodIdentifiers_MustBeAscii()
    {
        var offenders = new List<string>();
        foreach (string file in Directory.EnumerateFiles(
                     Path.Combine(RepoRoot(), "tests"), "*.cs", SearchOption.AllDirectories))
        {
            string normalized = file.Replace('\\', '/');
            if (normalized.Contains("/obj/") || normalized.Contains("/bin/"))
            {
                continue;
            }

            foreach (string line in File.ReadLines(file))
            {
                string decl = TestMethodDecl.Match(line).Value;
                if (decl.Length > 0 && Cjk.IsMatch(decl))
                {
                    offenders.Add($"{normalized.Replace(RepoRoot(), "")} {decl.Trim()}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "测试方法标识符含中文（01 分册 §2.1 英文命名，2026-09-05 L15 用户裁定）：\n"
            + string.Join("\n", offenders));
    }

    // ---------------- 反向验证自检（03 §4.1） ----------------

    [Fact]
    public void TestNamingGuard_DetectorFunction_SampleInjection_ReverseVerification()
    {
        // 样本中的 CJK 用 \u 转义书写——守卫扫描源码文本，转义后源码即真 ASCII（自举合规）
        string cjkSample = "public void BackupAsync_\u540c\u540d\u539f\u59cbinf\u591a\u7248\u672c_\u5404\u81ea\u6210\u76ee\u5f55()";
        Assert.Matches(TestMethodDecl, cjkSample);
        Assert.Matches(Cjk, cjkSample);

        string okSample = "public async Task BackupAsync_SameOriginalInfMultipleVersions_SeparateDirs()";
        Assert.Matches(TestMethodDecl, okSample);
        Assert.DoesNotMatch(Cjk, okSample);
    }

    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SystemToolkit.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("未定位到仓库根（SystemToolkit.sln）");
    }
}
