using System.Text.RegularExpressions;

namespace SystemToolkit.Tests.Architecture;

/// <summary>
/// 命令可执行性刷新守卫（2026-09-08 建立）。
///
/// <b>起因</b>：真机反馈「选中快照后『校验/恢复』按钮仍灰死」。根因是
/// <c>[ObservableProperty]</c> 生成的 setter <b>不会自动通知 RelayCommand</b>——
/// WPF 按钮只在收到 <c>CanExecuteChanged</c> 事件后才重新求值 CanExecute，
/// 未通知就永远停在旧的灰态。该 bug 与规则侧 Bug2 同根因，此前只修不测，故复发。
///
/// <b>本守卫做什么</b>：扫描所有模块 ViewModel——凡 <c>RelayCommand(CanExecute = nameof(CanXxx))</c>
/// 的判据引用了某个 ObservableProperty，就必须存在对应 <c>OnXxxChanged</c> 钩子
/// （内含 RefreshCanExecute / NotifyCanExecuteChanged）或属性上带
/// <c>[NotifyCanExecuteChangedFor]</c>。缺失即构建失败。
///
/// 姊妹防线：<c>tests/SystemToolkit.Tests/Backup/FileBackupViewModelCommandTests</c>
/// 用事件断言钉死运行期行为；本守卫负责静态兜底、覆盖所有模块。
/// </summary>
public class CommandCanExecuteRefreshGuardTests
{
    private static readonly Regex CanExecuteRefRegex = new(
        @"\[RelayCommand\([^\]]*CanExecute\s*=\s*nameof\((\w+)\)", RegexOptions.Compiled);

    private static readonly Regex ObservablePropRegex = new(
        @"\[ObservableProperty\]\s*(?:private|protected|internal|public)\s+[\w<>?\[\], ]+\s+_(\w+)\s*;",
        RegexOptions.Compiled);

    [Fact]
    public void EveryCanExecuteDependency_MustBeRefreshedOnPropertyChange()
    {
        var violations = new List<string>();
        foreach (string file in ViewModelFiles())
        {
            violations.AddRange(ScanFile(file));
        }

        Assert.Empty(violations);
    }

    /// <summary>单文件扫描（internal 供反向验证直测）。</summary>
    internal static List<string> ScanFile(string path)
    {
        // 先剔除注释：否则被注释掉的钩子（含方法名文本）会被误判为仍然存在
        string text = StripComments(File.ReadAllText(path));
        string fileName = Path.GetFileName(path);
        var violations = new List<string>();

        // 只管「选中态」属性（Selected*）：这类属性由界面绑定直接设置，VM 内部无从感知，
        // 只能靠 OnXxxChanged 钩子刷新命令——正是本次事故的场景。
        // IsBusy 之类由 VM 自己赋值的属性，赋值点已手动调用 RefreshCanExecute，
        // 静态扫描难以准确判定（易误报），故不纳入。
        var observedProps = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in ObservablePropRegex.Matches(text))
        {
            string prop = ToPascal(m.Groups[1].Value);
            if (prop.StartsWith("Selected", StringComparison.Ordinal))
            {
                observedProps.Add(prop);
            }
        }

        foreach (Match cmd in CanExecuteRefRegex.Matches(text))
        {
            string canName = cmd.Groups[1].Value;
            string? body = FindCanPropertyBody(text, canName);
            if (body is null)
            {
                continue; // 判据不是本文件的属性（如继承自基类）——不误报
            }

            foreach (string prop in observedProps)
            {
                if (!Regex.IsMatch(body, $@"\b{prop}\b"))
                {
                    continue;
                }

                if (HasChangeHook(text, prop) || HasNotifyAttribute(text, prop))
                {
                    continue;
                }

                violations.Add(
                    $"{fileName}：命令判据 {canName} 依赖属性 {prop}，但缺少 On{prop}Changed → " +
                    "RefreshCanExecute（或属性声明处加 [NotifyCanExecuteChangedFor]）——" +
                    "否则界面按钮会停在旧的灰态。");
            }
        }

        return violations;
    }

    private static string? FindCanPropertyBody(string text, string canName)
    {
        Match m = Regex.Match(text, $@"\b{canName}\b\s*(?:=>|[{{])");
        if (!m.Success)
        {
            return null;
        }

        int start = m.Index;
        int end = text.IndexOf(';', start);
        return end > start ? text[start..end] : null;
    }

    /// <summary>是否存在 <c>void On{prop}Changed</c> <b>方法声明</b>（注释已在入口剔除），
    /// 且方法体内调用了 RefreshCanExecute / NotifyCanExecuteChanged。</summary>
    private static bool HasChangeHook(string text, string prop)
    {
        Match m = Regex.Match(text, $@"(?:partial\s+)?void\s+On{prop}Changed\s*\(");
        if (!m.Success)
        {
            return false;
        }

        int end = text.IndexOf(';', m.Index);
        if (end < 0 || end - m.Index > 400)
        {
            end = Math.Min(text.Length, m.Index + 400);
        }

        string body = text[m.Index..end];
        return body.Contains("RefreshCanExecute") || body.Contains("NotifyCanExecuteChanged");
    }

    private static string StripComments(string text)
    {
        text = Regex.Replace(text, @"/\*[\s\S]*?\*/", string.Empty);
        return Regex.Replace(text, @"(?<!:)//[^\r\n]*", string.Empty);
    }

    private static bool HasNotifyAttribute(string text, string prop)
        => Regex.IsMatch(text, $@"\[NotifyCanExecuteChangedFor[\s\S]{{0,120}}?\b{prop}\b")
           || Regex.IsMatch(text, $@"\b{prop}\b[\s\S]{{0,120}}?\[NotifyCanExecuteChangedFor");

    private static string ToPascal(string field)
        => string.IsNullOrEmpty(field) ? field : char.ToUpperInvariant(field[0]) + field[1..];

    private static IEnumerable<string> ViewModelFiles()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SystemToolkit.sln")))
        {
            dir = dir.Parent;
        }

        string root = dir?.FullName ?? throw new InvalidOperationException("未定位到仓库根");
        string modules = Path.Combine(root, "src");
        return Directory.EnumerateFiles(modules, "*ViewModel*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
    }
}
