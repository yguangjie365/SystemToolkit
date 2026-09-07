using SystemToolkit.Core.Backup.Models;
using SystemToolkit.Core.Backup.Services;

namespace SystemToolkit.Tests;

/// <summary>目录扫描器测试（自旧工程移植，L15 英文命名）：基础扫描、符号链接跳过、多源前缀冲突。</summary>
public class DirectoryScannerTests
{
    private static string Temp()
    {
        string d = Path.Combine(Path.GetTempPath(), "fb_scan_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void ScanSingleFile_ReturnsOneFile_WithFileNameAsRelative()
    {
        string tmp = Temp();
        try
        {
            string file = Path.Combine(tmp, "single.txt");
            File.WriteAllText(file, "abc");

            ScanResult scan = DirectoryScanner.ScanSingleSource(file);

            Assert.Single(scan.Files);
            Assert.Equal("single.txt", scan.Files[0].RelativePath);
            Assert.Equal(3, scan.Files[0].Length);
            Assert.Empty(scan.EmptyDirs);
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public void ScanFolder_ReturnsFilesAndEmptyDirs()
    {
        string tmp = Temp();
        try
        {
            Directory.CreateDirectory(Path.Combine(tmp, "sub"));
            Directory.CreateDirectory(Path.Combine(tmp, "emptydir"));
            File.WriteAllText(Path.Combine(tmp, "a.txt"), "1");
            File.WriteAllText(Path.Combine(tmp, "sub", "b.txt"), "22");

            ScanResult scan = DirectoryScanner.ScanSingleSource(tmp);

            Assert.True(scan.Files.Count == 2);
            Assert.Contains("a.txt", scan.Files.Select(f => f.RelativePath));
            Assert.Contains("sub/b.txt", scan.Files.Select(f => f.RelativePath));
            Assert.Contains("emptydir", scan.EmptyDirs);
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public void ScanSingleSource_SkipsSymbolicLinkFile()
    {
        string tmp = Temp();
        try
        {
            string target = Path.Combine(tmp, "real.txt");
            File.WriteAllText(target, "data");
            string link = Path.Combine(tmp, "link.txt");
            try
            { File.CreateSymbolicLink(link, target); }
            catch { return; } // 当前环境无创建符号链接权限，跳过本断言

            ScanResult scan = DirectoryScanner.ScanSingleSource(tmp);

            Assert.Single(scan.Files);
            Assert.Equal("real.txt", scan.Files[0].RelativePath);
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public void ScanSingleSource_SkipsSymbolicLinkDirectory()
    {
        string tmp = Temp();
        try
        {
            Directory.CreateDirectory(Path.Combine(tmp, "real"));
            File.WriteAllText(Path.Combine(tmp, "real", "x.txt"), "1");
            string link = Path.Combine(tmp, "linkdir");
            try
            { Directory.CreateSymbolicLink(link, Path.Combine(tmp, "real")); }
            catch { return; } // 无权限则跳过

            ScanResult scan = DirectoryScanner.ScanSingleSource(tmp);

            // 真实目录下的文件被收录，符号链接目录被跳过
            Assert.Single(scan.Files);
            Assert.Equal("real/x.txt", scan.Files[0].RelativePath);
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public void ScanMultiSources_PrefixCollisionGetsHashSuffix()
    {
        string tmp = Temp();
        try
        {
            string a = Path.Combine(tmp, "alpha", "data");
            string b = Path.Combine(tmp, "beta", "data");
            Directory.CreateDirectory(a);
            Directory.CreateDirectory(b);
            File.WriteAllText(Path.Combine(a, "x.txt"), "1");
            File.WriteAllText(Path.Combine(b, "y.txt"), "2");

            var rule = new BackupRule
            {
                RuleName = "multi",
                SourceType = SourceTypes.Folder,
                SourcePaths = [a, b],
            };
            ScanResult scan = DirectoryScanner.ScanMultiSources(rule);

            Assert.True(scan.Files.Count == 2);
            var rels = scan.Files.Select(f => f.RelativePath).ToList();
            // 两个同名源目录（都叫 data）必须前缀隔离，不能互相覆盖
            Assert.True(rels.Any(r => r == "data/x.txt"), $"期望含 data/x.txt，实际：{string.Join(",", rels)}");
            Assert.True(rels.Any(r => r.StartsWith("data_") && r.EndsWith("/y.txt")),
                $"冲突源应加哈希后缀，实际：{string.Join(",", rels)}");
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }
}
