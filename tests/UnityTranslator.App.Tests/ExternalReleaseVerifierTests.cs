using System.Diagnostics;
using System.Text.Json;

namespace UnityTranslator.App.Tests;

public sealed class ExternalReleaseVerifierTests
{
    // Break caught: passing the final decision when one expected report disappears,
    // even though every static check and every other runtime report is Passed.
    [Theory]
    [InlineData(null, false)]
    [InlineData("Failed", false)]
    [InlineData("NotAttempted", false)]
    [InlineData("EnvironmentUnverified", false)]
    [InlineData("Blocked", false)]
    [InlineData("Unknown", false)]
    [InlineData("Passed", true)]
    public void FinalDecisionIndependentlyRequiresEveryExpectedRuntimeReport(string? status, bool expected)
    {
        var runtime = new List<object> { new { Name = "FinalSolutionTests", Status = "Passed" } };
        if (status is not null) runtime.Add(new { Name = "PhysicalInputAndF9", Status = status });
        var decision = Decide(new[] { new { Name = "StaticFixture", Status = "Passed" } }, runtime,
            ["FinalSolutionTests", "PhysicalInputAndF9"]);
        Assert.Equal(expected, decision);
    }

    [Fact]
    public void FinalDecisionAlsoRequiresSuccessfulStaticChecks()
    {
        Assert.False(Decide(new[] { new { Name = "StaticFixture", Status = "Failed" } },
            new[] { new { Name = "FinalSolutionTests", Status = "Passed" } }, ["FinalSolutionTests"]));
    }

    [Fact]
    public void EmptyOrDuplicateEvidenceCannotPassByCountingOnlyTheSurvivors()
    {
        object[] checks = [new { Name = "StaticFixture", Status = "Passed" }];
        object[] duplicateReports = [new { Name = "FinalSolutionTests", Status = "Passed" }, new { Name = "FinalSolutionTests", Status = "Passed" }];
        Assert.False(Decide(checks, duplicateReports, ["FinalSolutionTests", "PhysicalInputAndF9"]));
        Assert.False(Decide(checks, Array.Empty<object>(), ["FinalSolutionTests"]));
        Assert.False(Decide(Array.Empty<object>(), new[] { new { Name = "FinalSolutionTests", Status = "Passed" } }, ["FinalSolutionTests"]));
        Assert.False(Decide(checks, Array.Empty<object>(), []));
    }

    [Fact]
    public void ManifestChecksEveryFileAndExcludesOnlyTheManifestItself()
    {
        InSandbox((release, previous, evidence) =>
        {
            File.WriteAllText(Path.Combine(release,"first.txt"),"abc");
            File.WriteAllText(Path.Combine(release,"second.txt"),"abc");
            var rows=new[]{"first.txt","second.txt"}.Select(name=>new {Path=name,Length=3,Sha256="BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD"});
            File.WriteAllText(Path.Combine(release,"release-manifest.json"),JsonSerializer.Serialize(rows));
            Run(release,previous,evidence);
            using var report=JsonDocument.Parse(File.ReadAllText(Path.Combine(evidence,"verification.json")));
            Assert.Contains(report.RootElement.GetProperty("StaticChecks").EnumerateArray(),item=>item.GetProperty("Name").GetString()=="FullPackageManifest"&&item.GetProperty("Status").GetString()=="Passed");
            using var inventory=JsonDocument.Parse(File.ReadAllText(Path.Combine(evidence,"all-files.json")));
            Assert.Equal(3,inventory.RootElement.GetArrayLength());
        });
    }

    [Fact]
    public void MissingRuntimeAndForegroundReportsRemainExplicitBlockingItems()
    {
        InSandbox((release, previous, evidence) =>
        {
            Run(release, previous, evidence);
            using var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(evidence, "verification.json")));
            Assert.False(report.RootElement.GetProperty("Passed").GetBoolean());
            var items = report.RootElement.GetProperty("RequiredEvidence").EnumerateArray().ToArray();
            foreach (var name in new[] { "PublishedDefaultStartup", "PublishedCaptureDependencyResolution", "PhysicalInputAndF9", "NaturalGameImageTextTwoIl2CppVersions", "OwnedResourceCleanup", "RealConfigurationUnchanged" })
                Assert.Contains(items, item => item.GetProperty("Name").GetString() == name && item.GetProperty("Status").GetString() == "NotAttempted");
        });
    }

    [Fact]
    public void ExistingEvidenceCannotBeOverwritten()
    {
        InSandbox((release, previous, evidence) =>
        {
            Directory.CreateDirectory(evidence);
            var report = Path.Combine(evidence, "verification.json");
            File.WriteAllText(report, "existing evidence");
            Assert.NotEqual(0, Run(release, previous, evidence));
            Assert.Equal("existing evidence", File.ReadAllText(report));
        });
    }

    // Break caught: dropping the paired visibility/isolation gate or losing its explicit blocking status.
    // Other static checks intentionally fail in this small fixture; this does not isolate the full conjunction.
    [Theory]
    [InlineData(null, "NotAttempted")]
    [InlineData("Failed", "Failed")]
    public void RemoteOverlayVisibilityAndTargetIsolationRemainsRequired(string? suppliedStatus, string expectedStatus)
    {
        InSandbox((release, previous, evidence) =>
        {
            if (suppliedStatus is not null)
                File.WriteAllText(Path.Combine(release, "release-evidence.json"), JsonSerializer.Serialize(new
                {
                    SchemaVersion = 1,
                    CandidateExeSha256 = "unbound-test-fixture",
                    RequiredEvidence = new[] { new { Name = "RemoteOverlayVisibilityAndTargetIsolation", Status = suppliedStatus, Detail = "Paired capture failed." } }
                }));
            Assert.Equal(2, Run(release, previous, evidence));
            using var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(evidence, "verification.json")));
            var row = Assert.Single(report.RootElement.GetProperty("RequiredEvidence").EnumerateArray(),
                item => item.GetProperty("Name").GetString() == "RemoteOverlayVisibilityAndTargetIsolation");
            Assert.Equal(expectedStatus, row.GetProperty("Status").GetString());
            Assert.False(report.RootElement.GetProperty("Passed").GetBoolean());
            Assert.False(report.RootElement.GetProperty("DeliveryAllowed").GetBoolean());
        });
    }

    private static int Run(string release, string previous, string evidence)
    {
        var root = SourceRoot();
        var pwsh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache/codex-runtimes/codex-primary-runtime/dependencies/native/powershell/pwsh.exe");
        var start = new ProcessStartInfo(pwsh) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-File", Path.Combine(root, "packaging/verify-external-ocr.ps1"), "-ReleaseRoot", release, "-PreviousRelease", previous, "-EvidenceRoot", evidence }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        return TestProcessRunner.WaitForExit(process).ExitCode;
    }

    private static bool Decide(object checks, object reports, string[] expectedNames)
    {
        var script = Path.Combine(SourceRoot(), "packaging/Test-ExternalReleaseDecision.ps1").Replace("'", "''");
        string JsonLiteral(object value) => JsonSerializer.Serialize(value).Replace("'", "''");
        var command = $"$ErrorActionPreference='Stop'; $checks=ConvertFrom-Json -InputObject '{JsonLiteral(checks)}'; $reports=ConvertFrom-Json -InputObject '{JsonLiteral(reports)}'; $names=ConvertFrom-Json -InputObject '{JsonLiteral(expectedNames)}'; & '{script}' -StaticChecks @($checks) -RequiredEvidence @($reports) -ExpectedEvidenceNames @($names) | ConvertTo-Json -Compress";
        using var process = Process.Start(TestProcessRunner.PowerShell(command))!;
        var result = TestProcessRunner.WaitForExit(process);
        Assert.True(result.ExitCode == 0, result.Stderr);
        return JsonSerializer.Deserialize<bool>(result.Stdout.Trim());
    }

    private static string SourceRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "UnityTranslator.sln"))) root = root.Parent;
        Assert.NotNull(root);
        return root!.FullName;
    }

    private static void InSandbox(Action<string, string, string> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "vesper-verifier-tests-" + Guid.NewGuid());
        var release = Directory.CreateDirectory(Path.Combine(root, "release")).FullName;
        var previous = Directory.CreateDirectory(Path.Combine(root, "previous")).FullName;
        try { action(release, previous, Path.Combine(root, "evidence")); }
        finally { Directory.Delete(root, true); }
    }
}
