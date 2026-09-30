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
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [string]$Filter,
    [switch]$Live
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

. "$PSScriptRoot\env.ps1"

$project = Join-Path $RepoRoot 'tests\Yav.Tests\Yav.Tests.csproj'
$results = Join-Path $RepoRoot 'artifacts\test-results'
$arguments = @('test', $project, '-c', $Configuration, '--nologo', '-v', 'minimal',
    '--logger', 'trx;LogFileName=yav-tests.trx', '--results-directory', $results)
$arguments += @('--filter', (Get-TestFilter -Filter $Filter -Live:$Live))

if ($Live) { $env:YAV_LIVE_TESTS = '1' } else { Remove-Item Env:\YAV_LIVE_TESTS -ErrorAction SilentlyContinue }

& $DotNet @arguments
$code = $LASTEXITCODE
if ($code -ne 0) { throw "Tests failed with exit code $code." }
