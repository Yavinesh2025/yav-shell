<#
.SYNOPSIS
    Installs YAV Shell for the current user. No administrator rights are needed and none are asked for.
.DESCRIPTION
    Copies the files of this package to a directory of the current user and registers the program so
    that it appears under "Installed apps" and can be removed there. The .NET runtime is part of the
    package; nothing else is installed.

    Adding the directory to PATH and creating a Start menu shortcut are optional and are only done when
    asked for. Your data (%LOCALAPPDATA%\YavShell: settings, history, workspaces) is never touched.

    This is a script, not a signed installer. The package is not code-signed; Windows may therefore
    warn when yav.exe is started for the first time after a download.
.PARAMETER InstallDir
    Where to install. Default: %LOCALAPPDATA%\Programs\YavShell
.PARAMETER AddToPath
    Adds the installation directory to the PATH of the current user, so that 'yav' works in every new console.
.PARAMETER Shortcut
    Creates "YAV Shell" in the Start menu of the current user.
.PARAMETER NoRegister
    Does not add the entry under "Installed apps".
.EXAMPLE
    .\install.ps1 -AddToPath -Shortcut
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$InstallDir,
    [switch]$AddToPath,
    [switch]$Shortcut,
    [switch]$NoRegister
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\YavInstall.ps1"

$source = $PSScriptRoot
if (-not (Test-Path -LiteralPath (Join-Path $source 'yav.exe'))) {
    throw "yav.exe is not next to this script. Run install.ps1 from the unpacked package, not from the source tree (scripts\package.ps1 builds the package)."
}

$problems = @(Test-YavPackage -Directory $source)
if ($problems.Count -gt 0) {
    throw "The package is incomplete or was changed after it was built:`n  " + ($problems -join "`n  ")
}

if (-not $InstallDir) { $InstallDir = Get-YavDefaultInstallDir }
$InstallDir = [IO.Path]::GetFullPath($InstallDir)
if ($InstallDir.TrimEnd('\') -ieq $source.TrimEnd('\')) {
    throw "The package is already in $InstallDir. Unpack it somewhere else, or use it where it is: it is portable."
}

if ($InstallDir.TrimEnd('\') -ieq (Get-YavDataDir).TrimEnd('\')) {
    throw "$InstallDir is where YAV keeps your data. Choose another directory for the program."
}

$running = @(Get-Process -Name yav -ErrorAction SilentlyContinue | Where-Object {
    try { $_.Path -and ([IO.Path]::GetDirectoryName($_.Path).TrimEnd('\') -ieq $InstallDir.TrimEnd('\')) } catch { $false }
})
if ($running.Count -gt 0) {
    throw "YAV Shell is running from $InstallDir (process $($running.Id -join ', ')). Close it with /exit and run the installation again."
}

$manifest = Get-Content -LiteralPath (Join-Path $source 'package-manifest.json') -Raw | ConvertFrom-Json
$version = [string]$manifest.version

if ($PSCmdlet.ShouldProcess($InstallDir, "Install $script:ProductName $version")) {
    if (Test-Path -LiteralPath $InstallDir) {
        # Only what an earlier installation put there is removed. A directory that holds anything else is left alone.
        $earlier = Join-Path $InstallDir 'package-manifest.json'
        if (-not (Test-Path -LiteralPath $earlier)) {
            if (@(Get-ChildItem -LiteralPath $InstallDir -Force).Count -gt 0) {
                throw "$InstallDir exists and was not created by this installer. Choose another directory with -InstallDir."
            }
        }
        else {
            foreach ($file in (Get-Content -LiteralPath $earlier -Raw | ConvertFrom-Json).files) {
                $path = Join-Path $InstallDir ($file.path -replace '/', '\')
                if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
            }

            Remove-Item -LiteralPath $earlier -Force
        }
    }

    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    foreach ($file in $manifest.files) {
        $relative = $file.path -replace '/', '\'
        $target = Join-Path $InstallDir $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $source $relative) -Destination $target -Force
    }

    Copy-Item -LiteralPath (Join-Path $source 'package-manifest.json') -Destination (Join-Path $InstallDir 'package-manifest.json') -Force

    $installed = @(Test-YavPackage -Directory $InstallDir)
    if ($installed.Count -gt 0) {
        throw "The installation in $InstallDir is incomplete:`n  " + ($installed -join "`n  ")
    }

    $reported = Get-YavVersion -Exe (Join-Path $InstallDir 'yav.exe')
    if ($reported.ExitCode -ne 0 -or $reported.Text -ne "yav $version") {
        throw "The installed yav.exe did not start as expected. It ended with exit code $($reported.ExitCode) and said: $($reported.Text)"
    }

    Write-Host "Installed $script:ProductName $version in $InstallDir"
}

$pathChanged = $false
if ($AddToPath -and $PSCmdlet.ShouldProcess('PATH of the current user', "Add $InstallDir")) {
    $current = Get-YavUserPath
    $changed = Add-YavPathEntry -Path $current.Value -Entry $InstallDir
    if ($changed -ne $current.Value) {
        Set-YavUserPath -Value $changed -Kind $current.Kind
        $pathChanged = $true
        Write-Host "Added to the PATH of $env:USERNAME. Consoles that are opened from now on find 'yav'."
    }
    else {
        Write-Host "The PATH of $env:USERNAME contains the directory already."
    }
}

$shortcutPath = $null
if ($Shortcut -and $PSCmdlet.ShouldProcess((Get-YavShortcutPath), 'Create shortcut')) {
    $shortcutPath = Get-YavShortcutPath
    New-YavShortcut -Target (Join-Path $InstallDir 'yav.exe') -ShortcutPath $shortcutPath
    Write-Host "Created the Start menu entry 'YAV Shell'."
}

if (-not $NoRegister -and $PSCmdlet.ShouldProcess('Installed apps', "Register $script:ProductName")) {
    $uninstall = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $InstallDir 'uninstall.ps1')`""
    New-Item -Path $script:UninstallKey -Force | Out-Null
    $values = [ordered]@{
        DisplayName     = $script:ProductName
        DisplayVersion  = $version
        Publisher       = 'YAV'
        InstallLocation = $InstallDir
        DisplayIcon     = (Join-Path $InstallDir 'yav.exe')
        UninstallString = $uninstall
        YavAddedToPath  = if ($AddToPath) { '1' } else { '0' }
        YavShortcut     = if ($shortcutPath) { $shortcutPath } else { '' }
    }
    foreach ($name in $values.Keys) {
        Set-ItemProperty -Path $script:UninstallKey -Name $name -Value $values[$name] -Type String
    }

    Set-ItemProperty -Path $script:UninstallKey -Name 'NoModify' -Value 1 -Type DWord
    Set-ItemProperty -Path $script:UninstallKey -Name 'NoRepair' -Value 1 -Type DWord
}

Write-Host ''
Write-Host 'Start it in the directory of a project:'
if ($AddToPath) {
    Write-Host '    yav' -NoNewline; Write-Host '            (in a console that was opened after this installation)'
}
else {
    Write-Host "    & `"$(Join-Path $InstallDir 'yav.exe')`""
}

Write-Host "First steps and everything else: $(Join-Path $InstallDir 'docs\user-guide.md')"
Write-Host "YAV uses the agent programs you have installed (Codex, Claude Code). 'yav doctor' shows what it finds."
