using System.Globalization;
using System.Runtime.Versioning;
using SystemToolkit.Core.GameManager.Models;

namespace SystemToolkit.Core.GameManager.Services;

/// <summary>
/// Steam 客户端管理服务（聚合 14 条 API，对应 NexBox steam.rs 14 条 Tauri 命令）。
/// <para>所有操作均为纯本地 + 少量可选 HTTP（头像在线兜底），与 Blazor/WPF 模块零耦合。</para>
/// <para>本服务本身无 Windows 互操作以外的平台敏感代码；为避免调用方误误用，
/// 在模块层注册时自行加 [SupportedOSPlatform("windows")] 断言即可。</para>
/// </summary>
public sealed partial class SteamService
{
    // =============== S3 用户 ===============
    /// <summary>
    /// 解析 config/loginusers.vdf。格式：
    /// <c>"users"{ "steam64" { AccountName / PersonaName / MostRecent / RememberPassword / Timestamp } }</c>
    /// </summary>
    /// <summary>解析 config/loginusers.vdf（记住的用户，按最近使用排序）。</summary>
    [SupportedOSPlatform("windows")]
    public SteamUser[] ParseLoginUsers(string steamInstallPath)
    {
        string vdfPath = Path.Combine(steamInstallPath, "config", "loginusers.vdf");
        if (!File.Exists(vdfPath))
        {
            _logger.Warn($"loginusers.vdf 不存在：{vdfPath}");
            return Array.Empty<SteamUser>();
        }
        string raw = File.ReadAllText(vdfPath);
        VdfValue root = VdfParser.Parse(raw); // {"users":{...}}
        VdfValue? usersObj = null;
        foreach ((string? k, VdfValue? v) in (root.GetObjEntries() ?? Array.Empty<KeyValuePair<string, VdfValue>>()))
        {
            if (string.Equals(k, "users", StringComparison.OrdinalIgnoreCase)
                || v.GetObjEntries() is not null)
            {
                // 文件里有时根对象里第一项直接就是 "users"，也可能没有外层 users{} 但内容同
                if (v.GetObjEntries() is { } ents && ents.Count > 0
                    && ents[0].Value.GetObjEntries() is { Count: > 0 } inner
                    && inner[0].Key != k)
                {
                    usersObj = v;
                    break;
                }
            }
        }
        // 如果没找到 users 子块但 root 本身有 steam64 一样的子项，就当作 root 是 users 块
        IReadOnlyList<KeyValuePair<string, VdfValue>>? entries = usersObj?.GetObjEntries() ?? root.GetObjEntries();
        if (entries is null)
            return Array.Empty<SteamUser>();
        string? activeUser = SteamRegistry.GetActiveProcessUserId();
        var list = new List<SteamUser>(capacity: entries.Count);
        foreach ((string? steam64, VdfValue? uobj) in entries)
        {
            if (string.IsNullOrEmpty(steam64) || uobj.GetObjEntries() is null)
                continue;
            // 跳过无效 id：id 中含非数字的排除掉（与 Rust parse u64 等价）
            if (!ulong.TryParse(steam64, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                continue;
            list.Add(new SteamUser
            {
                SteamId64 = steam64,
                AccountName = uobj.GetStr("AccountName") ?? string.Empty,
                PersonaName = uobj.GetStr("PersonaName") ?? string.Empty,
                MostRecent = ParseFlag(uobj.GetStr("MostRecent")),
                RememberPassword = ParseFlag(uobj.GetStr("RememberPassword")),
                Timestamp = ParseULong(uobj.GetStr("Timestamp")),
                AvatarUrl = uobj.GetStr("Avatar"),
                AvatarMediumUrl = uobj.GetStr("AvatarMedium"),
                AvatarFullUrl = uobj.GetStr("AvatarFullUrl"),
            });
        }
        // 排序：MostRecent=1 的放第一；再按 Timestamp 倒序
        SteamUser[] sorted = list
            .OrderByDescending(u => u.MostRecent)
            .ThenByDescending(u => u.Timestamp)
            .ToArray();
        if (sorted.Length > 0)
            _logger.Info($"解析 loginusers.vdf：{sorted.Length} 位用户，MostRecent={sorted[0].AccountName}，ActiveUser={activeUser ?? "<none>"}");
        return sorted;
    }

    // =============== S12 切换账户 ===============
    /// <summary>切换登录账户：关 Steam → 重写 loginusers.vdf → 写 AutoLoginUser → 以 -login 拉起。</summary>
    [SupportedOSPlatform("windows")]
    public bool SwitchAccount(string accountName)
    {
        if (string.IsNullOrWhiteSpace(accountName))
            return false;
        SteamInstallInfo info = GetInstallInfo();
        if (!info.Installed || info.InstallPath is null)
            return false;
        string luPath = Path.Combine(info.InstallPath, "config", "loginusers.vdf");
        if (!File.Exists(luPath))
            return false;

        // ① 如果 Steam 正在运行 → 先 taskkill 退出，最多等 10s
        bool killedOk = true;
        if (info.IsRunning)
        {
            killedOk = SteamProcessDetector.KillSteamAndWait();
            _logger.Info($"切账号：关闭 Steam 结果={killedOk}");
            if (!killedOk)
                return false;
        }
        // ② 重新生成 loginusers.vdf：只把目标用户标记 MostRecent=1 / RememberPassword=1
        try
        {
            if (!RewriteLoginUsersWithMostRecent(luPath, accountName))
            {
                _logger.Info($"切账号中止：目标账户 {accountName} 不在 loginusers.vdf 中");
                return false;
            }
        }
        catch (Exception e)
        {
            _logger.Error("重写 loginusers.vdf 失败，已保留.bak 回滚", e);
            return false;
        }
        // ③ 注册表写 AutoLoginUser + RememberPassword=1
        try
        { SteamRegistry.SetAutoLoginUser(accountName, rememberPassword: true); }
        catch (Exception e) { _logger.Error("写注册表 AutoLoginUser 失败", e); return false; }
        // ④ steam.exe -login {account}
        string exe = SteamProcessDetector.GetSteamExePath(info.InstallPath);
        if (File.Exists(exe))
        {
            try
            { SteamProcessDetector.LaunchSteam(exe, $"-login {QuoteArg(accountName)}"); }
            catch (Exception e) { _logger.Error("启动 Steam.exe -login 失败", e); return false; }
        }
        return true;
    }

    // =============== S14 删除用户 ===============
    /// <summary>从 loginusers.vdf 移除指定用户（Steam 运行中则拒绝）。</summary>
    [SupportedOSPlatform("windows")]
    public bool DeleteUser(string steamId64)
    {
        if (string.IsNullOrEmpty(steamId64))
            return false;
        if (SteamProcessDetector.IsRunning())
        {
            _logger.Warn("删除用户失败：Steam 仍在运行");
            return false;
        }
        SteamInstallInfo info = GetInstallInfo();
        if (!info.Installed || info.InstallPath is null)
            return false;
        string lu = Path.Combine(info.InstallPath, "config", "loginusers.vdf");
        if (!File.Exists(lu))
            return false;
        try
        {
            RewriteLoginUsersRemoveUser(lu, steamId64);
            return true;
        }
        catch (Exception e)
        {
            _logger.Error("删除用户失败", e);
            return false;
        }
    }

    // 切账号 / 删除用户：重写 loginusers.vdf。保证缩进风格、其他用户段保持与原文件一致；
    // 写入前先写 .bak-yyyyMMddHHmmss（与 BlazorShellBridge 写文件安全守卫同款策略）
    /// <returns>目标账户是否在 loginusers.vdf 中命中（false = 调用方必须中止，不得写盘）。</returns>
    private static bool RewriteLoginUsersWithMostRecent(string vdfPath, string targetAccount)
    {
        string bakPath = vdfPath + ".bak-" + DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        File.Copy(vdfPath, bakPath, overwrite: true);

        VdfValue root = VdfParser.Parse(File.ReadAllText(vdfPath));
        VdfValue? usersObj = null;
        foreach ((string? k, VdfValue? v) in (root.GetObjEntries() ?? Array.Empty<KeyValuePair<string, VdfValue>>()))
        {
            if (string.Equals(k, "users", StringComparison.OrdinalIgnoreCase))
            { usersObj = v; break; }
        }
        IReadOnlyList<KeyValuePair<string, VdfValue>>? users = usersObj?.GetObjEntries() ?? root.GetObjEntries();
        if (users is null)
            throw new InvalidDataException("loginusers.vdf 解析不出 users 块");
        var sb = new StringWriter();
        sb.Write("\"users\"\n{\n");
        bool found = false;
        foreach ((string? sid, VdfValue? uobj) in users)
        {
            IReadOnlyList<KeyValuePair<string, VdfValue>> entries = uobj.GetObjEntries() ?? Array.Empty<KeyValuePair<string, VdfValue>>();
            bool isTarget = entries.Any(x => x.Key == "AccountName" && string.Equals(x.Value.GetString(), targetAccount, StringComparison.Ordinal));
            sb.Write($"\t\"{sid}\"\n\t{{\n");
            // 先写原字段，覆盖 MostRecent / RememberPassword 对目标账户
            bool wroteM = false, wroteR = false, wroteA = false, wroteP = false, wroteT = false;
            foreach ((string? k, VdfValue? v) in entries)
            {
                // 【坑】必须用 GetString() 取本节点自身的值；GetStr(k) 是在子节点里按 key 查，
                // 对字符串字段永远返回 null，曾导致重写后所有字段值变成空串（2026-09-03 修复）。
                string val = v.GetString() ?? string.Empty;
                if (k == "MostRecent")
                { val = isTarget ? "1" : "0"; wroteM = true; found |= isTarget; }
                else if (k == "RememberPassword")
                { val = isTarget ? "1" : (val == "1" ? "1" : "0"); wroteR = true; }
                sb.Write($"\t\t\"{k}\"\t\t\"{Escape(val)}\"\n");
                if (k == "AccountName")
                    wroteA = true;
                if (k == "PersonaName")
                    wroteP = true;
                if (k == "Timestamp")
                    wroteT = true;
            }
            if (!wroteM)
                sb.Write($"\t\t\"MostRecent\"\t\t\"{(isTarget ? 1 : 0)}\"\n");
            if (!wroteR)
                sb.Write($"\t\t\"RememberPassword\"\t\t\"{(isTarget ? 1 : 0)}\"\n");
            if (!wroteA)
                sb.Write($"\t\t\"AccountName\"\t\t\"{Escape(targetAccount)}\"\n");
            if (!wroteP)
                sb.Write($"\t\t\"PersonaName\"\t\t\"{Escape(targetAccount)}\"\n");
            if (!wroteT)
                sb.Write($"\t\t\"Timestamp\"\t\t\"0\"\n");
            sb.Write("\t}\n");
        }
        sb.Write("}\n");
        // REVIEW-3 G-5：目标账户未命中时不得落盘——旧行为把全部用户 MostRecent 清 0 才
        // 拉起 -login，登录态被无意义篡改（found 变量算完即丢）。
        if (!found)
        {
            return false;
        }
        // 🔴 Steam loginusers.vdf 写坏会导致 Steam 登录态丢失——必须原子写（02 分册 §六）
        SystemToolkit.Core.Utilities.AtomicFile.WriteAllText(vdfPath, sb.ToString());
        return true;
    }

    private static void RewriteLoginUsersRemoveUser(string vdfPath, string steam64)
    {
        string bak = vdfPath + ".bak-" + DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        File.Copy(vdfPath, bak, overwrite: true);
        VdfValue root = VdfParser.Parse(File.ReadAllText(vdfPath));
        VdfValue? usersObj = null;
        string rootKey = "users";
        IReadOnlyList<KeyValuePair<string, VdfValue>> ents = root.GetObjEntries() ?? Array.Empty<KeyValuePair<string, VdfValue>>();
        foreach ((string? k, VdfValue? v) in ents)
        {
            if (string.Equals(k, "users", StringComparison.OrdinalIgnoreCase))
            {
                usersObj = v;
                rootKey = k;
                break;
            }
        }
        IReadOnlyList<KeyValuePair<string, VdfValue>>? users = usersObj?.GetObjEntries() ?? ents;
        if (users is null)
            return;
        var filtered = users.Where(x => x.Key != steam64).ToList();
        var sb = new StringWriter();
        sb.Write('"');
        sb.Write(rootKey);
        sb.Write("\"\n{\n");
        foreach ((string? sid, VdfValue? uobj) in filtered)
        {
            sb.Write("\t\"");
            sb.Write(sid);
            sb.Write("\"\n\t{\n");
            foreach ((string? k, VdfValue? v) in (uobj.GetObjEntries() ?? Array.Empty<KeyValuePair<string, VdfValue>>()))
            {
                sb.Write("\t\t\"");
                sb.Write(k);
                sb.Write("\"\t\t\"");
                sb.Write(Escape(v.GetString() ?? string.Empty));
                sb.Write("\"\n");
            }
            sb.Write("\t}\n");
        }
        sb.Write("}\n");
        // 🔴 Steam loginusers.vdf 写坏会导致 Steam 登录态丢失——必须原子写（02 分册 §六）
        SystemToolkit.Core.Utilities.AtomicFile.WriteAllText(vdfPath, sb.ToString());
    }

}
