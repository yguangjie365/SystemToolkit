using System.Collections.Generic;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace UiEditor.Core;

/// <summary>
/// 把 View 的 <c>.xaml</c> 源文本解析成「作者布局元素」列表（含源行号），供元素树与写回定位使用。
/// <para>
/// 关键：<b>排除资源/模板/样式内部件</b>（<c>ControlTemplate</c>/<c>DataTemplate</c>/<c>Setter</c>/
/// <c>*.Resources</c>/<c>*.Style</c>/<c>*.Template</c>/触发器等）——Spike #3 实证运行时可视树会穿透这些
/// 内部件（<c>PART_*</c>/<c>contentPresenter</c>），而编辑器只应暴露作者在布局里写的元素。
/// </para>
/// </summary>
public static class SourceMap
{
    private static readonly XNamespace XNs = "http://schemas.microsoft.com/winfx/2006/xaml";

    // 独立的模板/样式/资源/触发器元素：整棵子树排除
    private static readonly HashSet<string> ExcludedElements = new(System.StringComparer.Ordinal)
    {
        "ControlTemplate", "DataTemplate", "HierarchicalDataTemplate",
        "Setter", "Style", "ResourceDictionary",
        "Trigger", "DataTrigger", "MultiTrigger", "EventTrigger", "Storyboard",
        "ThicknessAnimation", "ColorAnimation", "DoubleAnimation",
    };

    // 属性元素语法里，这些「属性」的子树是资源/样式/模板/触发器 → 排除
    private static readonly string[] ExcludedPropertySuffixes =
    {
        ".Resources", ".Style", ".Template", ".ItemTemplate", ".ContentTemplate",
        ".HeaderTemplate", ".ItemContainerStyle", ".Triggers", ".Setters", ".Overrides",
    };

    public static List<InputElement> Build(string xamlText)
    {
        var list = new List<InputElement>();
        XDocument doc = XDocument.Parse(xamlText, LoadOptions.SetLineInfo);
        if (doc.Root is null)
        {
            return list;
        }

        Walk(doc.Root, insideExcluded: false, list);
        return list;
    }

    /// <summary>解析为作者元素<b>树</b>（含父子层级），供 UI 树与示意式布局预览同源渲染。根被排除时返回 null。</summary>
    public static SourceNode? BuildTree(string xamlText)
    {
        XDocument doc = XDocument.Parse(xamlText, LoadOptions.SetLineInfo);
        return doc.Root is null || IsExcluded(doc.Root) ? null : BuildNode(doc.Root);
    }

    private static SourceNode BuildNode(XElement el)
    {
        InputElement info = ToElement(el);
        var children = new List<SourceNode>();
        foreach (XElement child in el.Elements())
        {
            if (!IsExcluded(child))
            {
                children.Add(BuildNode(child));
            }
        }

        return new SourceNode(info.LocalName, info.Name, info.Line, info.Attributes, children);
    }

    private static void Walk(XElement el, bool insideExcluded, List<InputElement> outList)
    {
        bool excluded = insideExcluded || IsExcluded(el);

        if (!excluded)
        {
            outList.Add(ToElement(el));
        }

        foreach (XElement child in el.Elements())
        {
            Walk(child, excluded, outList);
        }
    }

    private static bool IsExcluded(XElement el)
    {
        string name = el.Name.LocalName;
        if (ExcludedElements.Contains(name))
        {
            return true;
        }

        // 属性元素：Grid.RowDefinitions / Button.Content 等——只有资源/样式/模板类才排除，布局类保留
        foreach (string suffix in ExcludedPropertySuffixes)
        {
            if (name.EndsWith(suffix, System.StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static InputElement ToElement(XElement el)
    {
        string? name = el.Attribute(XNs + "Name")?.Value;
        var attrs = el.Attributes()
            .Where(a => !a.IsNamespaceDeclaration)
            .Where(a => a.Name != XNs + "Name")
            .GroupBy(a => a.Name.LocalName)
            .ToDictionary(g => g.Key, g => g.Last().Value, System.StringComparer.Ordinal);

        int line = el is IXmlLineInfo li && li.HasLineInfo() ? li.LineNumber : 0;
        return new InputElement(el.Name.LocalName, name, line, attrs);
    }
}
