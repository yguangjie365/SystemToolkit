using System.Windows;
using System.Windows.Controls;
using SystemToolkit.Core.Utilities;

namespace SystemToolkit.Modules.MusicManager;

/// <summary>
/// 音乐管理视图（MUSIC-6）：三 Tab（曲库/播放/歌词）。
/// Loaded 注入文件夹选择回调并初始化曲库（幂等）。
/// </summary>
public partial class MusicManagerView : UserControl
{
    private readonly MusicManagerViewModel _vm;

    public MusicManagerView(MusicManagerViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 文件夹选择回调注入（对照 FileBackup.RestoreRequest 注入模式——VM 不持有窗口引用）
        _vm.PickFolder ??= () =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "选择音乐扫描目录",
                Multiselect = false,
            };
            return dialog.ShowDialog(Window.GetWindow(this)) == true ? dialog.FolderName : null;
        };

        _ = InitializeOnceAsync();
    }

    private bool _initialized;

    private async Task InitializeOnceAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        try
        {
            await _vm.InitializeAsync();
        }
        catch (Exception ex)
        {
            // 🔴 初始化失败显式可见（曲库文件 IO 异常等），不让 Dispatcher 吞掉
            _vm.ReportInitError($"曲库初始化失败：{ex.Message}");
        }
    }
}
