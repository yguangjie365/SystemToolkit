using SystemToolkit.Core.Drivers;

namespace SystemToolkit.Tests.Drivers;

/// <summary>
/// PnPUtil XML 解析守卫（v2.0，2026-09-05 本机 Win11 26300 实测 schema 定案）。
/// 覆盖：标准块/日期版本合并拆分/扩展驱动 ExtensionId/文件计数/官方设备关联/非 XML 回退。
/// </summary>
public class PnpUtilXmlParserTests
{
    private const string TwoDriversXml = """
        <?xml version="1.0" encoding="utf-8"?>
        <PnpUtil Version="10.0.26300" Command="/enum-drivers /devices /files /format xml">
            <Driver DriverName="oem35.inf">
                <OriginalName>oem54.inf</OriginalName>
                <ProviderName>NVIDIA Corporation</ProviderName>
                <ClassName>MEDIA</ClassName>
                <ClassGuid>{4d36e96c-e325-11ce-bfc1-08002be10318}</ClassGuid>
                <DriverVersion>03/15/2026 1.4.5.7</DriverVersion>
                <SignerName>Microsoft Windows Hardware Compatibility Publisher</SignerName>
                <CatalogAttributes>
                    <Attribute>Declarative</Attribute>
                </CatalogAttributes>
                <WhcpVersion>Unknown</WhcpVersion>
                <CatalogFile>nvhda.cat</CatalogFile>
                <Files>
                    <File Name="nvhda64v.sys"></File>
                </Files>
                <Devices>
                    <Device InstanceId="HDAUDIO\FUNC_01&amp;VEN_10DE&amp;DEV_009F&amp;SUBSYS_FFFFFFFF&amp;REV_1001\5&amp;348b2000&amp;1&amp;0001">
                        <DeviceDescription>NVIDIA High Definition Audio</DeviceDescription>
                        <Status>Started</Status>
                    </Device>
                </Devices>
            </Driver>
            <Driver DriverName="oem2.inf">
                <OriginalName>alderlakedmasecextension.inf</OriginalName>
                <ProviderName>INTEL</ProviderName>
                <ClassName>Extension</ClassName>
                <ClassGuid>{e2f84ce7-8efa-411c-aa69-97454ca4cb57}</ClassGuid>
                <ExtensionId>{aaa2e5d0-bba2-4900-95b4-fee1e274e6fe}</ExtensionId>
                <DriverVersion>07/18/1968 10.1.45.9</DriverVersion>
                <SignerName>Microsoft Windows Hardware Compatibility Publisher</SignerName>
                <CatalogAttributes>
                    <Attribute>Declarative</Attribute>
                </CatalogAttributes>
                <WhcpVersion>Unknown</WhcpVersion>
                <Files>
                    <File Name="a.sys"></File>
                    <File Name="b.dll"></File>
                </Files>
            </Driver>
        </PnpUtil>
        """;

    [Fact]
    public void ParseXml_StandardBlock_AllFieldsPresent()
    {
        List<DriverPackage> packages = PnpUtilService.ParseEnumXml(TwoDriversXml);
        Assert.Equal(2, packages.Count);

        DriverPackage d = packages[0];
        Assert.Equal("oem35.inf", d.PublishedName);
        Assert.Equal("oem54.inf", d.OriginalName);
        Assert.Equal("NVIDIA Corporation", d.Provider);
        Assert.Equal("MEDIA", d.ClassName);
        Assert.Equal("{4d36e96c-e325-11ce-bfc1-08002be10318}", d.ClassGuid);
        Assert.Equal("Microsoft Windows Hardware Compatibility Publisher", d.SignerName);
        Assert.Equal("1.4.5.7", d.Version);
        Assert.NotNull(d.Date);
        Assert.Equal(2026, d.Date!.Value.Year);
        Assert.Equal(3, d.Date.Value.Month);
        Assert.Equal(15, d.Date.Value.Day);
        Assert.Equal(1, d.FileCount);
        Assert.Single(d.DeviceNames);
        Assert.Equal("NVIDIA High Definition Audio", d.DeviceNames[0]);
    }

    [Fact]
    public void ParseXml_ExtensionDriver_ExtensionIdAndMultipleFiles()
    {
        List<DriverPackage> packages = PnpUtilService.ParseEnumXml(TwoDriversXml);
        DriverPackage d = packages[1];
        Assert.Equal("{aaa2e5d0-bba2-4900-95b4-fee1e274e6fe}", d.ExtensionId);
        Assert.Equal(2, d.FileCount);
        Assert.Equal("10.1.45.9", d.Version);
        Assert.NotNull(d.Date);
        Assert.Equal(1968, d.Date!.Value.Year);
        Assert.Empty(d.DeviceNames); // 无设备关联 → 空列表
    }

    [Fact]
    public void ParseXml_DriverWithoutDeviceNameAttribute_IsSkipped()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <PnpUtil Version="10.0.26300">
                <Driver>
                    <ProviderName>无发布名</ProviderName>
                </Driver>
            </PnpUtil>
            """;

        Assert.Empty(PnpUtilService.ParseEnumXml(xml));
    }

    [Fact]
    public void ParseXml_NonXmlText_ReturnsEmptyDoesNotThrow()
    {
        Assert.Empty(PnpUtilService.ParseEnumXml("发布名称:      oem1.inf\n原始名称:      x.inf"));
        Assert.Empty(PnpUtilService.ParseEnumXml(""));
    }

    [Fact]
    public void ParseXml_MergedDateVersionField_ThreeDateFormats()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <PnpUtil>
                <Driver DriverName="a.inf"><DriverVersion>1/2/2026 1.0.0.0</DriverVersion></Driver>
                <Driver DriverName="b.inf"><DriverVersion>纯版本无日期</DriverVersion></Driver>
            </PnpUtil>
            """;

        List<DriverPackage> packages = PnpUtilService.ParseEnumXml(xml);
        Assert.Equal(2, packages.Count);
        Assert.NotNull(packages[0].Date);
        Assert.Equal(1, packages[0].Date!.Value.Month);
        Assert.Equal("1.0.0.0", packages[0].Version);
        // 无法拆分时：版本字段保留原文，日期为 null
        Assert.Null(packages[1].Date);
        Assert.Equal("纯版本无日期", packages[1].Version);
    }
}
