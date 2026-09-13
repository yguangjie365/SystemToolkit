using System.Windows;
using System.Windows.Input;

namespace SystemToolkit.Modules.GameManager;

/// <summary>
/// Steam Web API Key 录入小窗（批次 4，2026-09-13）：无边框 + 主题化，模态于宿主窗口。
/// <para>
/// 形态对齐 <c>MiniPlayerWindow</c>（本仓唯一无边框先例：自绘标题行 + 拖动），
/// Owner/ShowDialog 走既有模态窗范式。
/// </para>
/// <para>
/// 🔴 职责边界：本窗**只负责采集**（非空校验 + 回传文本），保存/清除与随后的重载由 View 调 VM 完成
/// —— VM 不弹窗（审查纪律），窗口也不碰存储、不认识 Key 存储抽象。
/// </para>
/// </summary>
public partial class SteamApiKeyWindow : Window
{
    /// <summary>构造。</summary>
    /// <param name="currentKeyConfigured">
    /// 是否已保存过 Key（决定「清除」是否出现）。**不回显 Key 本身**——窗口拿不到明文。
    /// </param>
    public SteamApiKeyWindow(bool currentKeyConfigured)
    {
        InitializeComponent();
        // 没有可清的东西就不显示「清除」：禁用态在本主题下是文字几乎不可见的白框（实机截图确认），
        // 比隐藏更让人困惑（"这里有个空按钮"）。
        ClearButton.Visibility = currentKeyConfigured ? Visibility.Visible : Visibility.Collapsed;
        Loaded += (_, _) => KeyBox.Focus();
    }

    /// <summary>用户要保存的 Key；点「清除」/「取消」/关闭时为 <c>null</c>。</summary>
    public string? ApiKey { get; private set; }

    /// <summary>用户是否点了「清除」。</summary>
    public bool ClearRequested { get; private set; }

    /// <summary>标题行拖动（无边框窗口必须自绘拖动）。</summary>
    private void OnTitleBarDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 鼠标释放瞬间的竞态（MiniPlayerWindow 同款处置）：拖动失败无需打扰用户
        }
    }

    /// <summary>取消 / ✕ 关闭（Esc 由「取消」按钮的 IsCancel 触发）。</summary>
    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        ClearRequested = true;
        DialogResult = true;
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        string key = KeyBox.Password.Trim();
        if (key.Length == 0)
        {
            // 就地反馈，不弹 MessageBox；并告诉用户"移除"该走哪个键
            ErrorText.Text = "请输入 Key；如需移除已保存的 Key，请点「清除」。";
            ErrorText.Visibility = Visibility.Visible;
            KeyBox.Focus();
            return;
        }

        ApiKey = key;
        DialogResult = true;
    }
}
