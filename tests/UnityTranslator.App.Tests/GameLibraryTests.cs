using UnityTranslator.App;

namespace UnityTranslator.App.Tests;

public sealed class GameLibraryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "unity-translator-library-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void ExeAndFolderBecomeOnePersistentCard()
    {
        var gameRoot = Path.Combine(_root, "Game");
        Directory.CreateDirectory(Path.Combine(gameRoot, "Game_Data", "Managed"));
        var exe = Path.Combine(gameRoot, "Game.exe");
        WriteFakeX64Pe(exe);
        File.WriteAllBytes(Path.Combine(gameRoot, "UnityPlayer.dll"), [1]);
        File.WriteAllBytes(Path.Combine(gameRoot, "Game_Data", "Managed", "Assembly-CSharp.dll"), [1]);
        var libraryFile = Path.Combine(_root, "library.json");

        var library = new GameLibrary(libraryFile);
        library.Add(exe);
        library.Add(gameRoot);

        var card = Assert.Single(new GameLibrary(libraryFile).List());
        Assert.Equal(exe, card.ExePath);
        Assert.Equal("Mono", card.Backend);
    }

    private static void WriteFakeX64Pe(string path)
    {
        var bytes = new byte[256];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(bytes, 0x3c);
        bytes[0x80] = (byte)'P';
        bytes[0x81] = (byte)'E';
        BitConverter.GetBytes((ushort)0x8664).CopyTo(bytes, 0x84);
        File.WriteAllBytes(path, bytes);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
