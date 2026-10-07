<#
.SYNOPSIS
    Checks the package, dist\yav.exe, the way a machine without .NET, Git and agents would use it.
.DESCRIPTION
    Copies yav.exe into a new directory and starts it in a process of its own, for which exactly four
    environment variables are replaced: PATH holds nothing but Windows, DOTNET_ROOT and DOTNET_ROOT(x86) lead
    nowhere, and DOTNET_MULTILEVEL_LOOKUP is 0. So no .NET, no Git and no agent is on PATH. Every other variable
    of this process is passed on as it is; the check itself sets YAV_HOME, TMP and TEMP to directories of its
    own. It then lets the program install itself into another new directory with 'yav.exe install --dir ...
    --no-path --no-register', compares what was installed with the files of the repository (the license, the
    notices, the README, docs\*.md, licenses\*.txt and the examples), starts it, and lets the installed program
    remove itself with 'yav uninstall --dir ...'.

    With -Sandbox the same is done inside Windows Sandbox: a new, disposable Windows without any of the
    software of this machine. There the program is also installed with the options a user gets by default -
    into the default directory, with the PATH entry and the entry under "Installed apps" - and removed again
    with the command that "Installed apps" runs. That needs the Windows feature "Windows Sandbox" and opens
    its window.

    Both start yav hidden and with its input redirected, as a script does, so yav cannot ask anything: the
    offer to install itself at the start, and a window of its own that waits for Enter before it closes, are
    not exercised.

    Without -Sandbox nothing of this machine is changed: PATH and "Installed apps" are left alone.
.PARAMETER Package
    The program to check. Default: dist\yav.exe.
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
. (Join-Path $PSScriptRoot 'package-tools.ps1')

if (-not $Package) { $Package = Join-Path $repo 'dist\yav.exe' }
if (-not (Test-Path -LiteralPath $Package -PathType Leaf)) {
    throw "$Package does not exist. Build the package with scripts\package.ps1."
}

$Package = (Resolve-Path -LiteralPath $Package).ProviderPath
$problems = @(Test-YavChecksumFile -Path $Package)
if ($problems.Count -gt 0) { throw "The package is not the one that was built: $($problems -join ' ')" }
$expected = Get-YavSha256 -Path $Package
$version = Get-YavProductVersion -Repository $repo

$results = Join-Path $repo 'artifacts\package-verification'
New-Item -ItemType Directory -Path $results -Force | Out-Null

# What the installation has to hold next to yav.exe, as the repository holds it: '<sha256>  <relative path>'.
$installedFiles = @(Get-YavPackageFiles -Repository $repo | ForEach-Object { (Get-YavSha256 -Path (Join-Path $repo $_)) + '  ' + $_ })
function Write-ExpectedFiles([string]$Path) {
    [IO.File]::WriteAllLines($Path, [string[]]$installedFiles, (New-Object System.Text.UTF8Encoding $false))
}

if ($Sandbox) {
    $sandboxExe = Join-Path $env:SystemRoot 'System32\WindowsSandbox.exe'
    if (-not (Test-Path $sandboxExe)) { throw 'Windows Sandbox is not turned on for this Windows. Nothing was checked.' }
    if (Get-Process -Name 'WindowsSandbox*' -ErrorAction SilentlyContinue) { throw 'Windows Sandbox is running already, and only one can run at a time. Nothing was checked.' }

    $share = Join-Path $results 'sandbox'
    if (Test-Path $share) { Remove-Item $share -Recurse -Force }
    New-Item -ItemType Directory -Path $share | Out-Null
    Copy-Item -LiteralPath $Package -Destination (Join-Path $share 'yav.exe')
    Copy-Item (Join-Path $PSScriptRoot 'clean-machine-check.ps1') $share
    Write-ExpectedFiles (Join-Path $share 'expected-files.txt')

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
    <Command>powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\yav-check\clean-machine-check.ps1 -Package C:\yav-check\yav.exe -Sha256 $expected -Version $version -Results C:\yav-check\result.json -Expected C:\yav-check\expected-files.txt -ShutDown</Command>
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
    # A directory with a blank and a letter outside ASCII in its name, as a user's download directory may have. The
    # letter is written as its number: this file is ASCII then, which every PowerShell reads in the same way.
    $work = Join-Path ([IO.Path]::GetTempPath()) ('yav pack' + [char]0x00E4 + 'ge check ' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Path $work | Out-Null
    try {
        $copy = Join-Path $work 'download\yav.exe'
        New-Item -ItemType Directory -Path (Split-Path -Parent $copy) | Out-Null
        Copy-Item -LiteralPath $Package -Destination $copy
        $result = Join-Path $work 'result.json'
        $expectedFiles = Join-Path $work 'expected-files.txt'
        Write-ExpectedFiles $expectedFiles

        # A process of its own, for which exactly these four variables are replaced: PATH holds nothing but Windows,
        # DOTNET_ROOT and DOTNET_ROOT(x86) lead nowhere, and DOTNET_MULTILEVEL_LOOKUP is 0. Every other variable of
        # this process is passed on to it as it is; the check sets YAV_HOME, TMP and TEMP itself.
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
                -File (Join-Path $PSScriptRoot 'clean-machine-check.ps1') -Package $copy -Sha256 $expected -Version $version -Results $result -Expected $expectedFiles -Work $work
            $checkExit = $LASTEXITCODE
        }
        finally {
            foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name]) }
        }

        if (-not (Test-Path $result)) { throw "The check gave no result. Windows PowerShell, which ran it, ended with exit code $checkExit." }
        $report = Get-Content $result -Raw | ConvertFrom-Json
        Copy-Item $result (Join-Path $results 'isolated-environment.json') -Force
    }
    finally {
        # Not stopped by an error: a directory that cannot be removed must not hide the result, or the error that
        # ended the check. It is said instead.
        Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue -ErrorVariable notRemoved
        if (Test-Path -LiteralPath $work) {
            $reason = if ($notRemoved) { ': ' + $notRemoved[0].Exception.Message } else { '' }
            Write-Warning "The work directory of the check could not be removed; delete it by hand: $work$reason"
        }
    }
}

Write-Host ''
Write-Host ("Checked {0} {1} in {2}" -f [IO.Path]::GetFileName($Package), $version, $report.where)
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
