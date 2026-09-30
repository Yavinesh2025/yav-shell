<#
.SYNOPSIS
    Writes a summary of the last complete test run: how many tests each class has and how they ended.
.DESCRIPTION
    Reads the results that scripts\dev-check.ps1 or scripts\test.ps1 left in artifacts\test-results and
    writes docs\test-results.md. Nothing is run.
.PARAMETER Results
    The .trx file to read. Default: the newest one in artifacts\test-results.
#>
[CmdletBinding()]
param(
    [string]$Results
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not $Results) {
    $Results = Get-ChildItem -LiteralPath (Join-Path $repo 'artifacts\test-results') -Filter '*.trx' |
        Sort-Object LastWriteTime | Select-Object -Last 1 -ExpandProperty FullName
}

if (-not $Results -or -not (Test-Path -LiteralPath $Results)) { throw 'No test results were found. Run scripts\test.ps1 first.' }

[xml]$xml = Get-Content -LiteralPath $Results -Raw
$counters = $xml.TestRun.ResultSummary.Counters
$times = $xml.TestRun.Times
$started = [DateTimeOffset]::Parse($times.start, [Globalization.CultureInfo]::InvariantCulture)
$finished = [DateTimeOffset]::Parse($times.finish, [Globalization.CultureInfo]::InvariantCulture)

$classOf = @{}
foreach ($test in $xml.TestRun.TestDefinitions.UnitTest) { $classOf[$test.id] = $test.TestMethod.className }

$classes = @{}
foreach ($result in $xml.TestRun.Results.UnitTestResult) {
    $name = $classOf[$result.testId]
    if (-not $classes.ContainsKey($name)) { $classes[$name] = [pscustomobject]@{ Name = $name; Passed = 0; Failed = 0; Other = 0 } }
    switch ($result.outcome) {
        'Passed' { $classes[$name].Passed++ }
        'Failed' { $classes[$name].Failed++ }
        default { $classes[$name].Other++ }
    }
}

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add('# Test results')
$lines.Add('')
$lines.Add("The complete run of the automated tests on $($started.ToUniversalTime().ToString('yyyy-MM-dd HH:mm')) UTC, written by ``scripts\summarize-tests.ps1``.")
$lines.Add('The agents were the scripted stand-in: no test sends a request to a model. The tests that talk to the')
$lines.Add('installed agents are not part of this run; `docs\verification.md` says what they found.')
$lines.Add('')
$lines.Add('| | |')
$lines.Add('|---|---:|')
$lines.Add("| Tests | $($counters.total) |")
$lines.Add("| Passed | $($counters.passed) |")
$lines.Add("| Failed | $($counters.failed) |")
$lines.Add("| Not run | $([int]$counters.total - [int]$counters.executed) |")
$lines.Add("| Time | $([math]::Round(($finished - $started).TotalMinutes, 1)) minutes |")
$lines.Add('')
$lines.Add('| Class | Passed | Failed |')
$lines.Add('|---|---:|---:|')
foreach ($class in ($classes.Values | Sort-Object Name)) {
    $short = $class.Name -replace '^Yav\.Tests\.', ''
    $lines.Add("| $short | $($class.Passed) | $($class.Failed + $class.Other) |")
}

$target = Join-Path $repo 'docs\test-results.md'
[IO.File]::WriteAllLines($target, $lines, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "$($counters.passed) of $($counters.total) passed. Written to $target"
