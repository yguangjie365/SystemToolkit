namespace SystemToolkit.Core.Utilities;

/// <summary>展示格式化工具：字节大小等机器数值转人类可读文本。</summary>
public static class FormatUtil
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    /// <summary>格式化字节数为人类可读文本（如 1,536 → "1.5 KB"）。小于 1 KB 时省略小数点。
    /// 审查 L2：与 OverviewFormat.Bytes 收敛为单一实现（单位上限 PB、一位小数），
    /// OverviewFormat.Bytes 是本方法的 ulong 包装。</summary>
    public static string FormatSize(long bytes)
    {
        double scaled = bytes;
        int unitIndex = 0;
        while (scaled >= 1024.0 && unitIndex < Units.Length - 1)
        {
            scaled /= 1024.0;
            unitIndex++;
        }

        return unitIndex == 0 ? $"{bytes} B" : $"{scaled:0.#} {Units[unitIndex]}";
    }
}
