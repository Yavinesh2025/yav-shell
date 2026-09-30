# Shared setup for the build scripts. Dot-source it:  . "$PSScriptRoot\env.ps1"
# Finds a .NET SDK that satisfies global.json and keeps build output out of Dropbox sync.

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$script:RepoRoot = Split-Path -Parent $PSScriptRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

function Find-DotNetSdk {
    $required = (Get-Content (Join-Path $script:RepoRoot 'global.json') -Raw | ConvertFrom-Json).sdk.version
    $requiredBand = ($required -split '\.')[0..1] -join '.'
    $candidates = @()
    $onPath = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($onPath) { $candidates += $onPath.Source }
    $candidates += (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe')
    $candidates += (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe')

    foreach ($candidate in ($candidates | Select-Object -Unique)) {
        if (-not (Test-Path $candidate)) { continue }
        $sdks = & $candidate --list-sdks 2>$null
        if ($LASTEXITCODE -ne 0 -or -not $sdks) { continue }
        if ($sdks | Where-Object { $_ -like "$requiredBand.*" }) {
            return $candidate
        }
    }

    throw @"
No .NET $requiredBand SDK was found (global.json asks for $required).
Install it for the current user, without administrator rights:
    Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1 -OutFile dotnet-install.ps1
    .\dotnet-install.ps1 -Version $required -InstallDir "`$env:LOCALAPPDATA\Microsoft\dotnet" -NoPath
or machine-wide:
    winget install Microsoft.DotNet.SDK.10
"@
}

function Protect-BuildOutputFromSync {
    # Build output is reproducible and large. Marking the folders with Dropbox's documented
    # ignore attribute keeps them local. The attribute is harmless when Dropbox is not installed.
    foreach ($name in 'artifacts', 'dist') {
        $path = Join-Path $script:RepoRoot $name
        if (-not (Test-Path $path)) { New-Item -ItemType Directory -Path $path | Out-Null }
        try { Set-Content -Path $path -Stream com.dropbox.ignored -Value 1 -ErrorAction Stop } catch { }
    }
}

$script:DotNet = Find-DotNetSdk
$env:DOTNET_ROOT = Split-Path -Parent $script:DotNet
Protect-BuildOutputFromSync
