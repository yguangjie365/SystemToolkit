using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UiEditor.Core;
using Xunit;

namespace UiEditor.Tests;

public class CoreLogicTests
{
    private const string Pres = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private const string Xns = "http://schemas.microsoft.com/winfx/2006/xaml";

    // ───────────── SourceMap：排除资源/模板内部件，只留作者布局元素 ─────────────
    [Fact]
    public void SourceMap_ExcludesTemplateAndResourceInternals()
    {
        string xaml =
            $"<Grid xmlns=\"{Pres}\" xmlns:x=\"{Xns}\">" +
            "  <Grid.Resources>" +
            "    <Style x:Key=\"S\"><Setter Property=\"Background\" Value=\"Red\"/></Style>" +
            "  </Grid.Resources>" +
            "  <Border x:Name=\"HeaderBar\" Grid.Row=\"0\">" +
            "    <Border.Template><ControlTemplate><ContentPresenter x:Name=\"PART_Content\"/></ControlTemplate></Border.Template>" +
            "  </Border>" +
            "  <TextBlock x:Name=\"Body\" Grid.Row=\"1\"/>" +
            "</Grid>";

        List<InputElement> els = SourceMap.Build(xaml);
        HashSet<string?> names = els.Select(e => e.Name).ToHashSet();

        Assert.Contains("HeaderBar", names);
        Assert.Contains("Body", names);
        Assert.DoesNotContain("PART_Content", names);
        // 模板/样式/Setter 内部件一律不进树
        Assert.DoesNotContain(els, e => e.LocalName is "Setter" or "ControlTemplate" or "ContentPresenter");
    }

    [Fact]
    public void SourceMap_RecordsSourceLine()
    {
        string xaml =
            $"<Grid xmlns=\"{Pres}\" xmlns:x=\"{Xns}\">" + Environment.NewLine +
            "  <TextBlock x:Name=\"Body\"/>" + Environment.NewLine +
            "</Grid>";
        InputElement body = SourceMap.Build(xaml).Single(e => e.Name == "Body");
        Assert.Equal(2, body.Line);
    }

    [Fact]
    public void SourceMap_BuildTree_PreservesHierarchyAndExcludesTemplates()
    {
        string xaml =
            $"<Grid xmlns=\"{Pres}\" xmlns:x=\"{Xns}\">" +
            "  <Border x:Name=\"Header\">" +
            "    <Border.Template><ControlTemplate><ContentPresenter x:Name=\"PART_X\"/></ControlTemplate></Border.Template>" +
            "    <TextBlock x:Name=\"Title\"/>" +
            "  </Border>" +
            "</Grid>";

        SourceNode? root = SourceMap.BuildTree(xaml);
        Assert.NotNull(root);
        Assert.Equal("Grid", root!.LocalName);
        SourceNode header = root.Children.Single(c => c.Name == "Header");
        // 模板内部件不进树；作者子元素保留
        Assert.DoesNotContain(header.Children, c => c.Name == "PART_X");
        Assert.Contains(header.Children, c => c.Name == "Title");
    }

    // ───────────── XamlSplicer：字节级、改一处=一行、拒绝非唯一名 ─────────────
    [Fact]
    public void Splicer_SetAttributeByName_ChangesExactlyOneLine()
    {
        string raw =
            $"<Grid xmlns=\"{Pres}\" xmlns:x=\"{Xns}\">" + Environment.NewLine +
            "  <Border x:Name=\"HeaderBar\" Grid.Row=\"0\" Background=\"{DynamicResource Brush_Surface}\"/>" + Environment.NewLine +
            "</Grid>";

        string patched = XamlSplicer.SetAttributeByName(raw, "HeaderBar", "Grid.Row", "1");

        Assert.Contains("Grid.Row=\"1\"", patched);
        Assert.DoesNotContain("Grid.Row=\"0\"", patched);
        // 行数不变、仅目标行变化（证明未重排全文件）
        string[] a = raw.Split('\n');
        string[] b = patched.Split('\n');
        Assert.Equal(a.Length, b.Length);
        Assert.Single(a.Where((_, i) => a[i] != b[i]));
    }

    [Fact]
    public void Splicer_InsertsAttributeWhenAbsent()
    {
        string raw = $"<Grid xmlns=\"{Pres}\" xmlns:x=\"{Xns}\"><Border x:Name=\"B\"/></Grid>";
        string patched = XamlSplicer.SetAttributeByName(raw, "B", "Grid.Column", "2");
        Assert.Contains("Grid.Column=\"2\"", patched);
    }

    [Fact]
    public void Splicer_RejectsDuplicateName()
    {
        string raw =
            $"<Grid xmlns=\"{Pres}\" xmlns:x=\"{Xns}\">" +
            "<Border x:Name=\"Dup\"/><Border x:Name=\"Dup\"/></Grid>";
        Assert.Throws<InvalidOperationException>(
            () => XamlSplicer.SetAttributeByName(raw, "Dup", "Grid.Row", "1"));
    }

    // ───────────── BareValueChecker：拦硬编码色值/字号，放行令牌 ─────────────
    [Fact]
    public void BareValueChecker_FlagsHexAndNumericFontSize()
    {
        IReadOnlyList<string> hits = BareValueChecker.Scan(
            "<Border Background=\"#FFFFFF\"/><TextBlock FontSize=\"14\"/>");
        Assert.Equal(2, hits.Count);
    }

    [Fact]
    public void BareValueChecker_AllowsTokens()
    {
        IReadOnlyList<string> hits = BareValueChecker.Scan(
            "<Border Background=\"{DynamicResource Brush_Accent}\" FontSize=\"{DynamicResource Font_SizeBody}\"/>");
        Assert.Empty(hits);
    }

    // ───────────── GridBands：拖拽落位指针→轨道吸附 ─────────────
    [Fact]
    public void GridBands_ResolveTrack_MapsPointerToBand()
    {
        // 三条轨道：高 100 / 50 / 150（累计边界 100 / 150 / 300）
        double[] sizes = { 100, 50, 150 };
        Assert.Equal(0, GridBands.ResolveTrack(sizes, 0));
        Assert.Equal(0, GridBands.ResolveTrack(sizes, 99));
        Assert.Equal(1, GridBands.ResolveTrack(sizes, 100));
        Assert.Equal(1, GridBands.ResolveTrack(sizes, 149));
        Assert.Equal(2, GridBands.ResolveTrack(sizes, 150));
        Assert.Equal(2, GridBands.ResolveTrack(sizes, 299));
        Assert.Equal(2, GridBands.ResolveTrack(sizes, 9999)); // 越界钳到末轨
        Assert.Equal(0, GridBands.ResolveTrack(System.Array.Empty<double>(), 5)); // 空轨道安全
    }

    // ───────────── DesignTextPrep：剥 x:Class 与事件句柄 ─────────────
    [Fact]
    public void DesignTextPrep_StripsClassAndEvents()
    {
        string raw =
            $"<UserControl x:Class=\"Ns.View\" xmlns=\"{Pres}\" xmlns:x=\"{Xns}\" Loaded=\"OnLoaded\">" +
            "<Button Click=\"OnGo\" Content=\"go\"/></UserControl>";

        string prepped = DesignTextPrep.Prep(raw, out int stripped);

        Assert.DoesNotContain("x:Class", prepped);
        Assert.DoesNotContain("Loaded=", prepped);
        Assert.DoesNotContain("Click=", prepped);
        Assert.Equal(2, stripped);
        Assert.Contains("Content=\"go\"", prepped); // 非事件属性保留
    }

    // ───────────── 真实模块文件 smoke：解析不抛、能采到作者元素 ─────────────
    [Fact]
    public void SourceMap_OnRealGameManagerView_ParsesAndExcludesPartInternals()
    {
        string root = FindRepoRoot();
        string file = Path.Combine(root, "src", "SystemToolkit.Modules.GameManager", "GameManagerView.xaml");
        Assert.True(File.Exists(file), $"找不到真实 View 文件：{file}");

        string text = File.ReadAllText(file);
        List<InputElement> els = SourceMap.Build(text);

        Assert.True(els.Count > 5, "真实 View 应采到足量作者元素");
        Assert.DoesNotContain(els, e => e.Name is not null && e.Name.StartsWith("PART_", StringComparison.Ordinal));
        Assert.DoesNotContain(els, e => e.LocalName is "Setter" or "ControlTemplate");
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("未找到仓库根");
    }
}
