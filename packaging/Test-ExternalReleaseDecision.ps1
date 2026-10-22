param(
    [AllowEmptyCollection()][object[]]$StaticChecks,
    [AllowEmptyCollection()][object[]]$RequiredEvidence,
    [AllowEmptyCollection()][string[]]$ExpectedEvidenceNames
)
# Called only after the checker validates candidate/artifact identities. Missing,
# duplicate or non-passing obligations cannot be turned into an all-passed subset.
if (@($StaticChecks).Count -eq 0 -or @($ExpectedEvidenceNames).Count -eq 0 -or
    @($StaticChecks | Where-Object Status -ne 'Passed').Count -ne 0 -or
    @($RequiredEvidence).Count -ne @($ExpectedEvidenceNames).Count -or
    @($ExpectedEvidenceNames | Select-Object -Unique).Count -ne @($ExpectedEvidenceNames).Count) { return $false }
foreach ($name in $ExpectedEvidenceNames) {
    if ([string]::IsNullOrWhiteSpace($name)) { return $false }
    $matches = @($RequiredEvidence | Where-Object Name -eq $name)
    if ($matches.Count -ne 1 -or $matches[0].Status -ne 'Passed') { return $false }
}
return $true
