using System.Diagnostics;

namespace UnityTranslator.App.Tests;

// Test-only utility. The caller supplies a process it just started and owns.
internal static class TestProcessRunner
{
    internal sealed record Result(int ExitCode, string Stdout, string Stderr);

    internal static ProcessStartInfo PowerShell(string command)
    {
        var pwsh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache/codex-runtimes/codex-primary-runtime/dependencies/native/powershell/pwsh.exe");
        var start = new ProcessStartInfo(pwsh) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-Command", command }) start.ArgumentList.Add(arg);
        return start;
    }

    internal static Result WaitForExit(Process ownedProcess, int timeoutMilliseconds = 30000)
    {
        var stdout = ownedProcess.StandardOutput.ReadToEndAsync();
        var stderr = ownedProcess.StandardError.ReadToEndAsync();
        bool timedOut;
        try { timedOut = !ownedProcess.WaitForExit(timeoutMilliseconds); }
        finally
        {
            if (!ownedProcess.HasExited)
            {
                try { ownedProcess.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (ownedProcess.HasExited) { }
                if (!ownedProcess.WaitForExit(5000))
                    throw new TimeoutException("Test-owned child did not stop; cleanup is incomplete.");
            }
        }
        Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        if (timedOut)
            throw new TimeoutException($"Test-owned process timed out and was stopped.\nstdout: {Clip(stdout.Result)}\nstderr: {Clip(stderr.Result)}");
        return new(ownedProcess.ExitCode, stdout.Result, stderr.Result);
    }

    private static string Clip(string value) => value.Length <= 4000 ? value : value[..4000];
}
