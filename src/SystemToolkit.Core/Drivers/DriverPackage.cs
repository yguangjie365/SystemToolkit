namespace SystemToolkit.Core.Drivers;

/// <summary>
/// Driver Store 驱动包分类（FR-06 四类互斥，优先级：系统关键 &gt; 正在使用 &gt; 旧版本 &gt; 无设备关联）。
/// </summary>
public enum DriverPackageState
{
    /// <summary>尚未分类。</summary>
    Unknown = 0,

    /// <summary>同原始名称组内版本最新（或唯一）。</summary>
    Current = 1,

    /// <summary>同原始名称组内存在更高版本 → 本包为旧版本（清理候选）。</summary>
    OldVersion = 2,

    /// <summary>第三方包且当前无任何设备关联（组内最新）——清理最安全候选。</summary>
    NoDeviceAssociation = 3,

    /// <summary>收件箱驱动（非 oem 前缀，pnputil 拒绝删除）——系统关键，默认排除且不可勾选。</summary>
    SystemCritical = 4,
}

/// <summary>
/// Driver Store 中的一个驱动包。字段对齐 pnputil /enum-drivers /devices /files /format xml
/// 输出（2026-09-05 实测定案 schema，RAPR 同源信息架构）。
/// </summary>
public sealed record DriverPackage
{
    /// <summary>发布名称：oemXX.inf = 第三方注入包；原生 inf = 收件箱驱动。</summary>
    public required string PublishedName { get; init; }

    /// <summary>原始名称（INF 内部名）：同名不同版本会共存于 Store，是"旧版本"判定的分组键。
    /// 注意（实测）：第三方包安装后原始名可能被再次改名（如 oem54.inf → 原名 oem54.inf 链）。</summary>
    public string OriginalName { get; init; } = "";

    /// <summary>类型：驱动 / Driver / 软件组件 / Software component（保留原文）。</summary>
    public string Type { get; init; } = "";

    /// <summary>设备类 GUID（如 {4d36e967-e325-11ce-bfc1-08002be10318} 磁盘驱动），类别翻译查表键。</summary>
    public string ClassName { get; set; } = ""; // 扫描时做 GUID→中文类名翻译，需可写

    /// <summary>设备类 GUID（如 {4d36e967-...}），中文类名翻译与启动关键判定的查表键。</summary>
    public string ClassGuid { get; init; } = "";

    /// <summary>驱动提供方名称（INF Provider，如 Microsoft / NVIDIA）。</summary>
    public string Provider { get; init; } = "";

    /// <summary>驱动日期（XML DriverDate，可能缺失）。</summary>
    public DateTime? Date { get; init; }

    /// <summary>版本原文（驱动版本常见四段式 a.b.c.d，比较见 <see cref="DriverVersion"/>）。</summary>
    public string Version { get; init; } = "";

    /// <summary>签名者（XML SignerName，如 "Microsoft Windows Hardware Compatibility Publisher"）。v2.0 新增。</summary>
    public string SignerName { get; init; } = "";

    /// <summary>扩展 ID（XML ExtensionId，扩展驱动非空）。v2.0 新增。</summary>
    public string ExtensionId { get; init; } = "";

    /// <summary>驱动文件数（XML Files 节点计数，-1=未查询）。v2.0 新增。</summary>
    public int FileCount { get; init; } = -1;

    /// <summary>关联设备名列表（XML Devices/Device/DeviceDescription，pnputil 官方关联；空 = 无设备关联）。</summary>
    public List<string> DeviceNames { get; set; } = new();

    /// <summary>启动关键设备类（存储/启动类，DEVPKEY_DeviceClass_BootCritical）——
    /// 删除可致蓝屏或无法开机，分类时强制归入系统关键并禁止勾选删除（RAPR BootCritical 防线同思路）。</summary>
    public bool IsBootCritical { get; set; }

    /// <summary>oemXX.inf = 运行期注入的第三方包；收件箱驱动为 false。</summary>
    public bool IsThirdParty => PublishedName.StartsWith("oem", StringComparison.OrdinalIgnoreCase);

    /// <summary>分类结果（<see cref="DriverStoreClassifier"/> 填充）。</summary>
    public DriverPackageState State { get; set; } = DriverPackageState.Unknown;
}
