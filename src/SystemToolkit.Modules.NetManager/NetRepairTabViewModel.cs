using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SystemToolkit.Core.Network.Models;
using SystemToolkit.Core.Network.Services;

namespace SystemToolkit.Modules.NetManager;

/// <summary>修复项卡片 VM（权限 / 重启徽章由描述符驱动，执行前强确认）。</summary>
public sealed class RepairStepVm
{
    public RepairStepDescriptor Descriptor { get; }

    public RepairStepVm(RepairStepDescriptor descriptor) => Descriptor = descriptor;

    public string Id => Descriptor.Id;

    public string Title => Descriptor.Title;

    public string Description => Descriptor.Description;

    public string CommandPreview => Descriptor.CommandPreview;

    public bool RequiresAdmin => Descriptor.RequiresAdmin;

    public bool RequiresReboot => Descriptor.RequiresReboot;
}

/// <summary>
/// 「网络修复」Tab：一键安全修复（flushdns → renew，失败即停）+ 六项修复卡。
/// 特权项经 ElevatingCommandRunner 按需 UAC；重置类（winsock/ipreset）标注需重启并强确认。
/// </summary>
public partial class NetRepairTabViewModel : ObservableObject
{
    private readonly INetRepairService _repair;
    private readonly INetDiagnosticService _diagnostic;
    private readonly Action<string> _log;

    public NetRepairTabViewModel(INetRepairService repair, INetDiagnosticService diagnostic, Action<string> log)
    {
        _repair = repair;
        _diagnostic = diagnostic;
        _log = log;
        foreach (RepairStepDescriptor descriptor in repair.Steps)
        {
            Steps.Add(new RepairStepVm(descriptor));
        }
    }

    /// <summary>确认对话框回调（由组合根转接）。</summary>
    public Func<string, string, bool>? ConfirmRequest { get; set; }

    /// <summary>一键安全修复成功后触发（组合根接自动复诊断联动）。</summary>
    public event Action? SafeSequenceCompleted;

    public ObservableCollection<RepairStepVm> Steps { get; } = new();

    [ObservableProperty]
    private bool _isBusy;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunSafeSequenceAsync()
    {
        if (!Confirm("一键安全修复",
                "将依次执行：刷新 DNS 缓存 → 重新获取 IP（自动选用首选已连接物理网卡）。\n"
                + "两项均无需管理员权限、无需重启；任一步失败立即停止。\n\n"
                + "⚠️ 重新获取 IP 期间网络会短暂中断。确定继续吗？"))
        {
            return;
        }

        IsBusy = true;
        try
        {
            IReadOnlyList<string> executed = await _repair.RunSafeSequenceAsync(_log).ConfigureAwait(true);
            _log(executed.Count > 0
                ? "[修复] ✅ 安全修复完成：" + string.Join(" → ", executed)
                : "[修复] ❌ 安全修复未完成（首步即失败），请查看日志");
            if (executed.Count > 0)
            {
                SafeSequenceCompleted?.Invoke();
            }
        }
        catch (Exception ex)
        {
            // 审查 🟠-2（2026-09-10）：写命令必须用户可见（AsyncRelayCommand 会吞）
            _log("[修复] ❌ 安全修复异常：" + ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunStepAsync(RepairStepVm? vm)
    {
        if (vm is null)
        {
            return;
        }

        StringBuilder warning = new StringBuilder(vm.Descriptor.Title).Append("：").AppendLine(vm.Descriptor.Description);
        if (vm.RequiresAdmin)
        {
            warning.AppendLine("• 需要管理员权限（执行时将弹出 UAC 确认）");
        }

        if (vm.RequiresReboot)
        {
            warning.AppendLine("• ⚠️ 完成后需要重启计算机才能生效，且会清除相关的自定义网络设置");
        }

        warning.AppendLine().Append("确定执行吗？");

        if (!Confirm("执行修复", warning.ToString()))
        {
            _log($"[修复] 已取消：{vm.Title}");
            return;
        }

        IsBusy = true;
        try
        {
            int exit = await _repair.ExecuteAsync(vm.Id, _log).ConfigureAwait(true);
            _log(exit == 0
                ? $"[修复] ✅ {vm.Title} 完成"
                : exit == 1223
                    ? $"[修复] ⚠️ {vm.Title}：用户拒绝了 UAC 提权，操作安全终止（无副作用）"
                    : $"[修复] ❌ {vm.Title} 失败（退出码 {exit}）");
        }
        catch (Exception ex)
        {
            // 审查 🟠-2（2026-09-10）：进程被杀/网络断流等非预期异常必须用户可见（AsyncRelayCommand 会吞）
            _log($"[修复] ❌ {vm.Title} 异常：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanRun => !IsBusy;

    private bool Confirm(string title, string message)
        => ConfirmRequest?.Invoke(title, message) == true; // 审查 Y1：危险操作确认缺省应拒绝（fail-closed）
}
