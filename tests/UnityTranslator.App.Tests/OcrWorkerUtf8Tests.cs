using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit.Abstractions;

namespace UnityTranslator.App.Tests;

public sealed class OcrWorkerUtf8Tests(ITestOutputHelper output)
{
    // Removing stdout decoding must corrupt Japanese or TSV row boundaries on non-UTF8 hosts.
    [Fact]
    public async Task RecognizeAsyncPreservesUtf8TextRowsRectanglesAndConfidenceWithoutConsole()
    {
        using var result = await RunWorker("success");
        var boxes = result.RootElement.GetProperty("boxes").EnumerateArray().ToArray();
        Assert.Equal(new[] { "かなカナ", "世界へようこそ", "HELLO WORLD" },
            boxes.Select(box => box.GetProperty("Text").GetString()));
        AssertBox(boxes[0], 10, 10, 100, 20, 0.99f);
        AssertBox(boxes[1], 10, 40, 180, 20, 0.98f);
        AssertBox(boxes[2], 10, 70, 130, 20, 0.96f);
        Assert.Equal(JsonValueKind.Null, result.RootElement.GetProperty("error").ValueKind);
    }

    // Removing stderr decoding must lose the meaningful Japanese worker failure.
    [Fact]
    public async Task RecognizeAsyncPreservesUtf8JapaneseStderrInIOExceptionWithoutConsole()
    {
        using var result = await RunWorker("error");
        Assert.Equal("IOException", result.RootElement.GetProperty("errorType").GetString());
        Assert.Contains("認識失敗：日本語のエラー", result.RootElement.GetProperty("error").GetString());
        Assert.Equal(JsonValueKind.Null, result.RootElement.GetProperty("boxes").ValueKind);
    }

    private static void AssertBox(JsonElement box, int x, int y, int width, int height, float confidence)
    {
        Assert.Equal(x, box.GetProperty("X").GetInt32());
        Assert.Equal(y, box.GetProperty("Y").GetInt32());
        Assert.Equal(width, box.GetProperty("Width").GetInt32());
        Assert.Equal(height, box.GetProperty("Height").GetInt32());
        Assert.Equal(confidence, box.GetProperty("Confidence").GetSingle());
    }

    private async Task<JsonDocument> RunWorker(string mode)
    {
        using var lease = new FixtureLease();
        output.WriteLine("lease: " + lease.Root);
        var input = lease.AddFile("input.NOT-AN-IMAGE", "test sentinel, never an image");
        var fixture = Path.Combine(AppContext.BaseDirectory, "OcrWorkerUtf8Fixture");
        RejectReparseAncestors(fixture);
        foreach (var source in Directory.GetFiles(fixture)) lease.CopyFile(source, Path.GetFileName(source));
        lease.CopyFile(Path.Combine(fixture, "OcrWorkerUtf8Fixture.exe"), "tesseract.exe");
        lease.AddDirectory("tessdata");
        lease.AddFile(Path.Combine("tessdata", "jpn.traineddata"), "test sentinel");
        lease.AddFile(Path.Combine("tessdata", "eng.traineddata"), "test sentinel");
        var start = new ProcessStartInfo(Path.Combine(lease.Root, "OcrWorkerUtf8Fixture.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = lease.Root
        };
        foreach (var argument in new[] { "parent", lease.Root, input }) start.ArgumentList.Add(argument);
        start.Environment["OCR_UTF8_FIXTURE_INPUT"] = input;
        start.Environment["OCR_UTF8_FIXTURE_MODE"] = mode;
        using var process = Process.Start(start) ?? throw new IOException("Fixture did not start");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(deadline.Token); }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.True(process.ExitCode == 0, $"Fixture exit {process.ExitCode}: {await stderr}");
        Assert.Equal("", await stderr);
        var json = await stdout;
        output.WriteLine(json);
        var result = JsonDocument.Parse(json);
        Assert.Equal(0u, result.RootElement.GetProperty("consoleOutputCodePage").GetUInt32());
        return result;
    }

    private static void RejectReparseAncestors(string path)
    {
        for (var item = new DirectoryInfo(Path.GetFullPath(path)); item is not null; item = item.Parent)
            if (item.Exists && (item.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Reparse path rejected: {item.FullName}");
    }

    private sealed class FixtureLease : IDisposable
    {
        public string Root { get; } = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "UnityTranslator", "ocr-utf8-" + Guid.NewGuid().ToString("N")));
        private readonly List<string> files = [];
        private readonly List<string> directories = [];

        public FixtureLease()
        {
            if (!Path.IsPathFullyQualified(Root) || Root.StartsWith(@"\\", StringComparison.Ordinal))
                throw new IOException("Lease must be an absolute local path");
            RejectReparseAncestors(Root);
            if (Directory.Exists(Root)) throw new IOException("Lease already exists");
            Directory.CreateDirectory(Root);
            directories.Add(Root);
        }

        public void AddDirectory(string relative)
        {
            var path = Path.Combine(Root, relative);
            Directory.CreateDirectory(path);
            directories.Add(path);
        }

        public string AddFile(string relative, string text)
        {
            var path = Path.Combine(Root, relative);
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            files.Add(path);
            stream.Write(Encoding.UTF8.GetBytes(text));
            return path;
        }

        public void CopyFile(string source, string relative)
        {
            RejectReparseAncestors(source);
            var path = Path.Combine(Root, relative);
            using var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            files.Add(path);
            using var original = File.OpenRead(source);
            original.CopyTo(destination);
        }

        public void Dispose()
        {
            RejectReparseAncestors(Root);
            foreach (var file in files.AsEnumerable().Reverse()) File.Delete(file);
            foreach (var directory in directories.AsEnumerable().Reverse()) Directory.Delete(directory, recursive: false);
        }
    }
}
