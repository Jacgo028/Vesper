using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using UnityTranslator.Core;

namespace UnityTranslator.App;

public interface IOcrRecognizer
{
    Task<IReadOnlyList<OcrBox>> RecognizeAsync(string imagePath, CancellationToken cancellationToken);
}

public sealed class OcrWorkerHost(string runtimeDirectory) : IOcrRecognizer
{
    private readonly string _executable = Path.Combine(runtimeDirectory, "tesseract.exe");
    private readonly string _data = Path.Combine(runtimeDirectory, "tessdata");

    public bool IsAvailable => File.Exists(_executable) &&
                               File.Exists(Path.Combine(_data, "jpn.traineddata")) &&
                               File.Exists(Path.Combine(_data, "eng.traineddata"));

    public static void DeleteCapturedFrame(string imagePath)
    {
        try
        {
            var image = Path.GetFullPath(imagePath);
            var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "UnityTranslator")) + Path.DirectorySeparatorChar;
            if (image.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) && File.Exists(image)) File.Delete(image);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
    }

    public async Task<IReadOnlyList<OcrBox>> RecognizeAsync(string imagePath, CancellationToken cancellationToken)
    {
        if (!IsAvailable) throw new FileNotFoundException("本机画面识别组件未安装", _executable);
        var image = Path.GetFullPath(imagePath);
        var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "UnityTranslator")) + Path.DirectorySeparatorChar;
        if (!image.StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("截图位置不在翻译器临时文件夹内", nameof(imagePath));
        if (!File.Exists(image)) throw new FileNotFoundException("截图不存在", image);

        var start = new ProcessStartInfo(_executable)
        {
            WorkingDirectory = Path.GetDirectoryName(_executable)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in new[] { image, "stdout", "--tessdata-dir", _data, "-l", "jpn+eng", "--psm", "11", "tsv" })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("无法启动本机识别组件");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errors = process.StandardError.ReadToEndAsync(cancellationToken);
        try { await process.WaitForExitAsync(cancellationToken); }
        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(); throw; }
        if (process.ExitCode != 0) throw new IOException("画面识别失败：" + (await errors).Trim());
        return ParseTsv(await output);
    }

    public static IReadOnlyList<OcrBox> ParseTsv(string tsv)
    {
        var words = new Dictionary<string, List<OcrBox>>();
        foreach (var line in tsv.Split('\n').Skip(1))
        {
            var fields = line.TrimEnd('\r').Split('\t', 12);
            if (fields.Length != 12 || fields[0] != "5" || string.IsNullOrWhiteSpace(fields[11])) continue;
            if (!int.TryParse(fields[6], out var x) || !int.TryParse(fields[7], out var y) ||
                !int.TryParse(fields[8], out var width) || !int.TryParse(fields[9], out var height) ||
                !float.TryParse(fields[10], NumberStyles.Float, CultureInfo.InvariantCulture, out var conf)) continue;
            var key = string.Join(":", fields[1], fields[2], fields[3], fields[4]);
            if (!words.TryGetValue(key, out var row)) words[key] = row = [];
            row.Add(new OcrBox(x, y, width, height, fields[11].Trim(), conf / 100f));
        }

        return words.Values.Select(row =>
        {
            var japanese = row.Any(word => word.Text.Any(c => c >= '\u3040' && c <= '\u30ff'));
            var text = string.Join(japanese ? "" : " ", row.Select(word => word.Text));
            var x = row.Min(word => word.X);
            var y = row.Min(word => word.Y);
            return new OcrBox(x, y, row.Max(word => word.X + word.Width) - x,
                row.Max(word => word.Y + word.Height) - y, text, row.Min(word => word.Confidence));
        }).ToArray();
    }
}
