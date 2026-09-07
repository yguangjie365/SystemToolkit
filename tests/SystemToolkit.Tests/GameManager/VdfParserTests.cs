using System.Reflection;
using SystemToolkit.Core.GameManager.Models;
using SystemToolkit.Core.GameManager.Services;
using Xunit;

namespace SystemToolkit.Tests.GameManager;

/// <summary>
/// VdfParser + SteamService 登录记录重写回归（旧工程 2026-09-03 契约移植）：
/// 修复前 RewriteLoginUsersWithMostRecent/RemoveUser 误用 GetStr(k) 导致所有字段值被写成空串。
/// 本组用例钉死「重写后字段值原样保留、仅翻转 MostRecent/RememberPassword」这一契约。
/// </summary>
public class VdfParserTests
{
    private const string SampleLoginUsers =
        "\"users\"\n" +
        "{\n" +
        "\t\"76561197960265728\"\n" +
        "\t{\n" +
        "\t\t\"AccountName\"\t\t\"alice\"\n" +
        "\t\t\"PersonaName\"\t\t\"Alice\"\n" +
        "\t\t\"MostRecent\"\t\t\"1\"\n" +
        "\t\t\"RememberPassword\"\t\"1\"\n" +
        "\t\t\"Timestamp\"\t\t\"1700000000\"\n" +
        "\t}\n" +
        "\t\"76561197960265729\"\n" +
        "\t{\n" +
        "\t\t\"AccountName\"\t\t\"bob\"\n" +
        "\t\t\"PersonaName\"\t\t\"Bob\"\n" +
        "\t\t\"MostRecent\"\t\t\"0\"\n" +
        "\t\t\"RememberPassword\"\t\"0\"\n" +
        "\t\t\"Timestamp\"\t\t\"1690000000\"\n" +
        "\t}\n" +
        "}\n";

    private static VdfValue ParseSample() => VdfParser.Parse(SampleLoginUsers);

    [Fact]
    public void Parse_LoginUsers_AllUsersAndFieldsExtracted()
    {
        VdfValue root = ParseSample();
        VdfValue users = root.GetObjEntries()!.First(kv => kv.Key == "users").Value;
        VdfValue alice = users.GetObjEntries()!.First(kv => kv.Key == "76561197960265728").Value;
        Assert.Equal("alice", alice.GetStr("AccountName"));
        Assert.Equal("Alice", alice.GetStr("PersonaName"));
        Assert.Equal("1", alice.GetStr("MostRecent"));
    }

    [Fact]
    public void GetString_StringNodeReturnsOwnValue_ObjectNodeReturnsNull()
    {
        VdfValue root = ParseSample();
        VdfValue usersObj = root.GetObj("users")!;
        Assert.NotNull(usersObj.GetObjEntries());
        Assert.Null(usersObj.GetString()); // 对象节点 → null

        VdfValue accountNode = usersObj.GetObjEntries()!.First(kv => kv.Key == "76561197960265728").Value.GetObj("AccountName")!;
        Assert.Equal("alice", accountNode.GetString()); // 字符串节点 → 自身值
    }

    [Fact]
    public void RewriteLoginUsers_FieldValuesPreserved_OnlyTargetFlipsMostRecent()
    {
        string path = Path.Combine(Path.GetTempPath(), "loginusers_rewrite_" + Guid.NewGuid().ToString("N") + ".vdf");
        try
        {
            File.WriteAllText(path, SampleLoginUsers);

            MethodInfo method = typeof(SteamService).GetMethod(
                "RewriteLoginUsersWithMostRecent", BindingFlags.NonPublic | BindingFlags.Static)!;
            Assert.NotNull(method);
            method.Invoke(null, [path, "bob"]);

            VdfValue root = VdfParser.Parse(File.ReadAllText(path));
            VdfValue users = root.GetObj("users")!;
            VdfValue alice = users.GetObjEntries()!.First(kv => kv.Key == "76561197960265728").Value;
            VdfValue bob = users.GetObjEntries()!.First(kv => kv.Key == "76561197960265729").Value;

            Assert.Equal("alice", alice.GetStr("AccountName"));
            Assert.Equal("Alice", alice.GetStr("PersonaName"));
            Assert.Equal("1700000000", alice.GetStr("Timestamp"));
            Assert.Equal("0", alice.GetStr("MostRecent"));

            Assert.Equal("bob", bob.GetStr("AccountName"));
            Assert.Equal("Bob", bob.GetStr("PersonaName"));
            Assert.Equal("1690000000", bob.GetStr("Timestamp"));
            Assert.Equal("1", bob.GetStr("MostRecent"));
            Assert.Equal("1", bob.GetStr("RememberPassword"));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
            foreach (string bak in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".bak-*"))
                File.Delete(bak);
        }
    }

    [Fact]
    public void DeleteUser_RemainingUsers_Preserved()
    {
        string path = Path.Combine(Path.GetTempPath(), "loginusers_remove_" + Guid.NewGuid().ToString("N") + ".vdf");
        try
        {
            File.WriteAllText(path, SampleLoginUsers);

            MethodInfo method = typeof(SteamService).GetMethod(
                "RewriteLoginUsersRemoveUser", BindingFlags.NonPublic | BindingFlags.Static)!;
            Assert.NotNull(method);
            method.Invoke(null, [path, "76561197960265728"]);

            VdfValue root = VdfParser.Parse(File.ReadAllText(path));
            VdfValue users = root.GetObj("users")!;
            IReadOnlyList<KeyValuePair<string, VdfValue>> entries = users.GetObjEntries()!;
            Assert.Single(entries);
            Assert.Equal("76561197960265729", entries[0].Key);
            Assert.Equal("bob", entries[0].Value.GetStr("AccountName"));
            Assert.Equal("Bob", entries[0].Value.GetStr("PersonaName"));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
            foreach (string bak in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".bak-*"))
                File.Delete(bak);
        }
    }

    [Fact]
    public void Playtime_ReadFromLocalconfigPlaytimeKey_NotForever()
    {
        if (!OperatingSystem.IsWindows())
            return;

        string root = Path.Combine(Path.GetTempPath(), "steamtest_" + Guid.NewGuid().ToString("N"));
        try
        {
            string steamapps = Path.Combine(root, "steamapps");
            string userCfg = Path.Combine(root, "userdata", "68295936", "config");
            Directory.CreateDirectory(steamapps);
            Directory.CreateDirectory(userCfg);

            File.WriteAllText(Path.Combine(steamapps, "appmanifest_440.acf"),
                "\"AppState\"\n{\n" +
                "\t\"appid\"\t\t\"440\"\n" +
                "\t\"name\"\t\t\"Team Fortress 2\"\n" +
                "\t\"installdir\"\t\t\"TF2\"\n" +
                "\t\"SizeOnDisk\"\t\t\"12345678\"\n" +
                "}\n");

            File.WriteAllText(Path.Combine(userCfg, "localconfig.vdf"),
                "\"UserLocalConfigStore\"\n{\n" +
                "\t\"Software\"\n\t{\n" +
                "\t\t\"Valve\"\n\t\t{\n" +
                "\t\t\t\"Steam\"\n\t\t\t{\n" +
                "\t\t\t\t\"apps\"\n\t\t\t\t{\n" +
                "\t\t\t\t\t\"440\"\n\t\t\t\t\t{\n" +
                "\t\t\t\t\t\t\"Playtime\"\t\t\"54\"\n" +
                "\t\t\t\t\t\t\"LastPlayed\"\t\t\"1719154930\"\n" +
                "\t\t\t\t\t}\n" +
                "\t\t\t\t}\n" +
                "\t\t\t}\n" +
                "\t\t}\n" +
                "\t}\n" +
                "}\n");

            var service = new SteamService();
            var libraries = new List<SteamLibrary> { new() { Index = 0, Path = root, Apps = ["440"] } };
            SteamGame[] games = service.ScanInstalledGames(root, libraries);

            SteamGame game = Assert.Single(games);
            Assert.Equal(440u, game.AppId);
            Assert.Equal("Team Fortress 2", game.Name);
            Assert.Equal(54UL, game.PlaytimeMinutes);
            Assert.Equal(1719154930L, game.LastPlayed);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
