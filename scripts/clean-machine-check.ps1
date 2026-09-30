<#
.SYNOPSIS
    The check that verify-package.ps1 runs: on this machine with an empty environment, or inside Windows Sandbox.
    It runs in Windows PowerShell 5.1, because that is what a new Windows has.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Package,
    [Parameter(Mandatory)][string]$Results,
    [string]$Work,
    [switch]$ShutDown
)

$ErrorActionPreference = 'Stop'
$checks = New-Object System.Collections.Generic.List[object]

function Add-Check([string]$Name, [bool]$Passed, [string]$Detail = '') {
    $script:checks.Add([pscustomobject]@{ name = $Name; passed = $Passed; detail = $Detail })
}

function Get-Sha256([string]$Path) {
    # Not Get-FileHash: Windows PowerShell does not find it when a program below PowerShell 7 started it.
    $algorithm = [System.Security.Cryptography.SHA256]::Create()
    $stream = [System.IO.File]::OpenRead($Path)
    try { [System.BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
    finally { $stream.Dispose(); $algorithm.Dispose() }
}

function Invoke-Yav([string]$Exe, [string[]]$Arguments, [string]$InputText) {
    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = $Exe
    $start.Arguments = ($Arguments | ForEach-Object { if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ } }) -join ' '
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.RedirectStandardInput = $true
    $start.StandardOutputEncoding = [Text.Encoding]::UTF8
    $start.StandardErrorEncoding = [Text.Encoding]::UTF8
    $start.CreateNoWindow = $true
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $process = [Diagnostics.Process]::Start($start)
    if ($InputText) { $process.StandardInput.Write($InputText) }
    $process.StandardInput.Close()
    $output = $process.StandardOutput.ReadToEndAsync()
    $errors = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit(120000)) { $process.Kill(); throw "$Exe $($Arguments -join ' ') did not end within two minutes." }
    $watch.Stop()
    [pscustomobject]@{ ExitCode = $process.ExitCode; Output = $output.Result; Error = $errors.Result; Milliseconds = $watch.ElapsedMilliseconds }
}

try {
    if (-not $Work) { $Work = Join-Path $env:TEMP ('yav check ' + [Guid]::NewGuid().ToString('N').Substring(0, 8)) }
    New-Item -ItemType Directory -Path $Work -Force | Out-Null
    $env:YAV_HOME = Join-Path $Work 'data'
    $where = if ($ShutDown) { 'Windows Sandbox: a new Windows without any software of this machine' } else { 'this machine, with an environment that holds nothing but Windows' }

    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    $git = Get-Command git -ErrorAction SilentlyContinue
    Add-Check 'No .NET is on PATH' (-not $dotnet) $(if ($dotnet) { $dotnet.Source } else { '' })
    Add-Check 'No Git is on PATH' (-not $git) $(if ($git) { $git.Source } else { '' })
    if ($ShutDown) {
        $runtimes = Test-Path (Join-Path $env:ProgramFiles 'dotnet')
        Add-Check 'No .NET is installed on this Windows' (-not $runtimes) (Join-Path $env:ProgramFiles 'dotnet')
    }

    $unpacked = Join-Path $Work 'unpacked'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory($Package, $unpacked)
    $root = Get-ChildItem $unpacked -Directory | Select-Object -First 1 -ExpandProperty FullName
    $yav = Join-Path $root 'yav.exe'
    Add-Check 'The package contains yav.exe' (Test-Path $yav) $root

    $manifest = Get-Content (Join-Path $root 'package-manifest.json') -Raw | ConvertFrom-Json
    $changed = @($manifest.files | Where-Object {
        $path = Join-Path $root ($_.path -replace '/', '\')
        -not (Test-Path -LiteralPath $path) -or (Get-Sha256 $path) -ne $_.sha256
    })
    Add-Check 'Every file is what the manifest says' ($changed.Count -eq 0) "$(@($manifest.files).Count) files"
    Add-Check 'The manifest says that the package is not signed' ($manifest.signed -eq $false) ([string]$manifest.signature)

    $cold = Invoke-Yav $yav @('--version')
    Add-Check 'yav --version' ($cold.ExitCode -eq 0 -and $cold.Output.Trim() -eq "yav $($manifest.version)") "$($cold.Output.Trim()), $($cold.Milliseconds) ms the first time"
    $times = 1..5 | ForEach-Object { (Invoke-Yav $yav @('--version')).Milliseconds } | Sort-Object
    Add-Check 'yav --version, started five more times' $true "median $($times[2]) ms, slowest $($times[4]) ms"

    $help = Invoke-Yav $yav @('--help')
    Add-Check 'yav --help' ($help.ExitCode -eq 0 -and $help.Output -match 'yav run --project') ''

    $bogus = Invoke-Yav $yav @('--bogus')
    Add-Check 'An option that does not exist ends with exit code 64' ($bogus.ExitCode -eq 64) $bogus.Error.Trim().Split("`n")[0]

    $doctor = Invoke-Yav $yav @('doctor', '--json')
    $report = $null
    try { $report = $doctor.Output | ConvertFrom-Json } catch { }
    Add-Check 'yav doctor --json gives a report' ($null -ne $report -and $report.type -eq 'doctor') "exit code $($doctor.ExitCode), $($doctor.Milliseconds) ms"
    if ($report) {
        $runtime = $report.checks | Where-Object { $_.name -eq 'Runtime' } | Select-Object -First 1
        Add-Check 'The .NET runtime that runs yav is the one of the package' ($runtime.detail -match 'part of this installation' -and $runtime.detail -like "*$root*") $runtime.detail
        $data = $report.checks | Where-Object { $_.name -eq 'Data directory' } | Select-Object -First 1
        Add-Check 'The data directory is the one that was named' ($data.detail -like "$env:YAV_HOME*") $data.detail
        $agents = @($report.checks | Where-Object { $_.name -eq 'Installation' })
        Add-Check 'Agents that are not installed are reported, not assumed' ($agents.Count -gt 0 -and @($agents | Where-Object { $_.status -eq 'ok' }).Count -eq 0) (($agents | ForEach-Object { "$($_.area): $($_.status)" }) -join '; ')
    }

    $project = Join-Path $Work 'a project'
    New-Item -ItemType Directory -Path $project -Force | Out-Null
    Set-Content -Path (Join-Path $project 'app.txt') -Value 'one'
    $run = Invoke-Yav $yav @('run', '--project', $project, '--task', 'Change nothing.', '--json')
    $last = $null
    try { $last = ($run.Output.Trim().Split("`n") | Select-Object -Last 1) | ConvertFrom-Json } catch { }
    Add-Check 'yav run without models is blocked and says why' ($run.ExitCode -eq 2 -and $last.outcome -eq 'blocked' -and @($last.problems).Count -gt 0) "exit code $($run.ExitCode): $(@($last.problems | ForEach-Object { $_.code }) -join ', ')"
    Add-Check 'The project was not changed' ((Get-Content (Join-Path $project 'app.txt') -Raw).Trim() -eq 'one' -and @(Get-ChildItem $project -Force).Count -eq 1) ''

    $shell = Invoke-Yav $yav @($project) "/help`n/status`n/exit`n"
    Add-Check 'The shell reads commands from a pipe and ends' ($shell.ExitCode -eq 0 -and $shell.Output -match 'YAV Shell' -and $shell.Output -match 'State saved') "$($shell.Milliseconds) ms"
    Add-Check 'Nothing but plain text is written to a pipe' (-not $shell.Output.Contains([string][char]27)) ''

    $installed = Join-Path $Work 'installed here'
    & (Join-Path $root 'install.ps1') -InstallDir $installed -NoRegister | Out-Null
    $after = Invoke-Yav (Join-Path $installed 'yav.exe') @('--version')
    Add-Check 'install.ps1 installs, and what was installed starts' ($after.ExitCode -eq 0 -and $after.Output.Trim() -eq "yav $($manifest.version)") $installed

    Set-Content -Path (Join-Path $installed 'my own file.txt') -Value 'mine'
    & (Join-Path $installed 'uninstall.ps1') -InstallDir $installed | Out-Null
    $left = @(Get-ChildItem $installed -Recurse -File -ErrorAction SilentlyContinue | ForEach-Object { $_.Name })
    Add-Check 'uninstall.ps1 removes what was installed and nothing else' ($left.Count -eq 1 -and $left[0] -eq 'my own file.txt') ($left -join ', ')
    Add-Check 'The data of the user is kept by uninstall.ps1' (Test-Path (Join-Path $env:YAV_HOME 'yav.db')) $env:YAV_HOME

    if ($ShutDown) {
        & (Join-Path $root 'install.ps1') -AddToPath -Shortcut | Out-Null
        $target = Join-Path $env:LOCALAPPDATA 'Programs\YavShell'
        $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
        Add-Check 'With -AddToPath the directory is on the PATH of the user' ($userPath -like "*$target*") $userPath
        Add-Check 'With -Shortcut there is an entry in the Start menu' (Test-Path (Join-Path ([Environment]::GetFolderPath('Programs')) 'YAV Shell.lnk')) ''
        Add-Check 'The installation appears under Installed apps' (Test-Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\YavShell') ''
        $fresh = & "$env:SystemRoot\System32\cmd.exe" /d /c "set PATH=%PATH%;$target&& yav --version" 2>&1 | Out-String
        Add-Check 'yav is found by its name' ($fresh.Trim() -eq "yav $($manifest.version)") $fresh.Trim()

        & (Join-Path $target 'uninstall.ps1') | Out-Null
        $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
        Add-Check 'After uninstall.ps1 nothing of it is left' (
            -not (Test-Path $target) -and ($userPath -notlike "*$target*") -and
            -not (Test-Path (Join-Path ([Environment]::GetFolderPath('Programs')) 'YAV Shell.lnk')) -and
            -not (Test-Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\YavShell')) ''
    }
}
catch {
    Add-Check 'The check ran to its end' $false ($_.Exception.Message + ' at ' + $_.InvocationInfo.PositionMessage)
    $where = "$where"
}
finally {
    [pscustomobject]@{
        where   = $where
        at      = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
        windows = [Environment]::OSVersion.VersionString
        checks  = $checks
    } | ConvertTo-Json -Depth 5 | Set-Content -Path $Results -Encoding UTF8

    if ($ShutDown) { & "$env:SystemRoot\System32\shutdown.exe" /s /t 3 /f }
}
