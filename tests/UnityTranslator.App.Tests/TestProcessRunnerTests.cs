using System.Diagnostics;

namespace UnityTranslator.App.Tests;

public sealed class TestProcessRunnerTests
{
    // Break caught: timing out and returning while the exact test-owned child is still alive.
    [Fact]
    public async Task TimeoutStopsOwnedChildBeforeReturningAndPreservesCapturedDiagnostics()
    {
        using var process = Process.Start(TestProcessRunner.PowerShell("[Console]::Error.WriteLine('captured-error'); [Console]::Out.WriteLine('ready'); [Console]::Out.WriteLine('captured-out'); Start-Sleep -Seconds 60"))!;
        try
        {
            Assert.Equal("ready", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            var error = Assert.Throws<TimeoutException>(() => TestProcessRunner.WaitForExit(process, 100));
            Assert.True(process.HasExited, "The timeout returned before its owned child stopped.");
            Assert.Contains("captured-out", error.Message);
            Assert.Contains("captured-error", error.Message);
        }
        finally
        {
            // Keep the RED run safe even before the utility has been repaired.
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public void NormalNonzeroExitAndBothOutputStreamsAreReturned()
    {
        using var process = Process.Start(TestProcessRunner.PowerShell("[Console]::Out.WriteLine('normal-out'); [Console]::Error.WriteLine('normal-error'); exit 7"))!;
        var result = TestProcessRunner.WaitForExit(process, 5000);
        Assert.Equal(7, result.ExitCode);
        Assert.Contains("normal-out", result.Stdout);
        Assert.Contains("normal-error", result.Stderr);
    }

}
