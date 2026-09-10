using System;
using System.IO;
using System.Windows;
using SystemToolkit.Core.Utilities;

namespace SystemToolkit.UI.Common;

/// <summary>
/// 主题管理（ADR-005：双主题 Claude.Light / Nvidia.Dark 可切换，不重启）。
/// 切换 = 替换 Application.Resources.MergedDictionaries[0]（令牌字典），
/// 全站 {DynamicResource} 引用自动刷新；Icons.xaml（[1]）不动。
/// 持久化：%AppData%/SystemToolkit/appearance.json（AtomicFile 原子写）。
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
    }

    /// <summary>启动入口：读持久化主题应用（损坏/缺失回退默认）。任何视图解析资源前调用。</summary>
    public static void ApplyCurrentForStartup()
    {
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
    public static void ApplyAndPersist(string themeId)
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
        }
        catch (Exception)
        {
            // 持久化失败不回滚切换（本次会话仍生效），下次启动回退旧偏好
        }
    }

    private static string AppearancePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SystemToolkit", "appearance.json");

    private static readonly System.Text.Json.JsonSerializerOptions AppearanceOpts = new() { WriteIndented = true };

    private sealed record Appearance(string? Theme);
}
