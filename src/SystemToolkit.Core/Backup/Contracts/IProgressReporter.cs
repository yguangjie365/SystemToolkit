namespace SystemToolkit.Core.Backup.Contracts;

/// <summary>备份/恢复过程的进度与日志上报契约（UI 侧实现负责封送回 UI 线程）。</summary>
public interface IProgressReporter
{
    /// <summary>上报整体进度。</summary>
    /// <param name="done">已完成的条目数。</param>
    /// <param name="total">总条目数。</param>
    /// <param name="phase">当前阶段说明文本（如「备份中」）。</param>
    void OnProgress(int done, int total, string phase);

    /// <summary>上报阶段切换文本（如「正在扫描源文件…」）。</summary>
    void OnPhase(string text);

    /// <summary>追加一行过程日志。</summary>
    void OnLog(string message);
}
