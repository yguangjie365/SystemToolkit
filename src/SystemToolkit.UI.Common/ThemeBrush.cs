using System.Windows;
using System.Windows.Media;

namespace SystemToolkit.UI.Common;

/// <summary>
/// 主题笔刷取值（2026-09-08 从 LogLine.FindBrush 提升为公共工具）。
/// 🔴 UI 令牌纪律：C# 侧的画笔一律「先取主题资源，失败才回退硬编码 hex」——
/// 直接 new SolidColorBrush(Color.FromRgb(...)) 会让该颜色脱离主题机制
/// （换肤/主题迭代时不跟随），正是旧工程 428 处裸值事故的 C# 版。
/// 由 <c>CsBrushLiteralGuardTests</c> 强制：C# 中的画笔构造必须走本方法（或 TryFindResource）。
/// </summary>
public static class ThemeBrush
{
    /// <summary>
    /// 取主题笔刷；主题未加载（设计器/测试宿主）或键缺失时回退到 <paramref name="fallbackHex"/>。
    /// </summary>
    /// <param name="key">主题资源键（如 Brush_Success）。</param>
    /// <param name="fallbackHex">回退色值（#RRGGBB，应与主题字典同名键保持一致）。</param>
    public static Brush Find(string key, string fallbackHex)
        => Application.Current?.TryFindResource(key) as Brush
           ?? FrozenFallback(fallbackHex);

    /// <summary>
    /// 构造回退笔刷。
    /// 🟠 V14-U4：必须 <see cref="Freezable.Freeze"/>（照 <c>CoverColorFactory</c> / <c>VinylTextureFactory</c>
    /// 的既有约定）——本方法会被**非 UI 线程**调用（日志行由任意线程产生，<c>LogLine.Create</c> 取行色），
    /// 未冻结的 <see cref="SolidColorBrush"/> 带线程亲和，跨线程被 WPF 访问即抛
    /// 「调用线程无法访问此对象」；冻结后只读、可自由跨线程。
    /// 返回值是共享的不可变实例，调用方不得改写（要改就自己 Clone）。
    /// </summary>
    private static Brush FrozenFallback(string fallbackHex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallbackHex));
        brush.Freeze();
        return brush;
    }
}
