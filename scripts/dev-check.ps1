<#
.SYNOPSIS
    Development helper: builds the tests, prints compile errors compactly, then runs the (filtered) tests
    and prints each failure with its message.
.PARAMETER Filter
    Optional test filter, for example "FullyQualifiedName~Workspace".
.PARAMETER HangSeconds
    A test that runs longer than this ends the run and is named, instead of blocking it.
#>
[CmdletBinding()]
param(
    [string]$Filter,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [int]$MaxFailures = 15,
    [int]$HangSeconds = 120,
    [int]$MessageLines = 40
)

. "$PSScriptRoot\env.ps1"

$project = Join-Path $RepoRoot 'tests\Yav.Tests\Yav.Tests.csproj'

$build = & $DotNet build $project -c $Configuration --nologo -v quiet -consoleLoggerParameters:NoSummary 2>&1 | Out-String -Width 4000
if ($LASTEXITCODE -ne 0) {
    $flat = $build -replace "`r?`n\s{2,}", ' '
    $errors = @(($flat -split "`r?`n") | Where-Object { $_ -match '(error|warning) [A-Z]+\d+' } | ForEach-Object {
        $line = $_
        $start = $line.IndexOf('YAV Shell\')
        if ($start -ge 0) { $line = $line.Substring($start + 10) }
        $end = $line.IndexOf(' [D:')
        if ($end -gt 0) { $line = $line.Substring(0, $end) }
        $line
    } | Select-Object -Unique)
    if ($errors.Count -eq 0) { Write-Host $build }
    Write-Host "BUILD FAILED ($($errors.Count) distinct message(s))" -ForegroundColor Red
    $errors | Select-Object -First 40 | ForEach-Object { Write-Host "  $_" }
    exit 2
}

$results = Join-Path $RepoRoot 'artifacts\test-results'
$trx = Join-Path $results 'dev-check.trx'
Remove-Item $trx -ErrorAction SilentlyContinue
$arguments = @('test', $project, '-c', $Configuration, '--no-build', '--nologo', '-v', 'quiet',
    '--logger', 'trx;LogFileName=dev-check.trx', '--results-directory', $results,
    '--blame-hang-timeout', "${HangSeconds}s", '--blame-hang-dump-type', 'none')
$filters = @('(Category!=Live)')
if ($Filter) { $filters += "($Filter)" }
$arguments += @('--filter', ($filters -join '&'))

$startedAt = Get-Date
$output = & $DotNet @arguments 2>&1 | Out-String -Width 4000
$code = $LASTEXITCODE

if (Test-Path $trx) {
    [xml]$xml = Get-Content $trx -Raw
    $counters = $xml.TestRun.ResultSummary.Counters
    $color = if ($counters.failed -eq '0') { 'Green' } else { 'Red' }
    Write-Host ("TESTS  total {0}  passed {1}  failed {2}  skipped {3}" -f $counters.total, $counters.passed, $counters.failed, ([int]$counters.total - [int]$counters.executed)) -ForegroundColor $color
    $failed = @($xml.TestRun.Results.UnitTestResult | Where-Object { $_.outcome -eq 'Failed' })
    foreach ($result in ($failed | Select-Object -First $MaxFailures)) {
        Write-Host ''
        Write-Host ("FAIL  {0}" -f $result.testName) -ForegroundColor Red
        $message = [string]$result.Output.ErrorInfo.Message
        ($message -split "`r?`n" | Select-Object -First $MessageLines) | ForEach-Object { Write-Host "    $_" }
        $stack = [string]$result.Output.ErrorInfo.StackTrace
        ($stack -split "`r?`n" | Where-Object { $_ -match 'Yav\.' } | Select-Object -First 4) | ForEach-Object {
            Write-Host ("    " + ($_ -replace ' in .*YAV Shell\\', ' in ').Trim())
        }
    }
    if ($failed.Count -gt $MaxFailures) { Write-Host ("... and {0} more failure(s)" -f ($failed.Count - $MaxFailures)) }
}
else {
    Write-Host $output
}

if ($output -match 'blame|aborted|hang') {
    $sequence = Get-ChildItem $results -Recurse -Filter 'Sequence_*.xml' -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTime -ge $startedAt } | Sort-Object LastWriteTime | Select-Object -Last 1
    if ($sequence) {
        [xml]$blame = Get-Content $sequence.FullName -Raw
        $running = @($blame.TestSequence.Test | Where-Object { $_.Completed -eq 'False' })
        if ($running.Count -gt 0) {
            Write-Host ''
            Write-Host "HANG  these tests were still running after ${HangSeconds}s:" -ForegroundColor Red
            $running | ForEach-Object { Write-Host "    $($_.Name)" }
        }
    }
}

exit $code
