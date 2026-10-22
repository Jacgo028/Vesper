using System.Text.Json;
using UnityTranslator.App;
using UnityTranslator.Core;

namespace UnityTranslator.App.Tests;

/// <summary>
/// "这款游戏实测 BepInEx 6 起不来，改用 BepInEx 5" 这个结论**能不能活过一次重启**（第 100 轮）。
///
/// ★ 为什么专门测这件事：
///   自动回退本身只做一次（换文件 + 记结论）。真正决定它有没有用的是**结论存不存得住**：
///   存不住的话，用户每次启动 App 都会退回"按版本号自动判断"，于是每次都被装回
///   起不来的那一档 —— 表面上"修了"，实际上一点没修。
///   而这条链上有三个独立的静默失效点（都是"不报错、只是不生效"）：
///     ① `GameProfile` 重新探测时把 `PreferredLoader` 冲掉；
///     ② `JsonSerializer` 的命名策略改了，字段名对不上（序列化成 `PreferredLoader`
///        还是 `preferredLoader`，读的写的必须一致）；
///     ③ 读的是界面卡片（快照）而不是列表（真相），回退之后卡片还是旧值。
/// </summary>
public sealed class LoaderPreferencePersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "ut-loader-pref-" + Guid.NewGuid().ToString("N"));

    private string LibraryFile => Path.Combine(_root, "games.json");
    private string GameRoot => Path.Combine(_root, "Game");
    private string Exe => Path.Combine(GameRoot, "Game.exe");

    /// <summary>造一个能被 GameDetector 认出来的 Mono 游戏目录。</summary>
    private void WriteFakeGame()
    {
        Directory.CreateDirectory(Path.Combine(GameRoot, "Game_Data", "Managed"));
        var bytes = new byte[256];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(bytes, 0x3c);
        bytes[0x80] = (byte)'P';
        bytes[0x81] = (byte)'E';
        BitConverter.GetBytes((ushort)0x8664).CopyTo(bytes, 0x84);
        File.WriteAllBytes(Exe, bytes);
        File.WriteAllBytes(Path.Combine(GameRoot, "UnityPlayer.dll"), [1]);
        File.WriteAllBytes(Path.Combine(GameRoot, "Game_Data", "Managed", "Assembly-CSharp.dll"), [1]);
    }

    [Fact]
    public void TheDecisionSurvivesARestart()
    {
        WriteFakeGame();
        var library = new GameLibrary(LibraryFile);
        var game = library.Add(Exe);

        Assert.True(library.RememberPreferredLoader(game.GameId, GameProfile.BepInEx5Loader));

        // ＝重新打开 App
        var reopened = new GameLibrary(LibraryFile);

        Assert.Equal(GameProfile.BepInEx5Loader, reopened.PreferredLoaderFor(game.GameId));
        Assert.True(reopened.List().Single().PrefersBepInEx5);
    }

    [Fact]
    public void ReAddingTheSameGameKeepsTheDecision()
    {
        // 用户再点一次"添加游戏"（或拖进同一个目录）不能把结论冲掉。
        WriteFakeGame();
        var library = new GameLibrary(LibraryFile);
        var game = library.Add(Exe);
        library.RememberPreferredLoader(game.GameId, GameProfile.BepInEx5Loader);

        library.Add(Exe);

        Assert.Equal(GameProfile.BepInEx5Loader, library.PreferredLoaderFor(game.GameId));
    }

    [Fact]
    public void AFreshGameHasNoDecision()
    {
        WriteFakeGame();
        var library = new GameLibrary(LibraryFile);
        var game = library.Add(Exe);

        Assert.Equal(string.Empty, library.PreferredLoaderFor(game.GameId));
        Assert.False(library.List().Single().PrefersBepInEx5);
        // 没记录过的游戏问也不该抛，也不该瞎猜一个加载器回来。
        Assert.Equal(string.Empty, library.PreferredLoaderFor("no-such-game"));
    }

    [Fact]
    public void RememberingTheSameDecisionTwiceChangesNothing()
    {
        WriteFakeGame();
        var library = new GameLibrary(LibraryFile);
        var game = library.Add(Exe);

        Assert.True(library.RememberPreferredLoader(game.GameId, GameProfile.BepInEx5Loader));
        // 第二次返回 false：调用方靠它决定"要不要告诉用户我又改了一次"。
        Assert.False(library.RememberPreferredLoader(game.GameId, GameProfile.BepInEx5Loader));
    }

    [Fact]
    public void RememberingForAnUnknownGameDoesNotThrow()
    {
        var library = new GameLibrary(LibraryFile);
        Assert.False(library.RememberPreferredLoader("no-such-game", GameProfile.BepInEx5Loader));
    }

    [Fact]
    public void OlderLibraryFilesWithoutTheFieldStillLoad()
    {
        // 老用户机器上的 games.json 没有这个字段。读出来必须是"自动判断"，
        // 而且**不能因为多了一个字段就整份读不出来**（那会让用户的游戏列表消失）。
        WriteFakeGame();
        var library = new GameLibrary(LibraryFile);
        var game = library.Add(Exe);

        var json = File.ReadAllText(LibraryFile).Replace(",\"PreferredLoader\":\"\"", string.Empty);
        File.WriteAllText(LibraryFile, json);
        Assert.DoesNotContain("PreferredLoader", json);

        var reopened = new GameLibrary(LibraryFile);

        Assert.Single(reopened.List());
        Assert.Equal(string.Empty, reopened.PreferredLoaderFor(game.GameId));
    }

    [Fact]
    public void TheJsonFieldNameIsStable()
    {
        // 字段名是**磁盘格式**的一部分：改名字会让所有用户已存的结论失效
        // （表现是"回退过，但下次启动又换回去了"），而且不会报任何错。
        WriteFakeGame();
        var library = new GameLibrary(LibraryFile);
        var game = library.Add(Exe);
        library.RememberPreferredLoader(game.GameId, GameProfile.BepInEx5Loader);

        var text = File.ReadAllText(LibraryFile);
        Assert.Contains("\"PreferredLoader\":\"BepInEx5\"", text);

        // 反向也要成立：按这个字面字段名手写的记录必须能被读出来。
        var handwritten = Path.Combine(_root, "handwritten.json");
        File.WriteAllText(handwritten, JsonSerializer.Serialize(new[]
        {
            new GameProfile(game.GameId, GameRoot, Exe, "2022.3.62f2", "Mono", "x64", true, string.Empty)
            {
                PreferredLoader = GameProfile.BepInEx5Loader
            }
        }));
        Assert.Equal(GameProfile.BepInEx5Loader, new GameLibrary(handwritten).PreferredLoaderFor(game.GameId));
    }

    [Fact]
    public void ThePreferredLoaderDecidesWhichPackageGetsInstalled()
    {
        // 结论最终要落到"装哪个包"上 —— 这一步弄反了就是"换了等于没换"。
        var game = new GameProfile("g", _root, Exe, "2022.3.62f2 (7670c08855a9)", "Mono", "x64", true, "")
        {
            PreferredLoader = GameProfile.BepInEx5Loader
        };

        // Unity 2022 默认是 BepInEx 6；带上"实测起不来"的结论之后必须换成 BepInEx 5。
        var neutral = game with { PreferredLoader = string.Empty };
        Assert.Equal("unity2022-mono", PackageCatalog.ResolvePackageName(neutral, LoaderFamily.Auto));
        Assert.Equal("unity2022-mono-bepinex5", PackageCatalog.ResolvePackageName(game, LoaderFamily.BepInEx5));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (Exception) { }
    }
}
