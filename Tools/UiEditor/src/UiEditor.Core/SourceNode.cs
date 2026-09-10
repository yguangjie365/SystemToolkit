using System.Collections.Generic;

namespace UiEditor.Core;

/// <summary>源文件解析出的作者元素树节点（已排除资源/模板内部件）。UI 树与示意预览共用此结构。</summary>
public sealed record SourceNode(
    string LocalName,
    string? Name,
    int Line,
    Dictionary<string, string> Attributes,
    List<SourceNode> Children);
