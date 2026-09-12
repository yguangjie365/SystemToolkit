using System.Text;
using SystemToolkit.Core.Network.LanScan;

namespace SystemToolkit.ElevatedHelper;

/// <summary>
/// 子命令 osver：对批量 IPv4 逐台远程 WMI 读真实 OS 名与版本号（NET-6 精确识别，用户批准提权路线）。
/// <para>
/// 🔴 双端同源校验（<see cref="LanOsVerRules"/>）：任一 token 非严格 IPv4 或数量超限 → 整批拒绝
/// （零执行、不回显参数）——本进程带管理员令牌运行，不信任调用方拼好的参数。
/// 动作只读（WMI 查询）+ 仅写 --out（其路径限制已在 Program 入口统一把关）；无权限/非 Windows
/// 目标逐台写 <c>ip=</c> 空值行降级，恒退出 0（成败语义在结果文件里）。
/// </para>
/// </summary>
internal static class OsVerRunner
{
    public static async Task<int> RunAsync(string[] ipv4s, string outFile)
    {
        if (ipv4s.Length == 0 || ipv4s.Length > LanOsVerRules.MaxTargets
            || Array.Exists(ipv4s, static ip => !LanOsVerRules.IsValidTargetIp(ip)))
        {
            return WriteError("osver 参数非法（应为 ≤64 个严格 IPv4）");
        }

        var output = new StringBuilder();
        foreach (string ip in ipv4s)
        {
            LanOsRemoteInfo? info = await LanOsWmi.TryQueryAsync(ip).ConfigureAwait(false);
            output.AppendLine(LanOsVerRules.FormatResultLine(ip, info));
        }

        try
        {
            await File.WriteAllTextAsync(outFile, output.ToString(), Encoding.UTF8).ConfigureAwait(false);
            return 0;
        }
        catch
        {
            return WriteError("osver 结果写入失败");
        }
    }

    private static int WriteError(string message)
    {
        Console.Error.WriteLine(message);
        return -2;
    }
}
