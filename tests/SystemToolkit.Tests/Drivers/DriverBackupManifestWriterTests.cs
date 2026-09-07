using System.Security.Cryptography;
using SystemToolkit.Core.Drivers;

namespace SystemToolkit.Tests.Drivers;

/// <summary>
/// FR-02 备份三件套产物（manifest / devices / checksum）生成守卫。
/// </summary>
public class DriverBackupManifestWriterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"stk_bk_test_{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static DriverPackage Pkg(string published) => new()
    {
        PublishedName = published,
        OriginalName = "origin.inf",
        Provider = "TestCorp",
        Version = "1.2.3.4",
        Date = new DateTime(2026, 1, 2),
        ClassName = "Net",
        ClassGuid = "{4d36e972-e325-11ce-bfc1-08002be10318}",
    };

    [Fact]
    public void Write_GeneratesTripleArtifacts_WithCorrectContentStructure()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "Drivers", "oem1.inf_amd64_1"));
        byte[] payload = { 1, 2, 3, 4, 5 };
        File.WriteAllBytes(Path.Combine(_dir, "Drivers", "oem1.inf_amd64_1", "test.sys"), payload);

        var packages = new List<DriverPackage> { Pkg("oem1.inf") };
        var bindings = new Dictionary<string, List<DriverDeviceMapper.DeviceBinding>>(StringComparer.OrdinalIgnoreCase)
        {
            ["oem1.inf"] = new() { new DriverDeviceMapper.DeviceBinding("测试网卡", "PCI\\VEN_1234&DEV_5678") },
        };

        DriverBackupManifestWriter.Write(_dir, DriverBackupScope.ThirdPartyOnly, packages, bindings);

        Assert.True(File.Exists(Path.Combine(_dir, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(_dir, "devices.json")));
        Assert.True(File.Exists(Path.Combine(_dir, "checksum.json")));

        string manifest = File.ReadAllText(Path.Combine(_dir, "manifest.json"));
        Assert.Contains("\"PackageCount\": 1", manifest);
        Assert.Contains("oem1.inf", manifest);
        Assert.Contains("ThirdPartyOnly", manifest);
        Assert.Contains(Environment.MachineName, manifest);

        string devices = File.ReadAllText(Path.Combine(_dir, "devices.json"));
        Assert.Contains("测试网卡", devices);
        Assert.Contains("PCI\\\\VEN_1234&DEV_5678", devices);

        string checksum = File.ReadAllText(Path.Combine(_dir, "checksum.json"));
        string expected = Convert.ToHexStringLower(SHA256.HashData(payload));
        Assert.Contains("Drivers/oem1.inf_amd64_1/test.sys", checksum);
        Assert.Contains(expected, checksum);
    }

    [Fact]
    public void Write_NoDriversDirectory_ChecksumIsEmptyObject()
    {
        DriverBackupManifestWriter.Write(
            _dir, DriverBackupScope.All,
            new List<DriverPackage> { Pkg("oem9.inf") },
            new Dictionary<string, List<DriverDeviceMapper.DeviceBinding>>());

        Assert.Equal("{}", File.ReadAllText(Path.Combine(_dir, "checksum.json")));
    }

    [Fact]
    public void ComputeSha256_MatchesKnownValue()
    {
        string file = Path.Combine(_dir, "a.bin");
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(file, new byte[] { 0x61 }); // "a"
        Assert.Equal("ca978112ca1bbdcafac231b39a23dc4da786eff8147c4e72b9807785afee48bb",
            DriverBackupManifestWriter.ComputeSha256(file));
    }
}
