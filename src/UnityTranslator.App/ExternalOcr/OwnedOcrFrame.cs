using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UnityTranslator.Core;

namespace UnityTranslator.App.ExternalOcr;

/// <summary>Owns exactly one app-created PNG. Never adopts or cleans game-owned frames.</summary>
internal sealed class OwnedOcrFrame : IDisposable
{
    private readonly string _directory;
    private bool _ownsFile;
    private int _disposed;
    public OcrFrame Frame { get; }

    private OwnedOcrFrame(string directory, string path, CapturedClientFrame frame, int frameId)
    {
        _directory = directory;
        Frame = new(frameId, path, frame.Width, frame.Height, []);
    }

    internal static Task<OwnedOcrFrame> CreateAsync(CapturedClientFrame frame, int frameId, CancellationToken ct) =>
        Task.Run(() => Create(frame, frameId, ct), ct);

    private static OwnedOcrFrame Create(CapturedClientFrame frame, int frameId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var length = (long)frame.Width * frame.Height * 4;
        if (frameId <= 0 || frame.Width <= 0 || frame.Height <= 0 || length > int.MaxValue ||
            frame.Bgra32.Length != length)
            throw new ArgumentException("A tightly packed positive-size BGRA frame is required.", nameof(frame));
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "UnityTranslator", "external-ocr"));
        var directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".png");
        EnsureOrdinaryAncestors(directory);
        Directory.CreateDirectory(directory);
        var lease = new OwnedOcrFrame(directory, path, frame, frameId);
        try
        {
            EnsureOrdinaryAncestors(directory);
            ct.ThrowIfCancellationRequested();
            var bitmap = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32,
                null, frame.Bgra32.ToArray(), checked(frame.Width * 4));
            bitmap.Freeze();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                lease._ownsFile = true;
                encoder.Save(output);
            }
            ct.ThrowIfCancellationRequested();
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            EnsureOrdinaryAncestors(_directory);
            if (_ownsFile && File.Exists(Frame.ImagePath))
            {
                if ((File.GetAttributes(Frame.ImagePath) & FileAttributes.ReparsePoint) != 0) return;
                File.Delete(Frame.ImagePath);
            }
            // No enumeration/deletion of other files, and never recursive directory removal.
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void EnsureOrdinaryAncestors(string directory)
    {
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("External OCR temporary directories must not contain reparse points.");
    }
}
