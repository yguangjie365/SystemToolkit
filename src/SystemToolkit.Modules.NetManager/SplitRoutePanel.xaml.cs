using System.Windows.Controls;

namespace SystemToolkit.Modules.NetManager;

/// <summary>
/// 分流路由 Tab 面板（NET-7）：DataContext 由父视图注入 <c>{Binding Split}</c>。
/// 纯展示——全部编排在 <see cref="SplitRouteTabViewModel"/> / Core 侧服务，无 code-behind 逻辑。
/// </summary>
public partial class SplitRoutePanel : UserControl
{
    public SplitRoutePanel()
    {
        InitializeComponent();
    }
}
