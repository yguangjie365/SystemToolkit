using Net.Codecrete.QrCodeGenerator;

namespace SystemToolkit.Core.Utilities;

/// <summary>
/// 二维码矩阵生成（纯计算，零系统交互）：文件互传「手机扫码访问」用。
/// 消费者只负责把矩阵渲染成各自 UI 的形态（WPF → PNG 位图，Blazor → SVG），
/// 编码逻辑统一收口在这里，避免两套 UI 各写一份 QR 参数。
/// </summary>
public static class QrMatrix
{
    /// <summary>
    /// 生成二维码模块矩阵（Medium 纠错，同 WPF 侧既有口径）。
    /// 返回 <c>rows[y][x]</c>，true = 黑模块；边长 = rows.Length（含静默区需渲染方自理）。
    /// </summary>
    public static bool[][] Create(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("二维码内容不能为空。", nameof(text));
        }

        var qr = QrCode.EncodeText(text, QrCode.Ecc.Medium);
        int size = qr.Size;
        bool[][] rows = new bool[size][];
        for (int y = 0; y < size; y++)
        {
            bool[] row = new bool[size];
            for (int x = 0; x < size; x++)
            {
                row[x] = qr.GetModule(x, y);
            }
            rows[y] = row;
        }
        return rows;
    }
}
