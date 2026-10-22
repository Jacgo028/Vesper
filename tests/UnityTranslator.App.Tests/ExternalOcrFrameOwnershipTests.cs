using System.Windows.Media.Imaging;
using System.Buffers;
using System.Reflection;
using UnityTranslator.App.ExternalOcr;

namespace UnityTranslator.App.Tests;

[Collection(WpfUiCollection.Name)]
public sealed class ExternalOcrFrameOwnershipTests
{
    [Fact]
    public async Task Lease_writes_real_png_and_removes_only_its_recorded_file()
    {
        using var lease = await OwnedOcrFrame.CreateAsync(ExternalOcrTestDoubles.Frame(), 7, default);
        var path = lease.Frame.ImagePath;
        Assert.True(File.Exists(path));
        Assert.Empty(lease.Frame.KnownUiBoxes);
        Assert.Equal(7, lease.Frame.FrameId);
        using (var input = File.OpenRead(path))
        {
            var png = new PngBitmapDecoder(input, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            Assert.Equal(32, png.Frames[0].PixelWidth);
            Assert.Equal(24, png.Frames[0].PixelHeight);
        }
        var directory = Path.GetDirectoryName(path)!;
        var other = Path.Combine(directory, "game-owned.png");
        File.WriteAllText(other, "keep");
        lease.Dispose();
        lease.Dispose();
        Assert.False(File.Exists(path));
        Assert.Equal("keep", File.ReadAllText(other));
        File.Delete(other);
        Directory.Delete(directory);
    }

    [Fact]
    public async Task Empty_lease_directory_is_removed_on_disposal()
    {
        var lease = await OwnedOcrFrame.CreateAsync(ExternalOcrTestDoubles.Frame(), 1, default);
        var directory = Path.GetDirectoryName(lease.Frame.ImagePath)!;
        lease.Dispose();
        Assert.False(Directory.Exists(directory));
        Assert.False(string.IsNullOrEmpty(directory));
    }

    [Fact]
    public async Task Cancellation_does_not_create_a_lease()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OwnedOcrFrame.CreateAsync(ExternalOcrTestDoubles.Frame(), 1, stop.Token));
    }

    [Fact]
    public async Task Invalid_pixels_are_rejected_before_writing()
    {
        var frame = ExternalOcrTestDoubles.Frame() with { Bgra32 = new byte[3] };
        await Assert.ThrowsAsync<ArgumentException>(() => OwnedOcrFrame.CreateAsync(frame, 1, default));
    }

    [Fact]
    public async Task Pixel_read_failure_removes_the_new_empty_directory()
    {
        using var temp = new IsolatedTemp();
        using var memory = new ControlledMemory(() => throw new IOException("read failed"));
        await Assert.ThrowsAsync<IOException>(() => OwnedOcrFrame.CreateAsync(
            ExternalOcrTestDoubles.Frame() with { Bgra32 = memory.Memory }, 1, default));
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.ExternalRoot));
    }

    [Fact]
    public async Task Cancellation_during_encoding_deletes_the_created_png_and_directory()
    {
        using var temp = new IsolatedTemp();
        using var stop = new CancellationTokenSource();
        using var memory = new ControlledMemory(() => stop.Cancel());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OwnedOcrFrame.CreateAsync(
            ExternalOcrTestDoubles.Frame() with { Bgra32 = memory.Memory }, 1, stop.Token));
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.ExternalRoot));
    }

    [Fact]
    public async Task Directory_write_failure_preserves_preexisting_file()
    {
        using var temp = new IsolatedTemp();
        Directory.CreateDirectory(Path.GetDirectoryName(temp.ExternalRoot)!);
        File.WriteAllText(temp.ExternalRoot, "belongs to someone else");
        await Assert.ThrowsAsync<IOException>(() => OwnedOcrFrame.CreateAsync(ExternalOcrTestDoubles.Frame(), 1, default));
        Assert.Equal("belongs to someone else", File.ReadAllText(temp.ExternalRoot));
    }

    [Fact]
    public async Task Creation_rejects_a_reparse_ancestor_without_writing_to_its_destination()
    {
        using var temp = new IsolatedTemp();
        var destination = Path.Combine(temp.Root, "destination");
        Directory.CreateDirectory(destination);
        var link = Path.Combine(temp.Root, "UnityTranslator");
        Directory.CreateSymbolicLink(link, destination);
        try
        {
            await Assert.ThrowsAsync<IOException>(() => OwnedOcrFrame.CreateAsync(ExternalOcrTestDoubles.Frame(), 1, default));
            Assert.Empty(Directory.EnumerateFileSystemEntries(destination));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public async Task Disposal_does_not_follow_a_replaced_directory_into_game_owned_files()
    {
        using var temp = new IsolatedTemp();
        var lease = await OwnedOcrFrame.CreateAsync(ExternalOcrTestDoubles.Frame(), 1, default);
        var directory = Path.GetDirectoryName(lease.Frame.ImagePath)!;
        var original = directory + "-original";
        var destination = Path.Combine(temp.Root, "game");
        Directory.CreateDirectory(destination);
        var gameFile = Path.Combine(destination, Path.GetFileName(lease.Frame.ImagePath));
        File.WriteAllText(gameFile, "game owned");
        Directory.Move(directory, original);
        Directory.CreateSymbolicLink(directory, destination);
        try
        {
            lease.Dispose();
            Assert.Equal("game owned", File.ReadAllText(gameFile));
            Assert.True(File.Exists(Path.Combine(original, Path.GetFileName(lease.Frame.ImagePath))));
        }
        finally { Directory.Delete(directory); }
    }

    [Fact]
    public void A_recorded_path_is_not_owned_until_CreateNew_has_succeeded()
    {
        using var temp = new IsolatedTemp();
        var directory = Path.Combine(temp.ExternalRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "already-existing.png");
        File.WriteAllText(path, "not ours");
        // Exercise the real cleanup state after allocation but before a failed CreateNew.
        // Reflection avoids a production test hook or probabilistic GUID collision.
        using var lease = (OwnedOcrFrame)Activator.CreateInstance(typeof(OwnedOcrFrame),
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            [directory, path, ExternalOcrTestDoubles.Frame(), 1], null)!;
        lease.Dispose();
        Assert.True(File.Exists(path));
        Assert.Equal("not ours", File.ReadAllText(path));
    }

    private sealed class ControlledMemory(Action onRead) : MemoryManager<byte>
    {
        private readonly byte[] _pixels = new byte[32 * 24 * 4];
        public override Span<byte> GetSpan() { onRead(); return _pixels; }
        public override Memory<byte> Memory => CreateMemory(_pixels.Length);
        public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();
        public override void Unpin() { }
        protected override void Dispose(bool disposing) { }
    }

    // This nonparallel collection makes temporary process environment changes isolated.
    private sealed class IsolatedTemp : IDisposable
    {
        private readonly string? _temp = Environment.GetEnvironmentVariable("TEMP");
        private readonly string? _tmp = Environment.GetEnvironmentVariable("TMP");
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "external-ownership-" + Guid.NewGuid().ToString("N"));
        internal string ExternalRoot => Path.Combine(Root, "UnityTranslator", "external-ocr");
        internal IsolatedTemp()
        {
            Directory.CreateDirectory(Root);
            Environment.SetEnvironmentVariable("TEMP", Root);
            Environment.SetEnvironmentVariable("TMP", Root);
            Assert.Equal(Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar), Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));
        }
        public void Dispose()
        {
            Environment.SetEnvironmentVariable("TEMP", _temp);
            Environment.SetEnvironmentVariable("TMP", _tmp);
            Directory.Delete(Root, true);
        }
    }
}
