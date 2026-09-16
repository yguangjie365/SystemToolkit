using System.Windows;
using System.Windows.Threading;
using SystemToolkit.Core.FileTransfer.Models;
using SystemToolkit.Core.FileTransfer.Services;
using SystemToolkit.Core.Utilities;
using SystemToolkit.UI.Common;

namespace SystemToolkit.Modules.FileTransfer;

/// <summary>
/// 接收确认对话框（2026-09-13 批次 P1 ⑦）。
/// <para>
/// 输入一次 <see cref="TransferRequestEventArgs"/>（服务端已把预算结果、同名状态与将执行的动作
/// 都放在里面），输出一个 <see cref="TransferDecision"/>（接不接 + 本次的同名处理方式）。
/// </para>
/// <para>
/// 🔴 默认值只能是「拒绝」：直接关窗、Esc、进程退出都必须收场为拒绝——
/// 网络文件的默认放行会让"没人看屏幕"变成"随便谁都能塞文件进来"。
/// </para>
/// </summary>
public partial class ReceiveConfirmWindow : Window
{
    private readonly TransferRequestEventArgs _request;

    /// <summary>到点自动收场用的计时器（🟠 审查 v8-🟠-2）。<c>timeoutSeconds &lt;= 0</c> 时为 <c>null</c>。</summary>
    private readonly DispatcherTimer? _timeoutTimer;

    /// <summary>剩余秒数（倒计时展示用）。</summary>
    private int _remainingSeconds;

    public ReceiveConfirmWindow(TransferRequestEventArgs request, int timeoutSeconds)
    {
        _request = request;
        InitializeComponent();
        // 标题栏跟随主题明暗（共享接线器：句柄就绪套一次 + 主题切换跟随 + 关闭退订）
        TitleBarThemeWiring.Attach(this);
        Render(request, timeoutSeconds);

        // 🟠 审查 v8-🟠-2：文案一直承诺「约 N 秒后自动拒绝」，但窗口此前**没有任何计时器或
        // 自动关闭路径**，而 ShowDialog() 也不会因服务端超时而返回 —— 于是对端早已按超时判定
        // "拒绝"，用户却仍能点「接收并复制」，本机照样写剪贴板：**交付与回执脱钩**。
        // 这里把承诺兑现：到点自动按「拒绝」收场（Decision 默认值即 Reject，关窗即回执）。
        if (timeoutSeconds > 0)
        {
            _remainingSeconds = timeoutSeconds;
            _timeoutTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
            {
                Interval = TimeSpan.FromSeconds(1),
            };
            _timeoutTimer.Tick += OnTimeoutTick;
            _timeoutTimer.Start();
        }

        // 手动关窗（按钮 / Esc / 系统菜单）后计时器必须停 —— 与 🟡-6 同款纪律：不留长驻 1s 循环
        Closed += (_, _) => _timeoutTimer?.Stop();
    }

    /// <summary>
    /// 每秒倒计时；归零即按「拒绝」收场并关窗 —— 兑现 <see cref="TimeoutHint"/> 上的承诺。
    /// <para>
    /// <see cref="Decision"/> 保持默认的 <see cref="TransferDecision.Reject"/>：关窗即回执，
    /// 与服务端（以及对端）的超时判定一致，不再出现"对端认为失败、本机其实写成功了"。
    /// </para>
    /// </summary>
    private void OnTimeoutTick(object? sender, EventArgs e)
    {
        _remainingSeconds--;
        if (_remainingSeconds > 0)
        {
            TimeoutHint.Text = $"若不响应，约 {_remainingSeconds} 秒后自动拒绝（本窗口会关闭）";
            return;
        }

        _timeoutTimer?.Stop();
        TimeoutHint.Text = "已超时：本次接收按「拒绝」处理";
        Close();
    }

    /// <summary>
    /// 用户的决定（未点击任何按钮时为拒绝）。
    /// <para>
    /// ⚠️ 刻意**不叫**「Result」：架构守卫 AsyncGuard 按文本扫「点 + Result」判定 sync-over-async，
    /// 同步属性也会被它误报（守卫宁可多报，也不放过真正的同步等待）。改名比往豁免名单里塞一条更省事，
    /// 也不削弱守卫。
    /// </para>
    /// </summary>
    public TransferDecision Decision { get; private set; } = TransferDecision.Reject;

    private void Render(TransferRequestEventArgs request, int timeoutSeconds)
    {
        // 🟠 审查 v8-🟠-2：文案必须与代码能力一致 —— 计时器存在时才承诺"自动拒绝并关窗"。
        TimeoutHint.Text = timeoutSeconds > 0
            ? $"若不响应，约 {timeoutSeconds} 秒后自动拒绝（本窗口会关闭）"
            : "若不响应，对方会按超时取消";

        PeerText.Text = string.IsNullOrEmpty(request.PeerDeviceName)
            ? request.PeerEndpoint
            : $"{request.PeerEndpoint} · {request.PeerDeviceName}";

        if (request.Kind == TransferKind.Text)
        {
            RenderText(request);
            return;
        }

        RenderFile(request);
    }

    /// <summary>
    /// 文本模式：只显示**全文**与字数。
    /// <para>
    /// 🔴 全文，不做 120 字预览截断：用户要判断"接不接"必须看到全貌（方案 §5.2），
    /// 只给开头等于让人在信息不全的情况下点头。
    /// </para>
    /// <para>
    /// 本窗口**只负责问，不负责交付**：写剪贴板由 VM 在拿到"用户同意"之后做
    /// （见 <see cref="ReceiveTextDecision"/>）——这样"回执"与"实际交付"是同一个事实。
    /// </para>
    /// </summary>
    private void RenderText(TransferRequestEventArgs request)
    {
        Title = "收到一条文本";
        HeaderText.Text = "收到一条文本";

        // 文件专属信息行整块收起（Grid 行高为 Auto，全部收起即不占高度）
        FileLabel.Visibility = Visibility.Collapsed;
        FileText.Visibility = Visibility.Collapsed;
        DirLabel.Visibility = Visibility.Collapsed;
        DirText.Visibility = Visibility.Collapsed;
        DiskLabel.Visibility = Visibility.Collapsed;
        DiskText.Visibility = Visibility.Collapsed;
        ConflictSection.Visibility = Visibility.Collapsed;

        string text = request.Text ?? string.Empty;
        TextSection.Visibility = Visibility.Visible;
        TextBody.Text = text;
        TextMeta.Text = $"共 {request.TextLength} 字（UTF-8 {TransferText.GetByteCount(text)} 字节）";

        AcceptButton.Content = "接收并复制";
        HintText.Text = "接收后文本会写入你的剪贴板（会覆盖当前剪贴板内容）。";
    }

    /// <summary>文件模式：既有行为，逐字保持（含同名冲突区与提示文案）。</summary>
    private void RenderFile(TransferRequestEventArgs request)
    {
        FileText.Text = $"{request.FileName} · {FormatUtil.FormatSize(request.FileSize)}";

        DirText.Text = string.IsNullOrEmpty(request.ReceiveDirectory) ? "—" : request.ReceiveDirectory;

        DiskText.Text = request.AvailableFreeBytes is { } free
            ? $"{FormatUtil.FormatSize(free)} 可用"
                + (request.DiskSpace == DiskSpaceCheck.Enough ? " · 空间充足" : string.Empty)
            : "无法判定剩余空间（网络路径或卷未就绪）";

        ConflictSection.Visibility = request.TargetExists ? Visibility.Visible : Visibility.Collapsed;
        if (!request.TargetExists)
        {
            HintText.Text = "接收后文件将保存到上面列出的目录。";
            return;
        }

        if (request.ConflictPolicy == TransferConflictPolicy.Ask)
        {
            // 策略=询问：让用户逐次挑处理方式。默认「改名」——它是唯一不会丢东西的选项。
            ConflictAskPanel.Visibility = Visibility.Visible;
            ConflictAutoText.Visibility = Visibility.Collapsed;
            ChoiceRename.IsChecked = true;
            HintText.Text = "请选择同名处理方式后点「接收」；本次选择不会改变默认策略。";
            return;
        }

        ConflictAskPanel.Visibility = Visibility.Collapsed;
        ConflictAutoText.Visibility = Visibility.Visible;
        ConflictAutoText.Text = request.ConflictAction switch
        {
            ConflictResolution.Overwrite => "按当前策略：将覆盖原文件（原内容不可恢复）",
            ConflictResolution.Skip => "按当前策略：将跳过不接收（不留新文件）",
            _ => "按当前策略：将改名保存（原文件保持不变）",
        };
        HintText.Text = "这是当前策略下将会发生的动作。";
    }

    private void OnAcceptClick(object sender, RoutedEventArgs e)
    {
        Decision = TransferDecision.AcceptWith(SelectedConflictPolicy());
        DialogResult = true;
    }

    private void OnRejectClick(object sender, RoutedEventArgs e)
    {
        Decision = TransferDecision.Reject;
        DialogResult = false;
    }

    /// <summary>
    /// 本次生效的同名处理方式。仅策略=询问时由用户选择（服务端也只在这种情况下采用它）；
    /// 其余策略下原样回传设置值，服务端按已解析的策略执行。
    /// </summary>
    private TransferConflictPolicy SelectedConflictPolicy()
    {
        if (_request.ConflictPolicy != TransferConflictPolicy.Ask)
        {
            return _request.ConflictPolicy;
        }

        if (ChoiceOverwrite.IsChecked == true)
        {
            return TransferConflictPolicy.Overwrite;
        }
        if (ChoiceSkip.IsChecked == true)
        {
            return TransferConflictPolicy.Skip;
        }
        return TransferConflictPolicy.Rename;
    }
}
