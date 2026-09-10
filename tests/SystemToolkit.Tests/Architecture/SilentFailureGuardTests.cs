namespace SystemToolkit.Tests.Architecture;

/// <summary>
/// 静默失败守卫（🟠 审查 2026-09-10）。
/// <para>
/// 规则 6（AGENTS §3.1）：同一类问题出现第二次必须沉淀为构建时守卫。本仓库已有多次
/// 「吞异常/丢退出码 → 调用方无条件报成功」的事故：O4（还原丢退出码）、O9（保存失败不回滚）、
/// 🟠-1（清单写盘失败仍报「已保存」）、🔴-2（播放中断被当成自然播完）——根因相同：
/// 底层 catch 住异常或丢弃返回值之后不向上传播，上层照打成功文案。
/// </para>
/// <para>
/// 本文件锁定其中可在源码层静态判定的那一支：<c>EnvListService</c> 的用户可见写入口。
/// 反向验证方式：把 <c>SaveJson</c> 改回 <c>void</c>（catch 直接返回），本用例立即变红。
/// </para>
/// </summary>
public class SilentFailureGuardTests
{
    /// <summary>
    /// 用户可见的清单写入口（SaveWinget/SaveManual/SaveDriver）在写盘失败时必须上抛，
    /// 由调用方（AppManagerViewModel.PersistAll）的 catch 转为用户可见的失败提示。
    /// </summary>
    [Fact]
    public void EnvListService_UserFacingSaveEntryPoints_MustRaiseWriteFailure()
    {
        string text = File.ReadAllText(RepoFile("src/SystemToolkit.Core/Software/Services/EnvListService.cs"));

        // ① SaveJson 必须回传结果——曾经的 void + catch 直接 return = 调用方永远以为成功
        Assert.Contains("private bool SaveJson<T>", text);

        // ② 三个用户写入口必须检查返回值并上抛
        foreach (string entry in new[] { "SaveWinget", "SaveManual", "SaveDriver" })
        {
            string signature = $"public void {entry}(IEnumerable<";
            int start = text.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(start >= 0, $"未找到 {entry} 的声明（签名变了？请同步本守卫）");

            int end = text.IndexOf("\n    }", start, StringComparison.Ordinal);
            Assert.True(end > start, $"未定位到 {entry} 的方法体结尾");

            string body = text[start..end];
            Assert.Contains("throw new IOException", body);
        }
    }

    private static string RepoFile(string relativePath)
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SystemToolkit.sln")))
        {
            dir = dir.Parent;
        }

        string root = dir?.FullName ?? throw new InvalidOperationException("未定位到仓库根（SystemToolkit.sln）");
        return Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
    }
}
