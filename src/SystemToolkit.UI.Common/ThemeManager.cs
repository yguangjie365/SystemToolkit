using System;
using System.IO;
using System.Windows;
using SystemToolkit.Core.Utilities;
using SystemToolkit.UI.Common.Themes.Tokens;

namespace SystemToolkit.UI.Common;

/// <summary>
/// 主题管理（ADR-005：双主题 Claude.Light / Nvidia.Dark 可切换，不重启）。
/// 切换 = 替换 Application.Resources.MergedDictionaries[0]（令牌字典），
/// 全站 {DynamicResource} 引用自动刷新；Icons.xaml（[1]）不动。
/// 持久化：%LOCALAPPDATA%\SystemToolkit\appearance.json（02 §六统一配置根；AtomicFile 原子写）。
/// </summary>
public static class ThemeManager
{
    /// <summary>
    /// 可用主题 id → 包 URI（新增主题在此登记 + SettingsView 下拉）。
    /// <para>
    /// <b>IsDark</b>：该主题是否为深色底。供需要「按明暗分档」的派生色使用
    /// （2026-09-11 起：音乐播放器皮肤在深色主题下改走深染档）——
    /// 把语义放进主题定义，而不是散落各处的 id 字符串比较，新增主题时不会漏登记。
    /// </para>
    /// </summary>
    public static readonly (string Id, string Label, string PackUri, bool IsDark)[] Themes =
    [
        ("claude", "Claude 浅色", "pack://application:,,,/SystemToolkit.UI.Common;component/Themes/Packs/Claude/Claude.Light.xaml", false),
        ("nvidia", "NVIDIA 深色", "pack://application:,,,/SystemToolkit.UI.Common;component/Themes/Packs/Nvidia/Nvidia.Dark.xaml", true),
    ];

    public const string DefaultThemeId = "claude";

    /// <summary>当前主题是否为深色底（未登记的主题按浅色处理）。</summary>
    public static bool IsDarkTheme
    {
        get
        {
            foreach ((string Id, string Label, string PackUri, bool IsDark) t in Themes)
            {
                if (t.Id == CurrentThemeId)
                {
                    return t.IsDark;
                }
            }

            return false;
        }
    }

    /// <summary>当前主题 id（未应用过 = Default）。</summary>
    public static string CurrentThemeId { get; private set; } = DefaultThemeId;

    /// <summary>
    /// 主题已切换（<see cref="Apply"/> 成功后触发）。
    /// <para>
    /// 宿主（MainWindow）据此**重建当前视图**：令牌类引用已全部走 <c>DynamicResource</c> 自动刷新，
    /// 但 <c>Style.BasedOn</c> 不支持 DynamicResource（WPF 硬限制），继承了主题包样式的派生样式
    /// 仍指向旧包实例——重建视图可让其按新包重新解析，做到真正的"即时切换"。
    /// </para>
    /// </summary>
    public static event Action? ThemeChanged;

    /// <summary>按 id 应用主题；未知 id 回退默认。启动（App.xaml.cs，字典 0 占位后）与切换共用。</summary>
    public static void Apply(string? themeId)
    {
        string id = themeId is null ? DefaultThemeId : themeId.Trim().ToLowerInvariant();
        // 🟡 V14-U7：判据统一为「查不到 = null」。原实现是**两套**判据——先 `match.Id is null`
        // 表示"未匹配"，后面又用 `string.IsNullOrEmpty(match.PackUri)` 表示"PackUri 空"；
        // 同一件事两个标志，任一处漂移就会出现"没匹配却当匹配用"。
        (string Id, string Label, string PackUri, bool IsDark)? match = FindTheme(id);
        if (match is null) // 未知 id → 回退默认
        {
            id = DefaultThemeId;
            match = FindTheme(id);
        }

        Application app = Application.Current;
        if (app is null)
        {
            throw new InvalidOperationException("ThemeManager.Apply 需在 Application 初始化后调用");
        }

        System.Collections.ObjectModel.Collection<ResourceDictionary> dicts = app.Resources.MergedDictionaries;
        if (dicts.Count == 0)
        {
            throw new InvalidOperationException("App.Resources.MergedDictionaries 为空——App.xaml 须保留一个占位字典");
        }

        // DefaultThemeId 自身未登记（配置/代码错误）时再退到 Themes[0]，保证 packUri 一定有值
        string packUri = match?.PackUri is { Length: > 0 } uri ? uri : Themes[0].PackUri;

        // 快照 → 替换 → 校验 → 失败回滚（危险操作四步纪律的"字典版"）。
        // Source 赋值本身会即时加载并对坏包抛异常（探针实证 2026-09-14），赋值不成立时
        // dicts[0] 保持原样；健康检查失败（加载成功但缺哨兵令牌）则在此显式换回旧字典，
        // 不留"字典已换、令牌缺失"的半截状态，CurrentThemeId 也不推进。
        ResourceDictionary previous = dicts[0];
        dicts[0] = new ResourceDictionary { Source = new Uri(packUri) };
        try
        {
            EnsurePackUsable(dicts[0], packUri);
        }
        catch
        {
            dicts[0] = previous;
            throw;
        }

        CurrentThemeId = id;
        NotifyThemeChanged(); // 宿主重建视图（BasedOn 派生样式跟随，见事件注释）
    }

    /// <summary>按 id 查主题登记项；未登记返回 null（"未匹配"的**唯一**判据）。</summary>
    private static (string Id, string Label, string PackUri, bool IsDark)? FindTheme(string id)
    {
        foreach ((string Id, string Label, string PackUri, bool IsDark) t in Themes)
        {
            if (t.Id == id)
            {
                return t;
            }
        }

        return null;
    }

    /// <summary>
    /// 应用后健康检查（🟡 V14-U7）：确认新字典**真的拿到了设计令牌**。
    /// <para>
    /// 失败语义（2026-09-14 临时探针实证，结论见变更记录）——**何时抛、何时不抛**：
    /// ① **不抛**：包 URI 找不到 / XAML 内容坏。原因：`Source` 赋值**当场就抛**
    ///    （IOException / XamlParseException / WebException）⇒ 根本走不到本检查，
    ///    由调用方既有兜底处理：启动 `App.xaml.cs` 回退默认主题并留 Warn；
    ///    切换 `SettingsViewModel` 下拉回退 + ❌ 文案（两处**都已有** try/catch）。
    /// ② **抛**：字典加载成功却**缺哨兵令牌**（包被改坏 / 键被改名）——这种包不会报任何错，
    ///    只会让全站 `{DynamicResource}` 静默回退默认值，正是本项要拦的"静默成功"。
    /// 抛出前调用点已把字典换回上一个主题，故其唯一后果是"主题没换 + 调用方拿到异常"，
    /// 不会把应用留在半截状态（启动与切换两条路径都不会因它崩，见 ①）。
    /// </para>
    /// </summary>
    private static void EnsurePackUsable(ResourceDictionary dictionary, string packUri)
    {
        if (dictionary.Contains(TokenKeys.Brushes.Brush_Background))
        {
            return;
        }

        throw new InvalidOperationException(
            $"主题包缺少设计令牌哨兵「{TokenKeys.Brushes.Brush_Background}」，已回滚为切换前的主题：{packUri}");
    }

    /// <summary>
    /// 通知全部订阅者主题已切换。
    /// 🟠 V14-U3：原先裸调 <c>ThemeChanged?.Invoke()</c>，两处不合格 ——
    /// ①**多播链会被首个异常中断**，排在后面的订阅者收不到通知（本事件现有 3 个订阅者：
    /// 宿主重建视图 / FileBackup 的 VSS 复查 / Music 的播放器笔刷刷新）；
    /// ②异常会冲出 <see cref="Apply"/>（启动路径 = 崩在 Application 初始化里；切换路径 =
    /// 主题已换、但调用方拿到异常，且持久化那一步被跳过）。
    /// 基础设施事件的口径是「**尽力通知全部订阅者**、自身不抛异常」，故逐个订阅者独立兜底 + 留痕。
    /// </summary>
    private static void NotifyThemeChanged()
    {
        if (ThemeChanged is not { } handlers)
        {
            return;
        }

        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action)handler)();
            }
            catch (Exception ex)
            {
                SystemToolkit.Core.Logging.AppLog.Write(SystemToolkit.Core.Logging.LogEntry.Create(
                    SystemToolkit.Core.Logging.LogLevel.Warn, "theme",
                    $"主题切换订阅者回调异常（该订阅者已跳过，其余照常通知）："
                    + $"{handler.Method.DeclaringType?.Name}.{handler.Method.Name}", ex));
            }
        }
    }

    /// <summary>启动入口：读持久化主题应用（损坏/缺失回退默认）。任何视图解析资源前调用。</summary>
    public static void ApplyCurrentForStartup()
    {
        MigrateLegacyAppearance(); // 02 §六：旧 Roaming 位置一次性迁移

        string? persisted = null;
        try
        {
            if (File.Exists(AppearancePath))
            {
                Appearance? a = System.Text.Json.JsonSerializer.Deserialize<Appearance>(
                    File.ReadAllText(AppearancePath), AppearanceOpts);
                persisted = string.IsNullOrWhiteSpace(a?.Theme) ? null : a.Theme;
            }
        }
        catch (Exception ex)
        {
            persisted = null; // 损坏 = 默认，不阻断启动
            // V14-U1（Q-021 口径，2026-09-14）：偏好读取类"失败不阻断"的 catch **必须留一条日志**
            // —— 否则"用户改过主题、重启却回到默认"这件事在应用内零线索（原为空 catch）。
            SystemToolkit.Core.Logging.AppLog.Write(SystemToolkit.Core.Logging.LogEntry.Create(
                SystemToolkit.Core.Logging.LogLevel.Warn, "theme",
                $"主题偏好读取失败，回退默认主题「{DefaultThemeId}」：{AppearancePath}", ex));
        }

        Apply(persisted);
    }

    /// <summary>运行时切换入口（Settings）：立即应用 + 原子持久化。</summary>
    /// <returns>
    /// 偏好是否**成功落盘**。🟠 审查 2026-09-11（🟠-6）：原为 <c>void</c>，调用方只能无条件
    /// 报"重启后保持"——写失败时这句承诺不成立（「假成功」族）。
    /// </returns>
    public static bool ApplyAndPersist(string themeId)
    {
        Apply(themeId);
        try
        {
            string? dir = Path.GetDirectoryName(AppearancePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            AtomicFile.WriteAllText(AppearancePath,
                System.Text.Json.JsonSerializer.Serialize(new Appearance(CurrentThemeId), AppearanceOpts));
            return true;
        }
        catch (Exception ex)
        {
            // 持久化失败不回滚切换（本次会话仍生效），下次启动回退旧偏好。
            // 🟠-6：原为空 catch——连日志都没有，用户与排查者都无从知道「这次切换其实没存下来」。
            SystemToolkit.Core.Logging.AppLog.Write(SystemToolkit.Core.Logging.LogEntry.Create(
                SystemToolkit.Core.Logging.LogLevel.Warn, "theme",
                $"主题偏好持久化失败（本次会话仍生效，下次启动会回退旧偏好）：{AppearancePath}", ex));
            return false;
        }
    }

    /// <summary>主题偏好落盘位置（02 §六统一配置根：<c>%LOCALAPPDATA%\SystemToolkit\</c>）。</summary>
    private static string AppearancePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SystemToolkit", "appearance.json");

    /// <summary>
    /// 旧版位置（Roaming <c>%AppData%</c>）——2026-09-11 按 02 §六统一到 LOCALAPPDATA，
    /// 本路径仅作一次性迁移来源，不再写入。
    /// </summary>
    private static string LegacyAppearancePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SystemToolkit", "appearance.json");

    /// <summary>
    /// 一次性迁移：新位置缺失且旧位置存在时复制，保留老用户的主题偏好选择。
    /// （范式对齐 <c>RuleManager.MigrateLegacyRulesFile</c>；失败退化为「无偏好 → 默认主题」。）
    /// </summary>
    private static void MigrateLegacyAppearance()
    {
        try
        {
            if (File.Exists(AppearancePath) || !File.Exists(LegacyAppearancePath))
            {
                return;
            }

            string? dir = Path.GetDirectoryName(AppearancePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.Copy(LegacyAppearancePath, AppearancePath);
        }
        catch (Exception ex)
        {
            // V14-U1（Q-021 口径，2026-09-14）：迁移失败不阻断启动（读不到偏好即回退默认主题），
            // 但**必须留一条日志** —— 同文件 ApplyAndPersist 2026-09-11 修过同款空 catch（🟠-6），
            // 当时只修了一处，这两处残留到本轮才被独立点回（"修一处、漏两处"的实证）。
            SystemToolkit.Core.Logging.AppLog.Write(SystemToolkit.Core.Logging.LogEntry.Create(
                SystemToolkit.Core.Logging.LogLevel.Warn, "theme",
                $"旧主题偏好迁移失败（回退默认主题，旧文件仍在 Roaming 原处）：{LegacyAppearancePath}", ex));
        }
    }

    private static readonly System.Text.Json.JsonSerializerOptions AppearanceOpts = new() { WriteIndented = true };

    private sealed record Appearance(string? Theme);
}
