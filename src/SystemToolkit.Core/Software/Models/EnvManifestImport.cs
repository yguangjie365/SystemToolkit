namespace SystemToolkit.Core.Software.Models;

/// <summary>清单文件的格式。</summary>
public enum EnvManifestFormat
{
    /// <summary>winget 官方导出清单（<c>winget export -o</c> 的 schema 2.0 文件）。</summary>
    WingetExport,

    /// <summary>本工具自研的整体清单（<see cref="EnvCatalog"/>，旧格式；现已降级为**只读导入**）。</summary>
    LegacyCatalog,
}

/// <summary>
/// 清单导入结果。区分「成功/失败」与「命中的格式」——两者决定调用方的落盘动作：
/// winget 格式**只有包 Id**（无手工/驱动清单、无名称分类图标），故导入时**不得**清空这两类清单。
/// </summary>
public sealed class EnvManifestImport
{
    /// <summary>是否导入成功（失败时 <see cref="Errors"/> 非空，原因已记日志）。</summary>
    public bool Success { get; private init; }

    /// <summary>命中的文件格式。</summary>
    public EnvManifestFormat Format { get; private init; }

    /// <summary>导入得到的清单（失败时为空清单）。</summary>
    public EnvCatalog Catalog { get; private init; } = new EnvCatalog();

    /// <summary>失败原因（成功时为空）。</summary>
    public List<string> Errors { get; private init; } = new List<string>();

    /// <summary>构造成功结果。</summary>
    public static EnvManifestImport Ok(EnvManifestFormat format, EnvCatalog catalog) =>
        new EnvManifestImport { Success = true, Format = format, Catalog = catalog };

    /// <summary>构造失败结果。</summary>
    public static EnvManifestImport Failed(List<string> errors) =>
        new EnvManifestImport { Success = false, Errors = errors };
}
