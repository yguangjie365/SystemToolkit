using System.Collections.Generic;

namespace UiEditor.Core;

/// <summary>
/// 源文件里解析出的一个「作者布局元素」（已排除资源/模板内部件）。
/// </summary>
/// <param name="LocalName">标签本地名，如 <c>Border</c> / <c>TextBlock</c> / <c>Grid.RowDefinition</c>。</param>
/// <param name="Name">该元素的 <c>x:Name</c>（可空——无名元素靠 <see cref="Line"/> 定位）。</param>
/// <param name="Line">开标签在源文件中的行号（1-based）。字节 splice 不改行数，故行号稳定。</param>
/// <param name="Attributes">开标签上的属性（本地名→值，已剔除 xmlns 声明与 x:Name）。</param>
public sealed record InputElement(
    string LocalName,
    string? Name,
    int Line,
    Dictionary<string, string> Attributes);
