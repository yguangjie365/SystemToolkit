using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace SystemToolkit.Core.Drivers;

public sealed partial class PnpUtilService
{
    // ------------------------------------------------------------------
    // 输出解析（internal 供守卫测试直测）。
    // 策略对齐 DriverStoreExplorer：不依赖本地化标签文本——
    // ①分隔符兼容 ASCII ':' 与全角 '：'；②字段按多语言关键词归一；
    // ③8 字段块按位置序兜底（pnputil 输出顺序跨语言稳定：发布名/原始名/类型/类别/GUID/提供程序/日期/版本）。
    // ------------------------------------------------------------------
    internal static List<DriverPackage> ParseEnumOutput(string output, IFormatProvider? culture = null)
    {
        var packages = new List<DriverPackage>();
        List<(string Key, string Value)>? current = null;

        foreach (string rawLine in output.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0)
            {
                continue;
            }

            int sep = IndexOfSeparator(line);
            if (sep <= 0)
            {
                continue;
            }

            string key = line[..sep].Trim();
            string value = line[(sep + 1)..].Trim();

            if (IsPublishedName(key, value))
            {
                if (current is not null)
                {
                    packages.Add(ToPackage(current, culture));
                }

                current = new List<(string Key, string Value)> { ("published", value) };
                continue;
            }

            if (current is null)
            {
                continue;
            }

            current.Add((FieldKey(key), value));
        }

        if (current is not null)
        {
            packages.Add(ToPackage(current, culture));
        }

        return packages;
    }

    /// <summary>键值分隔符：ASCII ':' 与全角 '：' 取先出现者。</summary>
    private static int IndexOfSeparator(string line)
    {
        int ascii = line.IndexOf(':');
        int fullwidth = line.IndexOf('\uFF1A');
        if (ascii < 0)
        {
            return fullwidth;
        }

        if (fullwidth < 0)
        {
            return ascii;
        }

        return Math.Min(ascii, fullwidth);
    }

    /// <summary>发布名行判定：值以 .inf 结尾且键含发布名特征（中/英），或值直接是 oemXX.inf。</summary>
    private static bool IsPublishedName(string key, string value)
    {
        if (!value.EndsWith(".inf", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return key.Contains("发布", StringComparison.OrdinalIgnoreCase)
            || key.Contains("published", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("oem", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>字段名归一。注意顺序：GUID 必须在"类别/类名"之前判（"类 GUID" 同含"类"字）。
    /// Win11 新版标签：「类名」=类别、「驱动程序版本」=版本（旧版是独立「日期」「版本」两行）。</summary>
    private static string FieldKey(string rawKey)
    {
        string key = rawKey.Trim();
        if (key.Contains("GUID", StringComparison.OrdinalIgnoreCase))
        {
            return "guid";
        }

        if (key.Contains("原始", StringComparison.OrdinalIgnoreCase) || key.Contains("original", StringComparison.OrdinalIgnoreCase))
        {
            return "original";
        }

        if (key.Contains("类型", StringComparison.OrdinalIgnoreCase) || key.Contains("type", StringComparison.OrdinalIgnoreCase))
        {
            return "type";
        }

        if (key.Contains("类名", StringComparison.OrdinalIgnoreCase) || key.Contains("类别", StringComparison.OrdinalIgnoreCase)
            || key.Contains("class", StringComparison.OrdinalIgnoreCase))
        {
            return "class";
        }

        if (key.Contains("提供", StringComparison.OrdinalIgnoreCase) || key.Contains("provider", StringComparison.OrdinalIgnoreCase))
        {
            return "provider";
        }

        if (key.Contains("日期", StringComparison.OrdinalIgnoreCase) || key.Contains("date", StringComparison.OrdinalIgnoreCase))
        {
            return "date";
        }

        if (key.Contains("版本", StringComparison.OrdinalIgnoreCase) || key.Contains("version", StringComparison.OrdinalIgnoreCase))
        {
            return "version";
        }

        return key.ToLowerInvariant();
    }

    /// <summary>
    /// 组装包：关键词命中优先；若 7 个业务字段未凑齐且块恰为 8 行，按位置序兜底
    /// （标签乱码时关键词全部失配，但 pnputil 的字段顺序跨语言稳定）。
    /// </summary>
    private static DriverPackage ToPackage(List<(string Key, string Value)> fields, IFormatProvider? culture)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string key, string value) in fields)
        {
            map.TryAdd(key, value);
        }

        static bool Has(Dictionary<string, string> m, string k) => m.ContainsKey(k) && m[k].Length > 0;
        bool keywordComplete = Has(map, "original") && Has(map, "type") && Has(map, "class")
            && Has(map, "guid") && Has(map, "provider") && Has(map, "date") && Has(map, "version");

        string Get(string key, int positionalIndex) =>
            !keywordComplete && fields.Count == 8
                ? positionalIndex < fields.Count ? fields[positionalIndex].Value : ""
                : map.GetValueOrDefault(key, "");

        string published = fields.Count > 0 ? fields[0].Value : "";
        if (map.TryGetValue("published", out string? declared) && declared.Length > 0)
        {
            published = declared;
        }

        string dateText = Get("date", 6);
        DateTime? date = null;
        string version = Get("version", 7);

        // Win11 新版 pnputil：无独立日期行，「驱动程序版本」= "日期 版本号"（实测 2026-09-04）。
        // 无独立日期且版本值含空格时，首段按日期拆出。
        if (date is null && !Has(map, "date") && !string.IsNullOrWhiteSpace(version))
        {
            int space = version.IndexOf(' ');
            if (space > 0
                && DateTime.TryParse(version[..space], culture ?? CultureInfo.CurrentCulture, DateTimeStyles.None, out DateTime fromVersion))
            {
                date = fromVersion;
                version = version[(space + 1)..].Trim();
            }
        }

        if (!string.IsNullOrWhiteSpace(dateText))
        {
            date = DateTime.TryParse(dateText, culture ?? CultureInfo.CurrentCulture, DateTimeStyles.None, out DateTime parsed)
                ? parsed
                : date;
        }

        return new DriverPackage
        {
            PublishedName = published,
            OriginalName = Get("original", 1),
            Type = Get("type", 2),
            ClassName = Get("class", 3),
            ClassGuid = Get("guid", 4),
            Provider = Get("provider", 5),
            Date = date,
            Version = version,
        };
    }

    // ------------------------------------------------------------------
    // XML 解析（v2.0 主路径，internal 供守卫测试直测）。
    // schema 实测定案 2026-09-05（Win11 26300）：
    //   <PnpUtil><Driver DriverName="oemX.inf"><OriginalName/><ProviderName/><ClassName/><ClassGuid/>
    //   <DriverVersion>MM/dd/yyyy a.b.c.d</DriverVersion><SignerName/><ExtensionId?/><CatalogAttributes>
    //   <Attribute/><WhcpVersion/><CatalogFile?/><Files><File Name=/></Files>
    //   <Devices><Device InstanceId=><DeviceDescription/><Status/></Device></Devices></Driver>...
    // 注意：DriverVersion 是"日期+版本"合并字段；输出编码虽声明 utf-8 但含中文时实为 OEMCP（解码已处理）。
    // ------------------------------------------------------------------
    internal static List<DriverPackage> ParseEnumXml(string xml)
    {
        var packages = new List<DriverPackage>();
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (XmlException)
        {
            return packages; // 非 XML（老系统回显帮助文本）→ 空列表交由调用方回退文本解析
        }

        foreach (XElement driver in doc.Descendants("Driver"))
        {
            string published = (string?)driver.Attribute("DriverName") ?? "";
            if (published.Length == 0)
            {
                continue;
            }

            string original = Get(driver, "OriginalName");
            string className = Get(driver, "ClassName");
            string classGuid = Get(driver, "ClassGuid");
            string provider = Get(driver, "ProviderName");
            string signer = Get(driver, "SignerName");
            string extensionId = Get(driver, "ExtensionId");
            int fileCount = driver.Descendants("File").Count();

            // DriverVersion = "MM/dd/yyyy a.b.c.d"（无独立日期节点，实测）
            string versionRaw = Get(driver, "DriverVersion");
            DateTime? date = null;
            string version = versionRaw;
            int space = versionRaw.IndexOf(' ');
            if (space > 0
                && DateTime.TryParseExact(
                    versionRaw[..space],
                    new[] { "MM/dd/yyyy", "M/d/yyyy", "yyyy/M/d" },
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out DateTime parsed))
            {
                date = parsed;
                version = versionRaw[(space + 1)..].Trim();
            }

            var deviceNames = driver.Descendants("Device")
                .Select(d => Get(d, "DeviceDescription"))
                .Where(n => n.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            packages.Add(new DriverPackage
            {
                PublishedName = published,
                OriginalName = original,
                ClassName = className,
                ClassGuid = classGuid,
                Provider = provider,
                Date = date,
                Version = version,
                SignerName = signer,
                ExtensionId = extensionId,
                FileCount = fileCount,
                DeviceNames = deviceNames,
            });
        }

        return packages;
    }

    private static string Get(XElement element, string name) =>
        (string?)element.Element(name) ?? "";
}
