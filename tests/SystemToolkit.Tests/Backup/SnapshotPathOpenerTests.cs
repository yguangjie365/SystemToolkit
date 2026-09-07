using SystemToolkit.Core.Backup.Services;
using SystemToolkit.Core.Contracts;

namespace SystemToolkit.Tests;

/// <summary>
/// SnapshotPathOpener 测试（自旧工程移植，L15 英文命名）：校验、规范化、目录/文件分派、
/// 脏数据拒绝与告警落盘。打开动作经委托注入，测试不真的启动资源管理器。
/// </summary>
public class SnapshotPathOpenerTests
{
    private sealed class CapturingLogger : ILogger
    {
        public List<string> Warnings { get; } = [];
        public List<string> Errors { get; } = [];
        public void Warn(string message) => Warnings.Add(message);
        public void Error(string message, Exception? ex = null) => Errors.Add(message);
        public void Info(string message) { }
    }

    private static string MakeTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "snapopen_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Open_ExistingDirectory_InvokesOpenDelegate()
    {
        string dir = MakeTempDir();
        try
        {
            string? opened = null;
            var opener = new SnapshotPathOpener(openDirectory: p => opened = p);

            SnapshotOpenResult result = opener.Open(dir);

            Assert.Equal(SnapshotOpenResultKind.Opened, result.Kind);
            Assert.Equal(dir, opened);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Open_ExistingFile_InvokesRevealDelegate()
    {
        string dir = MakeTempDir();
        try
        {
            string file = Path.Combine(dir, "hello.txt");
            File.WriteAllText(file, "x");
            string? revealed = null;
            var opener = new SnapshotPathOpener(revealFile: p => revealed = p);

            SnapshotOpenResult result = opener.Open(file);

            Assert.Equal(SnapshotOpenResultKind.Opened, result.Kind);
            Assert.Equal(file, revealed);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Open_MissingPath_NotFoundWithNormalizedFullPath()
    {
        string dir = MakeTempDir();
        try
        {
            string missing = Path.Combine(dir, "missing");
            var opener = new SnapshotPathOpener();

            SnapshotOpenResult result = opener.Open(missing);

            Assert.Equal(SnapshotOpenResultKind.NotFound, result.Kind);
            Assert.Equal(Path.GetFullPath(missing), result.FullPath);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Open_BlankPath_InvalidPathAndNoDelegateInvoked(string path)
    {
        bool anythingOpened = false;
        var opener = new SnapshotPathOpener(
            openDirectory: _ => anythingOpened = true,
            revealFile: _ => anythingOpened = true);

        SnapshotOpenResult result = opener.Open(path);

        Assert.Equal(SnapshotOpenResultKind.InvalidPath, result.Kind);
        Assert.False(anythingOpened, "无效路径不得触发任何打开动作");
    }

    [Fact]
    public void Open_IllegalCharacterPath_InvalidPathAndWarnLogged()
    {
        var logger = new CapturingLogger();
        bool anythingOpened = false;
        var opener = new SnapshotPathOpener(logger,
            openDirectory: _ => anythingOpened = true,
            revealFile: _ => anythingOpened = true);

        SnapshotOpenResult result = opener.Open("bad\0path");

        Assert.Equal(SnapshotOpenResultKind.InvalidPath, result.Kind);
        Assert.False(anythingOpened);
        Assert.Contains(logger.Warnings, w => w.Contains("快照路径无效"));
    }

    [Fact]
    public void Open_WhitelistDenies_DeniedAndWarnLogged()
    {
        var logger = new CapturingLogger();
        bool anythingOpened = false;
        var opener = new SnapshotPathOpener(logger,
            openDirectory: _ => anythingOpened = true,
            revealFile: _ => anythingOpened = true);

        SnapshotOpenResult result = opener.Open("D:\\somewhere\\outside", _ => false);

        Assert.Equal(SnapshotOpenResultKind.Denied, result.Kind);
        Assert.False(anythingOpened);
        Assert.Contains(logger.Warnings, w => w.Contains("快照路径越界"));
    }

    [Fact]
    public void Open_WhitelistPasses_OpensNormally()
    {
        string dir = MakeTempDir();
        try
        {
            string? opened = null;
            var opener = new SnapshotPathOpener(openDirectory: p => opened = p);

            SnapshotOpenResult result = opener.Open(dir, p => p == dir);

            Assert.Equal(SnapshotOpenResultKind.Opened, result.Kind);
            Assert.Equal(dir, opened);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
