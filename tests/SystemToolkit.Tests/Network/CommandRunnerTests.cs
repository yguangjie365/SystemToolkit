using System.Text;
using SystemToolkit.Core.Network.Services;

namespace SystemToolkit.Tests;

/// <summary>
/// CommandRunner 静态初始化防线测试（真机修复回归）：cp936 等非 UTF 系代码页必须注册
/// <c>CodePagesEncodingProvider</c> 才能被 <c>Encoding.GetEncoding</c> 解析——此前未注册，
/// 静态构造抛异常 → TypeInitializationException → 修复页所有 netsh/ipconfig 操作全灭。
/// 此测试在真实 Windows 进程里触发静态构造，防回归。自旧工程移植，L15 英文命名。
/// </summary>
public class CommandRunnerTests
{
    [Fact]
    public void StaticInit_InstanceCreation_DoesNotThrow()
    {
        Exception? ex = Record.Exception(() => new CommandRunner());

        Assert.Null(ex);
    }

    [Fact]
    public void CodePagesRegistered_Cp936_DecodesChinese()
    {
        // 先触发 CommandRunner 静态构造（注册 CodePagesEncodingProvider 的位置）
        Exception? initEx = Record.Exception(() => new CommandRunner());
        Assert.Null(initEx);

        // 「网络」的 GBK 字节序列（netsh 中文输出的真实形态）
        byte[] gbkBytes = [0xCD, 0xF8, 0xC2, 0xE7];

        var gbk = Encoding.GetEncoding(936);

        Assert.Equal("网络", gbk.GetString(gbkBytes));
    }

    [Fact]
    public async Task RunAsync_RealNetshShowGlobal_EchoAndExitCode()
    {
        // 走一次真实 netsh（只读 show global，无副作用）：验证编码链路端到端可用
        var runner = new CommandRunner();
        var lines = new List<string>();

        int exit = await runner.RunAsync("netsh", "interface tcp show global", lines.Add);

        Assert.Equal(0, exit);
        Assert.NotEmpty(lines);
    }
}
