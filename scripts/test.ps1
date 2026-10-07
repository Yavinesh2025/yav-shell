<#
.SYNOPSIS
    Builds YAV Shell and runs the automated tests.
.DESCRIPTION
    These are fixture tests: they use a scripted stand-in for the agent CLIs and never send an inference
    request. Tests that talk to a real agent are opt-in (-Live) and only perform handshakes that the
    providers serve without inference.
.PARAMETER Filter
    Optional test filter, for example "FullyQualifiedName~ProcessRunner".
.PARAMETER Live
    Also runs the live handshake tests against the agent CLIs installed on this machine. The parts of a live run
    (LiveRunTests) are never run from here: they are parts of scripts\live-run.ps1, not tests of the product.
.PARAMETER HangSeconds
    When no test has begun or ended for that many seconds, the test platform ends the run and names the tests
    that were still running, instead of the run waiting for whatever ends it from outside. 0, the default, sets
    no limit.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [string]$Filter,
    [switch]$Live,
    [ValidateRange(0, 86400)]
    [int]$HangSeconds = 0
)

function Get-TestFilter {
    <#
        The filter of the tests to run. The parts of a live run are left out also with -Live: the part that continues
        a run asks the models, and only scripts\live-run.ps1 starts it, after it checked the authorization.
    #>
    param([string]$Filter, [switch]$Live)

    $filters = @()
    if ($Filter) { $filters += "($Filter)" }
    if (-not $Live) { $filters += '(Category!=Live)' }
    $filters += '(FullyQualifiedName!~Yav.Tests.Live.LiveRunTests.)'
    return $filters -join '&'
}

function Get-HangArguments {
    <#
        What tells the test platform to end a run in which no test has begun or ended for that many seconds. Its
        blame collector names the tests that were still running; no dump of memory is written. Nothing for 0.
    #>
    param([int]$HangSeconds)

    if ($HangSeconds -le 0) { return @() }
    return @('--blame-hang-timeout', "${HangSeconds}s", '--blame-hang-dump-type', 'none')
}

. "$PSScriptRoot\env.ps1"

$project = Join-Path $RepoRoot 'tests\Yav.Tests\Yav.Tests.csproj'
$results = Join-Path $RepoRoot 'artifacts\test-results'
$arguments = @('test', $project, '-c', $Configuration, '--nologo', '-v', 'minimal',
    '--logger', 'trx;LogFileName=yav-tests.trx', '--results-directory', $results)
$arguments += @(Get-HangArguments -HangSeconds $HangSeconds)
$arguments += @('--filter', (Get-TestFilter -Filter $Filter -Live:$Live))

if ($Live) { $env:YAV_LIVE_TESTS = '1' } else { Remove-Item Env:\YAV_LIVE_TESTS -ErrorAction SilentlyContinue }

& $DotNet @arguments
$code = $LASTEXITCODE
if ($code -ne 0) { throw "Tests failed with exit code $code." }
