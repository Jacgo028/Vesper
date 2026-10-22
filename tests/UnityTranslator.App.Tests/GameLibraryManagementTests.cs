using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using UnityTranslator.App;
using UnityTranslator.Core;

namespace UnityTranslator.App.Tests;

public sealed class GameLibraryManagementTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "unity-library-management", Guid.NewGuid().ToString("N"));
    private string LibraryFile => Path.Combine(_root, "library.json");

    [Fact]
    public void RenameSurvivesReopenAndRedetectionWithoutLosingLoader()
    {
        var exe = CreateGame("Game");
        var library = new GameLibrary(LibraryFile);
        var game = library.Add(exe);
        library.RememberPreferredLoader(game.GameId, GameProfile.BepInEx5Loader);
        Assert.True(Rename(library, game.GameId, "  我的游戏  "));
        Assert.Equal("我的游戏", Assert.Single(library.List()).DisplayName);
        Assert.Equal("我的游戏", library.Add(exe).DisplayName);
        var reopened = Assert.Single(new GameLibrary(LibraryFile).List());
        Assert.Equal("我的游戏", reopened.DisplayName);
        Assert.Equal(GameProfile.BepInEx5Loader, reopened.PreferredLoader);
        Assert.Equal("我的游戏", JsonNode.Parse(File.ReadAllText(LibraryFile))![0]!["CustomName"]!.GetValue<string>());
    }

    [Fact]
    public void RemovePersistsOnlyLibraryChangeAndLeavesGameAndDatabaseBytesUntouched()
    {
        var exe = CreateGame("Game");
        var database = Path.Combine(Path.GetDirectoryName(exe)!, "translations.db");
        File.WriteAllText(database, "saved translations");
        var before = Directory.GetFiles(Path.GetDirectoryName(exe)!, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        var library = new GameLibrary(LibraryFile);
        var game = library.Add(exe);
        var other = library.Add(CreateGame("Other"));
        Assert.True(Remove(library, game.GameId));
        Assert.Equal(other.GameId, Assert.Single(new GameLibrary(LibraryFile).List()).GameId);
        Assert.False(Remove(library, game.GameId));
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(Path.GetDirectoryName(exe)!, "*", SearchOption.AllDirectories).Order());
        foreach (var entry in before)
            Assert.Equal(entry.Value, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(entry.Key))));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankAliasRestoresExecutableName(string? name)
    {
        var library = new GameLibrary(LibraryFile);
        var game = library.Add(CreateGame("Game"));
        Rename(library, game.GameId, "Alias");
        Assert.True(Rename(library, game.GameId, name));
        Assert.Equal("Game", Assert.Single(new GameLibrary(LibraryFile).List()).DisplayName);
    }

    [Theory]
    [InlineData("line\nbreak")]
    [InlineData("tab\tname")]
    [InlineData("control\u0001")]
    [InlineData("\t")]
    public void ControlCharactersAreRejectedWithoutChangingLibrary(string alias)
    {
        var library = new GameLibrary(LibraryFile);
        var game = library.Add(CreateGame("Game"));
        var saved = File.ReadAllText(LibraryFile);
        Assert.Throws<ArgumentException>(() => Rename(library, game.GameId, alias));
        Assert.Equal("Game", Assert.Single(library.List()).DisplayName);
        Assert.Equal(saved, File.ReadAllText(LibraryFile));
    }

    [Fact]
    public void EightyCharactersAreAcceptedButEightyOneAreRejected()
    {
        var library = new GameLibrary(LibraryFile);
        var game = library.Add(CreateGame("Game"));
        Assert.True(Rename(library, game.GameId, new string('a', 80)));
        Assert.Throws<ArgumentException>(() => Rename(library, game.GameId, new string('b', 81)));
        Assert.Equal(new string('a', 80), Assert.Single(new GameLibrary(LibraryFile).List()).DisplayName);
    }

    [Fact]
    public void UnknownAndUnchangedRenamesDoNotSave()
    {
        var library = new GameLibrary(LibraryFile);
        var game = library.Add(CreateGame("Game"));
        Rename(library, game.GameId, "Alias");
        Directory.CreateDirectory(LibraryFile + ".tmp");
        Assert.False(Rename(library, "missing", "Alias"));
        Assert.False(Rename(library, game.GameId, " Alias "));
        Assert.False(Remove(library, "missing"));
    }

    [Fact]
    public void OldJsonWithoutAliasStillLoadsWithExecutableName()
    {
        var game = GameDetector.Detect(CreateGame("Game"));
        var json = JsonSerializer.SerializeToNode(game)!;
        json.AsObject().Remove("CustomName");
        File.WriteAllText(LibraryFile, new JsonArray(json).ToJsonString());
        Assert.Equal("Game", Assert.Single(new GameLibrary(LibraryFile).List()).DisplayName);
    }

    [Fact]
    public void LoadRefreshPreservesSavedAliasAndLoaderWhileUpdatingDetection()
    {
        var game = GameDetector.Detect(CreateGame("Game"));
        var json = JsonSerializer.SerializeToNode(game)!;
        json["CustomName"] = "  Saved Alias  ";
        json["PreferredLoader"] = GameProfile.BepInEx5Loader;
        json["Backend"] = "Outdated";
        File.WriteAllText(LibraryFile, new JsonArray(json).ToJsonString());
        var refreshed = Assert.Single(new GameLibrary(LibraryFile).List());
        Assert.Equal("Saved Alias", refreshed.DisplayName);
        Assert.Equal("Mono", refreshed.Backend);
        Assert.Equal(GameProfile.BepInEx5Loader, refreshed.PreferredLoader);
    }

    [Theory]
    [InlineData("rename")]
    [InlineData("remove")]
    [InlineData("loader")]
    [InlineData("add")]
    [InlineData("redetect")]
    public void FailedSaveLeavesMemoryAndPersistedLibraryUnchanged(string operation)
    {
        var exe = CreateGame("Game");
        var library = new GameLibrary(LibraryFile);
        var game = library.Add(exe);
        var other = CreateGame("Other");
        var saved = File.ReadAllText(LibraryFile);
        var before = library.List().ToArray();
        if (operation == "redetect")
            File.Delete(Path.Combine(Path.GetDirectoryName(exe)!, "Game_Data", "Managed", "Assembly-CSharp.dll"));
        // A directory at the temporary-file path reliably denies the real save, without mocks or ACL assumptions.
        Directory.CreateDirectory(LibraryFile + ".tmp");
        var error = Record.Exception(() =>
        {
            switch (operation)
            {
                case "rename": Rename(library, game.GameId, "Alias"); break;
                case "remove": Remove(library, game.GameId); break;
                case "loader": library.RememberPreferredLoader(game.GameId, GameProfile.BepInEx5Loader); break;
                case "add": library.Add(other); break;
                case "redetect": library.Add(exe); break;
            }
        });
        Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString() ?? "Save unexpectedly succeeded");
        Assert.Equal(before, library.List());
        Assert.Equal(saved, File.ReadAllText(LibraryFile));
    }

    private static bool Rename(GameLibrary library, string gameId, string? name) => Invoke(library, "Rename", gameId, name);
    private static bool Remove(GameLibrary library, string gameId) => Invoke(library, "Remove", gameId);

    private static bool Invoke(GameLibrary library, string name, params object?[] args)
    {
        var method = typeof(GameLibrary).GetMethod(name, BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(method);
        try { return Assert.IsType<bool>(method.Invoke(library, args)); }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private string CreateGame(string name)
    {
        var folder = Path.Combine(_root, name);
        var managed = Path.Combine(folder, name + "_Data", "Managed");
        Directory.CreateDirectory(managed);
        File.WriteAllBytes(Path.Combine(managed, "Assembly-CSharp.dll"), [1]);
        File.WriteAllBytes(Path.Combine(folder, "UnityPlayer.dll"), [1]);
        var bytes = new byte[256];
        bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(bytes, 0x3c);
        bytes[0x80] = (byte)'P'; bytes[0x81] = (byte)'E';
        BitConverter.GetBytes((ushort)0x8664).CopyTo(bytes, 0x84);
        var exe = Path.Combine(folder, name + ".exe");
        File.WriteAllBytes(exe, bytes);
        return exe;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
