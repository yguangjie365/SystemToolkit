using System;
using System.Collections.Generic;

namespace UiEditor.Core;

/// <summary>
/// 拖拽落位几何：把「指针在父 Grid 内的坐标」映射到「吸附到哪条行/列轨道」。
/// 纯函数，无 WPF 依赖，便于单测。轨道尺寸来自 RowDefinitions[i].ActualHeight / ColumnDefinitions[i].ActualWidth。
/// </summary>
public static class GridBands
{
    /// <summary>给定各轨道尺寸序列与指针在轨道方向上的坐标，返回其落入的轨道下标（越界钳制到 [0, n-1]）。</summary>
    public static int ResolveTrack(IReadOnlyList<double> trackSizes, double pos)
    {
        if (trackSizes is null || trackSizes.Count == 0)
        {
            return 0;
        }

        double acc = 0;
        for (int i = 0; i < trackSizes.Count; i++)
        {
            double next = acc + trackSizes[i];
            if (pos < next)
            {
                return i;
            }

            acc = next;
        }

        return trackSizes.Count - 1; // 落在最后一条轨道之外（含末尾边界）→ 钳到末轨
    }
}
