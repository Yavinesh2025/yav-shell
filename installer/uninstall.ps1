<#
.SYNOPSIS
    Removes YAV Shell for the current user.
.DESCRIPTION
    Removes the files the installation put there, its PATH entry, its Start menu entry and its entry
    under "Installed apps". Files in the installation directory that the installation did not put there
    are left where they are.

    Your data (settings, history, isolated workspaces) stays unless -RemoveData is given.
.PARAMETER InstallDir
    Where YAV Shell is installed. Default: the directory that was registered, otherwise the directory of this script.
.PARAMETER RemoveData
    Also removes %LOCALAPPDATA%\YavShell and the API key YAV stored in the Windows Credential Manager.
    Isolated workspaces with changes that were never applied are lost with it.
.PARAMETER Force
    Does not ask before removing data.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$InstallDir,
    [switch]$RemoveData,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\YavInstall.ps1"

$registered = if (Test-Path $script:UninstallKey) { Get-ItemProperty -Path $script:UninstallKey } else { $null }
if (-not $InstallDir) {
    $InstallDir = if ($registered -and $registered.InstallLocation) { $registered.InstallLocation } else { $PSScriptRoot }
}

$InstallDir = [IO.Path]::GetFullPath($InstallDir)
$manifestPath = Join-Path $InstallDir 'package-manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath)) {
    throw "$InstallDir does not hold an installation of YAV Shell (package-manifest.json is missing). Nothing was removed."
}

$running = @(Get-Process -Name yav -ErrorAction SilentlyContinue | Where-Object {
    try { $_.Path -and ([IO.Path]::GetDirectoryName($_.Path).TrimEnd('\') -ieq $InstallDir.TrimEnd('\')) } catch { $false }
})
if ($running.Count -gt 0) {
    throw "YAV Shell is running from $InstallDir (process $($running.Id -join ', ')). Close it with /exit first."
}

# The script removes the directory it may be running from, so it continues from a copy outside of it.
if ($PSScriptRoot.TrimEnd('\') -ieq $InstallDir.TrimEnd('\') -and -not $env:YAV_UNINSTALL_RELOCATED) {
    $copy = Join-Path ([IO.Path]::GetTempPath()) ("yav-uninstall-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $copy | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'uninstall.ps1'), (Join-Path $PSScriptRoot 'YavInstall.ps1') -Destination $copy
    $env:YAV_UNINSTALL_RELOCATED = '1'
    try {
        $forward = @{ InstallDir = $InstallDir }
        if ($RemoveData) { $forward.RemoveData = $true }
        if ($Force) { $forward.Force = $true }
        if ($WhatIfPreference) { $forward.WhatIf = $true }
        & (Join-Path $copy 'uninstall.ps1') @forward
    }
    finally {
        Remove-Item Env:\YAV_UNINSTALL_RELOCATED -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $copy -Recurse -Force -ErrorAction SilentlyContinue
    }

    return
}

if ($PSCmdlet.ShouldProcess($InstallDir, "Remove $script:ProductName")) {
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    foreach ($file in $manifest.files) {
        $path = Join-Path $InstallDir ($file.path -replace '/', '\')
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
    }

    Remove-Item -LiteralPath $manifestPath -Force

    # Directories are removed from the inside out, and only when nothing is left in them.
    Get-ChildItem -LiteralPath $InstallDir -Recurse -Directory | Sort-Object { $_.FullName.Length } -Descending | ForEach-Object {
        if (@(Get-ChildItem -LiteralPath $_.FullName -Force).Count -eq 0) { Remove-Item -LiteralPath $_.FullName -Force }
    }

    if (@(Get-ChildItem -LiteralPath $InstallDir -Force).Count -eq 0) {
        Remove-Item -LiteralPath $InstallDir -Force
        Write-Host "Removed $InstallDir"
    }
    else {
        Write-Host "Removed the files of $script:ProductName. $InstallDir holds other files and was kept."
    }
}

if ($PSCmdlet.ShouldProcess('PATH of the current user', "Remove $InstallDir")) {
    $current = Get-YavUserPath
    $changed = Remove-YavPathEntry -Path $current.Value -Entry $InstallDir
    if ($changed -ne $current.Value) {
        Set-YavUserPath -Value $changed -Kind $current.Kind
        Write-Host "Removed from the PATH of $env:USERNAME."
    }
}

$shortcut = if ($registered -and $registered.PSObject.Properties['YavShortcut'] -and $registered.YavShortcut) { $registered.YavShortcut } else { Get-YavShortcutPath }
if ((Test-Path -LiteralPath $shortcut) -and $PSCmdlet.ShouldProcess($shortcut, 'Remove shortcut')) {
    $target = (New-Object -ComObject WScript.Shell).CreateShortcut($shortcut).TargetPath
    if ([IO.Path]::GetDirectoryName($target).TrimEnd('\') -ieq $InstallDir.TrimEnd('\')) {
        Remove-Item -LiteralPath $shortcut -Force
        Write-Host "Removed the Start menu entry."
    }
}

if ((Test-Path $script:UninstallKey) -and $PSCmdlet.ShouldProcess('Installed apps', "Remove the entry of $script:ProductName")) {
    Remove-Item -Path $script:UninstallKey -Recurse -Force
}

$data = Get-YavDataDir
if ($RemoveData) {
    $confirmed = $Force -or $WhatIfPreference
    if (-not $confirmed -and (Test-Path -LiteralPath $data)) {
        Write-Host ''
        Write-Host "This removes ${data}: settings, history, and isolated workspaces, including changes that were never applied to a project."
        $confirmed = (Read-Host 'Type yes to remove your data') -eq 'yes'
    }

    if ($confirmed -and $PSCmdlet.ShouldProcess($data, 'Remove data')) {
        if (Test-Path -LiteralPath $data) { Remove-Item -LiteralPath $data -Recurse -Force }
        # The API key for Claude Code, when one was stored with /login claude --api-key.
        & cmdkey.exe /delete:YavShell/anthropic-api-key *> $null
        Write-Host "Removed your data and the stored API key."
    }
    elseif (-not $confirmed) {
        Write-Host "Your data was kept: $data"
    }
}
elseif (Test-Path -LiteralPath $data) {
    Write-Host "Your data was kept: $data  (uninstall.ps1 -RemoveData removes it)"
}
