using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using UnityTranslator.App;
using UnityTranslator.Core;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args is ["parent", var runtime, var input])
        {
            IReadOnlyList<OcrBox>? boxes = null;
            string? error = null;
            string? errorType = null;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { boxes = await new OcrWorkerHost(runtime).RecognizeAsync(input, deadline.Token); }
            catch (IOException ex) { error = ex.Message; errorType = ex.GetType().Name; }
            await WriteBytes(Console.OpenStandardOutput(), JsonSerializer.Serialize(new
            {
                boxes, error, errorType, pid = Environment.ProcessId,
                consoleOutputCodePage = GetConsoleOutputCP(), acp = GetACP(),
                framework = RuntimeInformation.FrameworkDescription
            }));
            return 0;
        }

        var expectedInput = Environment.GetEnvironmentVariable("OCR_UTF8_FIXTURE_INPUT");
        string[] expected = [expectedInput!, "stdout", "--tessdata-dir",
            Path.Combine(AppContext.BaseDirectory, "tessdata"), "-l", "jpn+eng", "--psm", "11", "tsv"];
        if (expectedInput is null || !args.SequenceEqual(expected))
        {
            await WriteBytes(Console.OpenStandardError(), "Invalid test worker arguments");
            return 90;
        }
        if (Environment.GetEnvironmentVariable("OCR_UTF8_FIXTURE_MODE") == "error")
        {
            await WriteBytes(Console.OpenStandardError(), "認識失敗：日本語のエラー\n");
            return 23;
        }

        // Hand-written BOMless UTF-8 bytes: the input sentinel is never read as an image.
        const string tsv = "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext\n" +
            "5\t1\t1\t1\t1\t1\t10\t10\t100\t20\t99\tかなカナ\n" +
            "5\t1\t1\t1\t2\t1\t10\t40\t180\t20\t98\t世界へようこそ\n" +
            "5\t1\t1\t1\t3\t1\t10\t70\t60\t20\t97\tHELLO\n" +
            "5\t1\t1\t1\t3\t2\t80\t70\t60\t20\t96\tWORLD\n";
        await WriteBytes(Console.OpenStandardOutput(), tsv);
        return 0;
    }

    private static async Task WriteBytes(Stream stream, string text)
    {
        await using (stream) { await stream.WriteAsync(new UTF8Encoding(false).GetBytes(text)); }
    }

    [DllImport("kernel32.dll")] private static extern uint GetConsoleOutputCP();
    [DllImport("kernel32.dll")] private static extern uint GetACP();
}
