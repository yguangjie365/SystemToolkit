using System;
using System.IO;
using System.Windows;
using SystemToolkit.Core.Utilities;

namespace SystemToolkit.UI.Common;

/// <summary>
/// 主题管理（ADR-005：双主题 Claude.Light / Nvidia.Dark 可切换，不重启）。
/// 切换 = 替换 Application.Resources.MergedDictionaries[0]（令牌字典），
/// 全站 {DynamicResource} 引用自动刷新；Icons.xaml（[1]）不动。
/// 持久化：%LOCALAPPDATA%\SystemToolkit\appearance.json（02 §六统一配置根；AtomicFile 原子写）。
/// </summary>
public static class ThemeManager
{
    /// <summary>可用主题 id → 包 URI（新增主题在此登记 + SettingsView 下拉）。</summary>
    public static readonly (string Id, string Label, string PackUri)[] Themes =
    [
        ("claude", "Claude 浅色", "pack://application:,,,/SystemToolkit.UI.Common;component/Themes/Packs/Claude/Claude.Light.xaml"),
        ("nvidia", "NVIDIA 深色", "pack://application:,,,/SystemToolkit.UI.Common;component/Themes/Packs/Nvidia/Nvidia.Dark.xaml"),
    ];

    public const string DefaultThemeId = "claude";

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
        (string Id, string Label, string PackUri) match = default;
        foreach ((string Id, string Label, string PackUri) t in Themes)
        {
            if (t.Id == id)
            {
                match = t;
                break;
            }
        }

        if (match.Id is null) // 未知 id → 回退默认
        {
            id = DefaultThemeId;
            foreach ((string Id, string Label, string PackUri) t in Themes)
            {
                if (t.Id == id)
                {
                    match = t;
                    break;
                }
            }
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

        string packUri = string.IsNullOrEmpty(match.PackUri) ? Themes[0].PackUri : match.PackUri;
        dicts[0] = new ResourceDictionary { Source = new Uri(packUri) };
        CurrentThemeId = id;
        ThemeChanged?.Invoke(); // 宿主重建视图（BasedOn 派生样式跟随，见事件注释）
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
        catch
        {
            persisted = null; // 损坏 = 默认，不阻断启动
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
        catch (Exception)
        {
            // 迁移失败不阻断启动（读不到偏好即回退默认主题）
        }
    }

    private static readonly System.Text.Json.JsonSerializerOptions AppearanceOpts = new() { WriteIndented = true };

    private sealed record Appearance(string? Theme);
}
