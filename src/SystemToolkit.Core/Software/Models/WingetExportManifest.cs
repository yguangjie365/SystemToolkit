using System.Text.Json;
using System.Text.Json.Serialization;

namespace SystemToolkit.Core.Software.Models;

/// <summary>
/// winget 官方导出清单（<c>winget export -o</c> / <c>winget import -i</c> 使用的 schema 2.0 文件）的读写模型。
/// 用途：让本工具的软件清单与 winget CLI 互通（他机迁移、命令行导入）。
/// <para>
/// 🔴 **有损性（2026-09-13 本机 winget v1.29.290 实测）**：<c>winget export</c> 只输出「能从某个源获得」的包，
/// 不在任何源中的已安装软件会被丢弃（本机实测就有 3 条：AntiCheatExpert、火绒安全软件、Microsoft 365）。
/// 因此本格式**不能**表达本工具自研清单的全貌——手工软件、驱动工具、分类/图标等均无对应字段，
/// 导入导出路径必须如实说明范围，不得让用户以为"导出即全量备份"。
/// </para>
/// <para>
/// 🔴 **schema 硬约束（同日用 <c>winget import</c> 实测逼出，非按文档推断）**：
/// ① <c>WinGetVersion</c> 若出现则必须匹配 pattern —— **空字符串非法**（省略则通过）→ 本模型未知时**整体省略**；
/// ② <c>SourceDetails.Identifier</c> **必填**（缺则 "Missing required property 'Identifier'"）；
/// ③ <c>Packages</c> **至少 1 条**（空数组即 schema 失败）→ 构造时不允许产生空源；
/// ④ <c>Sources: []</c> 是合法的（winget 返回 NO_APPLICATIONS_FOUND，属"没包可装"而非格式错）。
/// </para>
/// </summary>
public sealed class WingetExportManifest
{
    /// <summary>winget 官方 schema 地址（2.0）。</summary>
    public const string SchemaUrl = "https://aka.ms/winget-packages.schema.2.0.json";

    /// <summary>"$schema" 字段。</summary>
    [JsonPropertyName("$schema")]
    public string Schema { get; set; } = SchemaUrl;

    /// <summary>生成时间（winget 写 ISO 8601，如 2026-09-13T15:44:31.263-00:00）。</summary>
    [JsonPropertyName("CreationDate")]
    public string CreationDate { get; set; } = "";

    /// <summary>按源分组的包列表。</summary>
    [JsonPropertyName("Sources")]
    public List<WingetExportSource> Sources { get; set; } = new List<WingetExportSource>();

    /// <summary>
    /// 写出该文件的 winget 版本。🔴 未知时**必须为 null**（省略字段）——实测空字符串会被 schema 拒绝；
    /// 本工具不写此字段，由 winget 自己维护。
    /// </summary>
    [JsonPropertyName("WinGetVersion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WinGetVersion { get; set; }

    /// <summary>清单内包条目总数（跨源求和）。</summary>
    public int PackageCount()
    {
        int total = 0;
        foreach (WingetExportSource source in Sources)
        {
            total += source.Packages.Count;
        }

        return total;
    }

    /// <summary>
    /// 判断一段文本是否"看起来像" winget 导出清单：必须是 JSON 对象且含数组型 <c>Sources</c>。
    /// 供导入路径做格式判别（旧自研 <see cref="EnvCatalog"/> 格式没有 <c>Sources</c>）。
    /// </summary>
    public static bool LooksLikeWingetManifest(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            // 先验 ValueKind：TryGetProperty 对「类型不符」会抛异常（本仓已踩过的坑）
            return doc.RootElement.TryGetProperty("Sources", out JsonElement sources)
                && sources.ValueKind == JsonValueKind.Array;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// 解析 winget 导出清单。任何结构性问题即**整体拒绝**并回传错误清单
    /// （对齐 <see cref="EnvCatalogValidator"/> 的导入纪律：宁可整体拒绝，也不静默剔除条目）。
    /// </summary>
    public static bool TryParse(string json, out WingetExportManifest? manifest, out List<string> errors)
    {
        manifest = null;
        errors = new List<string>();

        WingetExportManifest? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<WingetExportManifest>(json);
        }
        catch (JsonException ex)
        {
            errors.Add("JSON 解析失败：" + ex.Message);
            return false;
        }

        if (parsed is null)
        {
            errors.Add("文件内容为空或不是有效的清单 JSON");
            return false;
        }

        // 反序列化把显式 null 写进非空集合属性 —— 与 EnvCatalogValidator 同款 null 容忍
        parsed.Sources ??= new List<WingetExportSource>();
        if (parsed.Sources.Count == 0)
        {
            errors.Add("清单缺少 Sources（不是 winget 导出格式？）");
            return false;
        }

        int index = 0;
        foreach (WingetExportSource source in parsed.Sources)
        {
            if (source is null)
            {
                errors.Add($"Sources[{index}] 为 null");
                index++;
                continue;
            }

            source.Packages ??= new List<WingetExportPackageRef>();
            source.SourceDetails ??= new WingetExportSourceDetails();

            int pkgIndex = 0;
            foreach (WingetExportPackageRef pkg in source.Packages)
            {
                if (pkg is null || string.IsNullOrWhiteSpace(pkg.PackageIdentifier))
                {
                    errors.Add($"Sources[{index}].Packages[{pkgIndex}] 缺少 PackageIdentifier");
                }

                pkgIndex++;
            }

            index++;
        }

        if (errors.Count > 0)
        {
            return false;
        }

        manifest = parsed;
        return true;
    }

    /// <summary>
    /// 由本工具的 winget 清单构造导出模型。**按源首次出现顺序**分组、组内保持输入顺序
    /// ——刻意不做字母重排（OUI 生成器踩过：排序会替上游做取舍，属不该有的语义改动）。
    /// 构造保证不产生空 <c>Packages</c>（schema 要求至少 1 条）。
    /// </summary>
    /// <param name="packages">清单条目。</param>
    /// <param name="created">生成时间；缺省取当前 UTC。</param>
    public static WingetExportManifest FromPackages(IEnumerable<WingetPackage> packages, DateTimeOffset? created = null)
    {
        var manifest = new WingetExportManifest
        {
            CreationDate = (created ?? DateTimeOffset.UtcNow).ToString("yyyy-MM-ddTHH:mm:ss.fffK"),
        };

        // 顺序保留的分组（不用 Dictionary —— 它的枚举顺序语义不足以承担"分组即有序列"的承诺）
        var ordered = new List<(string Name, WingetExportSource Source)>();
        foreach (WingetPackage pkg in packages)
        {
            if (pkg is null || string.IsNullOrWhiteSpace(pkg.Id))
            {
                continue;
            }

            string name = string.IsNullOrWhiteSpace(pkg.Source)
                ? WingetExportSourceDetails.WingetSourceName
                : pkg.Source.Trim();

            WingetExportSource? bucket = null;
            foreach ((string Name, WingetExportSource Source) entry in ordered)
            {
                if (string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    bucket = entry.Source;
                    break;
                }
            }

            if (bucket is null)
            {
                bucket = new WingetExportSource
                {
                    SourceDetails = WingetExportSourceDetails.ForKnownSource(name),
                };
                ordered.Add((name, bucket));
            }

            bucket.Packages.Add(new WingetExportPackageRef { PackageIdentifier = pkg.Id.Trim() });
        }

        foreach ((string _, WingetExportSource source) in ordered)
        {
            manifest.Sources.Add(source);
        }

        return manifest;
    }

    /// <summary>
    /// 修剪为 schema 可接受的形态：丢弃空源（<c>Packages</c> 至少 1 条）、补齐缺失的源信息
    /// （<c>Identifier</c> 必填；<c>Name</c> 缺失时无法判断源类型，按 winget 源补默认值）。
    /// 解析外部文件后再写回、或手工构造后写盘，都必须先过这一步。
    /// </summary>
    public void PruneForWrite()
    {
        Sources ??= new List<WingetExportSource>();

        var kept = new List<WingetExportSource>();
        foreach (WingetExportSource source in Sources)
        {
            if (source?.Packages is null || source.Packages.Count == 0)
            {
                continue;
            }

            source.SourceDetails ??= new WingetExportSourceDetails();
            var known = WingetExportSourceDetails.ForKnownSource(source.SourceDetails.Name);
            if (string.IsNullOrWhiteSpace(source.SourceDetails.Name))
            {
                source.SourceDetails.Name = known.Name;
            }

            if (string.IsNullOrWhiteSpace(source.SourceDetails.Identifier))
            {
                source.SourceDetails.Identifier = known.Identifier;
            }

            if (string.IsNullOrWhiteSpace(source.SourceDetails.Type))
            {
                source.SourceDetails.Type = known.Type;
            }

            if (string.IsNullOrWhiteSpace(source.SourceDetails.Argument))
            {
                source.SourceDetails.Argument = known.Argument;
            }

            kept.Add(source);
        }

        Sources = kept;
    }

    /// <summary>
    /// 转为本工具的清单条目（去重，按「源 + Id」）。清单没有名称/分类/图标字段 →
    /// 名称回落为 Id（不静默留空，否则 UI 显示空白行），分类回落默认值。
    /// </summary>
    public List<WingetPackage> ToPackages()
    {
        var result = new List<WingetPackage>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (WingetExportSource source in Sources)
        {
            string sourceName = string.IsNullOrWhiteSpace(source.SourceDetails?.Name)
                ? WingetExportSourceDetails.WingetSourceName
                : source.SourceDetails!.Name!.Trim();

            foreach (WingetExportPackageRef pkg in source.Packages)
            {
                string id = pkg.PackageIdentifier?.Trim() ?? "";
                if (id.Length == 0 || !seen.Add(sourceName + "\u0000" + id))
                {
                    continue;
                }

                result.Add(new WingetPackage
                {
                    Id = id,
                    Name = id,
                    Source = sourceName,
                });
            }
        }

        return result;
    }
}

/// <summary>winget 导出清单中的一个源（SourceDetails + 该源下的包）。</summary>
public sealed class WingetExportSource
{
    /// <summary>源信息（schema 必填）。</summary>
    [JsonPropertyName("SourceDetails")]
    public WingetExportSourceDetails SourceDetails { get; set; } = new WingetExportSourceDetails();

    /// <summary>该源下的包引用（schema 要求至少 1 条）。</summary>
    [JsonPropertyName("Packages")]
    public List<WingetExportPackageRef> Packages { get; set; } = new List<WingetExportPackageRef>();
}

/// <summary>winget 导出清单的源信息（字段名取自实测产物）。</summary>
public sealed class WingetExportSourceDetails
{
    /// <summary>winget 源名（默认源）。</summary>
    public const string WingetSourceName = "winget";

    /// <summary>Microsoft Store 源名。</summary>
    public const string MsStoreSourceName = "msstore";

    /// <summary>源地址。</summary>
    [JsonPropertyName("Argument")]
    public string Argument { get; set; } = "";

    /// <summary>
    /// 源标识符。🔴 **schema 必填**（缺则 <c>winget import</c> 直接报 "Missing required property 'Identifier'"）。
    /// 取值来自本机实测：winget 源 <c>Microsoft.Winget.Source_8wekyb3d8bbwe</c>、
    /// msstore 源 <c>StoreEdgeFD</c>（本机 winget 源库目录名，见
    /// <c>%LOCALAPPDATA%\Packages\Microsoft.DesktopAppInstaller_8wekyb3d8bbwe\LocalState\</c>）。
    /// </summary>
    [JsonPropertyName("Identifier")]
    public string Identifier { get; set; } = "";

    /// <summary>源名（<c>winget</c> / <c>msstore</c>）。</summary>
    [JsonPropertyName("Name")]
    public string Name { get; set; } = "";

    /// <summary>源类型。</summary>
    [JsonPropertyName("Type")]
    public string Type { get; set; } = "";

    /// <summary>
    /// 按源名给出已知源信息。🔴 实测发现 <c>winget export</c> 写出的是**本机实际源地址**
    /// （本机为镜像 <c>https://mirrors.ustc.edu.cn/winget-source</c>）；本工具不知道用户换成了什么源，
    /// 故 <c>Argument</c> 只写官方默认值，并在导出提示里说明——
    /// 需要准确地址请用「导出本机已安装」（走 winget 自己导出）。
    /// </summary>
    public static WingetExportSourceDetails ForKnownSource(string? name)
    {
        if (string.Equals(name, MsStoreSourceName, StringComparison.OrdinalIgnoreCase))
        {
            return new WingetExportSourceDetails
            {
                Name = MsStoreSourceName,
                Argument = "https://storeedgefd.dsx.mp.microsoft.com/v9.0",
                Identifier = "StoreEdgeFD",
                Type = "Microsoft.Rest",
            };
        }

        return new WingetExportSourceDetails
        {
            Name = WingetSourceName,
            Argument = "https://cdn.winget.microsoft.com/cache",
            Identifier = "Microsoft.Winget.Source_8wekyb3d8bbwe",
            Type = "Microsoft.PreIndexed.Package",
        };
    }
}

/// <summary>winget 导出清单中的一个包引用（本工具不使用版本字段：清单不固定版本）。</summary>
public sealed class WingetExportPackageRef
{
    /// <summary>包 Id（winget 包名或 msstore 的 9 位 id）。</summary>
    [JsonPropertyName("PackageIdentifier")]
    public string PackageIdentifier { get; set; } = "";
}
