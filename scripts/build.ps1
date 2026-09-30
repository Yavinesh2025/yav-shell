<#
.SYNOPSIS
    Builds every YAV Shell project.
.PARAMETER Configuration
    Debug or Release. Default: Debug.
.PARAMETER Project
    Optional path of a single project to build instead of the whole solution.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [string]$Project
)

. "$PSScriptRoot\env.ps1"

$target = if ($Project) { $Project } else { Join-Path $RepoRoot 'YavShell.slnx' }
& $DotNet build $target -c $Configuration --nologo -v minimal -consoleLoggerParameters:NoSummary
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }
