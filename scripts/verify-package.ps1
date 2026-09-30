<#
.SYNOPSIS
    Checks the package the way a machine without .NET, Git and agents would use it.
.DESCRIPTION
    Unpacks the portable package into a new directory and starts yav.exe with an environment that
    contains nothing but Windows itself: no .NET, no Git, no agent on PATH, and a data directory of
    its own. It then installs the package into another new directory with install.ps1, starts what
    was installed, and removes it with uninstall.ps1.

    With -Sandbox the same is done inside Windows Sandbox: a new, disposable Windows without any of
    the software of this machine. That needs the Windows feature "Windows Sandbox" and opens its window.

    Nothing of this machine is changed: PATH, the Start menu and "Installed apps" are left alone.
.PARAMETER Package
    The portable zip. Default: the newest one in dist\.
.PARAMETER Sandbox
    Run the check in Windows Sandbox instead of on this machine.
.PARAMETER TimeoutMinutes
    How long to wait for Windows Sandbox.
#>
[CmdletBinding()]
param(
    [string]$Package,
    [switch]$Sandbox,
    [int]$TimeoutMinutes = 12
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path -Parent $PSScriptRoot
. (Join-Path $repo 'installer\YavInstall.ps1')

if (-not $Package) {
    $Package = Get-ChildItem (Join-Path $repo 'dist') -Filter 'yav-shell-*-portable.zip' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime | Select-Object -Last 1 -ExpandProperty FullName
}

if (-not $Package -or -not (Test-Path -LiteralPath $Package)) {
    throw 'No package was found in dist\. Build it with scripts\package.ps1.'
}

$Package = (Resolve-Path -LiteralPath $Package).ProviderPath

$expected = ((Get-Content -LiteralPath "$Package.sha256" -Raw) -split '\s+')[0]
$actual = Get-YavSha256 -Path $Package
if ($expected -ne $actual) { throw "The package does not have the SHA-256 that was recorded for it: $actual instead of $expected." }

$results = Join-Path $repo 'artifacts\package-verification'
New-Item -ItemType Directory -Path $results -Force | Out-Null

if ($Sandbox) {
    $sandboxExe = Join-Path $env:SystemRoot 'System32\WindowsSandbox.exe'
    if (-not (Test-Path $sandboxExe)) { throw 'Windows Sandbox is not turned on for this Windows. Nothing was checked.' }
    if (Get-Process -Name 'WindowsSandbox*' -ErrorAction SilentlyContinue) { throw 'Windows Sandbox is running already, and only one can run at a time. Nothing was checked.' }

    $share = Join-Path $results 'sandbox'
    if (Test-Path $share) { Remove-Item $share -Recurse -Force }
    New-Item -ItemType Directory -Path $share | Out-Null
    Copy-Item $Package (Join-Path $share 'package.zip')
    Copy-Item (Join-Path $PSScriptRoot 'clean-machine-check.ps1') $share

    $configuration = Join-Path $share 'yav-check.wsb'
    @"
<Configuration>
  <Networking>Disable</Networking>
  <vGPU>Disable</vGPU>
  <ClipboardRedirection>Disable</ClipboardRedirection>
  <PrinterRedirection>Disable</PrinterRedirection>
  <AudioInput>Disable</AudioInput>
  <VideoInput>Disable</VideoInput>
  <MappedFolders>
    <MappedFolder>
      <HostFolder>$share</HostFolder>
      <SandboxFolder>C:\yav-check</SandboxFolder>
      <ReadOnly>false</ReadOnly>
    </MappedFolder>
  </MappedFolders>
  <LogonCommand>
    <Command>powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\yav-check\clean-machine-check.ps1 -Package C:\yav-check\package.zip -Results C:\yav-check\result.json -ShutDown</Command>
  </LogonCommand>
</Configuration>
"@ | Set-Content -Path $configuration -Encoding utf8

    Write-Host "Starting Windows Sandbox. It has no network, and it ends by itself when the check is done."
    Start-Process -FilePath $sandboxExe -ArgumentList "`"$configuration`""
    $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
    $result = Join-Path $share 'result.json'
    while (-not (Test-Path $result) -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 5 }
    if (-not (Test-Path $result)) {
        throw "Windows Sandbox gave no result within $TimeoutMinutes minutes. Nothing is verified. Close its window if it is still open."
    }

    Start-Sleep -Seconds 2
    $report = Get-Content $result -Raw | ConvertFrom-Json
    Copy-Item $result (Join-Path $results 'clean-machine.json') -Force
}
else {
    $work = Join-Path ([IO.Path]::GetTempPath()) ("yav pack√ge check " + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Path $work | Out-Null
    try {
        $result = Join-Path $work 'result.json'
        # A process of its own, whose environment holds nothing but Windows itself.
        $environment = @{
            PATH                     = "$env:SystemRoot\System32;$env:SystemRoot;$env:SystemRoot\System32\WindowsPowerShell\v1.0"
            DOTNET_ROOT              = (Join-Path $work 'there is no dotnet here')
            'DOTNET_ROOT(x86)'       = (Join-Path $work 'there is no dotnet here')
            DOTNET_MULTILEVEL_LOOKUP = '0'
        }
        $saved = @{}
        foreach ($name in $environment.Keys) {
            $saved[$name] = [Environment]::GetEnvironmentVariable($name)
            [Environment]::SetEnvironmentVariable($name, $environment[$name])
        }

        try {
            & "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass `
                -File (Join-Path $PSScriptRoot 'clean-machine-check.ps1') -Package $Package -Results $result -Work $work
        }
        finally {
            foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name]) }
        }

        if (-not (Test-Path $result)) { throw 'The check gave no result.' }
        $report = Get-Content $result -Raw | ConvertFrom-Json
        Copy-Item $result (Join-Path $results 'isolated-environment.json') -Force
    }
    finally {
        Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host ''
Write-Host ("Checked {0} in {1}" -f [IO.Path]::GetFileName($Package), $report.where)
foreach ($check in $report.checks) {
    $mark = if ($check.passed) { '[ok]  ' } else { '[FAIL]' }
    Write-Host ("  {0} {1}{2}" -f $mark, $check.name, $(if ($check.detail) { ': ' + $check.detail } else { '' }))
}

$failed = @($report.checks | Where-Object { -not $_.passed })
Write-Host ''
if ($failed.Count -gt 0) {
    Write-Host "$($failed.Count) check(s) failed." -ForegroundColor Red
    exit 1
}

Write-Host "All $(@($report.checks).Count) checks passed." -ForegroundColor Green
