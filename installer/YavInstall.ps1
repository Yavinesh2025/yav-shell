# Functions shared by install.ps1 and uninstall.ps1. Dot-source this file.
# Everything here works for the current user only and never asks for administrator rights.

Set-StrictMode -Version Latest

$script:ProductName = 'YAV Shell'
$script:UninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\YavShell'
$script:EnvironmentKey = 'HKCU:\Environment'

function Get-YavDefaultInstallDir {
    Join-Path $env:LOCALAPPDATA 'Programs\YavShell'
}

function Get-YavDataDir {
    # The same place yav.exe uses when YAV_HOME is not set.
    Join-Path $env:LOCALAPPDATA 'YavShell'
}

function ConvertTo-YavComparablePath {
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Path)

    $expanded = [Environment]::ExpandEnvironmentVariables($Path.Trim().Trim('"'))
    if ($expanded.Length -eq 0) { return '' }
    $expanded.TrimEnd('\').ToLowerInvariant()
}

function Add-YavPathEntry {
    <#
    .SYNOPSIS
        Returns the PATH text with the entry added at the end, unless it is there already.
        Nothing else in the text is changed: not the order, not the spelling, not empty parts.
    #>
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$Path,
        [Parameter(Mandatory)][string]$Entry
    )

    $wanted = ConvertTo-YavComparablePath $Entry
    foreach ($part in $Path -split ';') {
        if ((ConvertTo-YavComparablePath $part) -eq $wanted) { return $Path }
    }

    if ($Path.Length -eq 0) { return $Entry }
    if ($Path.EndsWith(';')) { return $Path + $Entry }
    return $Path + ';' + $Entry
}

function Remove-YavPathEntry {
    <#
    .SYNOPSIS
        Returns the PATH text without the entry. Every other part stays exactly as it was.
    #>
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$Path,
        [Parameter(Mandatory)][string]$Entry
    )

    # Parts that stay are joined as they were split, empty ones included: a text without the entry comes back unchanged.
    $wanted = ConvertTo-YavComparablePath $Entry
    $kept = @($Path -split ';' | Where-Object { (ConvertTo-YavComparablePath $_) -ne $wanted })
    return ($kept -join ';')
}

function Get-YavUserPath {
    # Read without expanding, so that %VARIABLES% in the user's PATH are written back as they were.
    $key = Get-Item -Path $script:EnvironmentKey
    [pscustomobject]@{
        Value = [string]$key.GetValue('Path', '', [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        Kind  = if ($key.GetValueNames() -contains 'Path') { $key.GetValueKind('Path') } else { [Microsoft.Win32.RegistryValueKind]::ExpandString }
    }
}

function Set-YavUserPath {
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$Value,
        [Parameter(Mandatory)][Microsoft.Win32.RegistryValueKind]$Kind
    )

    Set-ItemProperty -Path $script:EnvironmentKey -Name 'Path' -Value $Value -Type $Kind
    Send-YavEnvironmentChange
}

function Send-YavEnvironmentChange {
    # Tells running programs, such as Explorer, that the environment changed, so new consoles see it.
    if (-not ('YavInstall.Native' -as [type])) {
        Add-Type -Namespace YavInstall -Name Native -MemberDefinition @'
[DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, UIntPtr wParam, string lParam, uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);
'@
    }

    $result = [UIntPtr]::Zero
    [void][YavInstall.Native]::SendMessageTimeout([IntPtr]0xffff, 0x1A, [UIntPtr]::Zero, 'Environment', 2, 5000, [ref]$result)
}

function Get-YavShortcutPath {
    Join-Path ([Environment]::GetFolderPath('Programs')) 'YAV Shell.lnk'
}

function New-YavShortcut {
    param(
        [Parameter(Mandatory)][string]$Target,
        [Parameter(Mandatory)][string]$ShortcutPath
    )

    $shell = New-Object -ComObject WScript.Shell
    try {
        $shortcut = $shell.CreateShortcut($ShortcutPath)
        $shortcut.TargetPath = $Target
        # Started from the Start menu there is no project directory. YAV then starts without one and asks for /open.
        $shortcut.WorkingDirectory = [Environment]::GetFolderPath('UserProfile')
        $shortcut.Description = 'YAV Shell - a dual-model coding console'
        $shortcut.Save()
    }
    finally {
        [void][Runtime.InteropServices.Marshal]::ReleaseComObject($shell)
    }
}

function Get-YavVersion {
    <#
    .SYNOPSIS
        Starts yav.exe and returns what it says its version is. The program is started directly, because
        PowerShell takes brackets in the path of a program for a pattern.
    #>
    param([Parameter(Mandatory)][string]$Exe)

    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = $Exe
    $start.Arguments = '--version'
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.CreateNoWindow = $true
    $process = [System.Diagnostics.Process]::Start($start)
    $text = $process.StandardOutput.ReadToEnd()
    $process.WaitForExit()
    [pscustomobject]@{ ExitCode = $process.ExitCode; Text = $text.Trim() }
}

function Get-YavSha256 {
    <#
    .SYNOPSIS
        The SHA-256 of a file, in lower-case hexadecimal digits.
    #>
    param([Parameter(Mandatory)][string]$Path)

    # Not Get-FileHash: Windows PowerShell does not find it when it was started by a program that was
    # itself started from PowerShell 7, because it then looks for its modules where those of PowerShell 7 are.
    $algorithm = [System.Security.Cryptography.SHA256]::Create()
    try {
        $stream = [System.IO.File]::OpenRead($Path)
        try { $hash = $algorithm.ComputeHash($stream) }
        finally { $stream.Dispose() }
    }
    finally {
        $algorithm.Dispose()
    }

    [System.BitConverter]::ToString($hash).Replace('-', '').ToLowerInvariant()
}

function Get-YavFileHashes {
    <#
    .SYNOPSIS
        Every file below the directory with its size and SHA-256, named relative to the directory.
    #>
    param([Parameter(Mandatory)][string]$Directory)

    # The names are put together from the entries themselves. Cutting them out of full paths goes wrong
    # when the directory was named in its short form (C:\Users\LONGNA~1) and its files are not.
    function Get-Below([IO.DirectoryInfo]$Parent, [string]$Prefix) {
        foreach ($file in ($Parent.GetFiles() | Sort-Object Name)) {
            [pscustomobject]@{
                path   = $Prefix + $file.Name
                bytes  = $file.Length
                sha256 = Get-YavSha256 -Path $file.FullName
            }
        }

        foreach ($child in ($Parent.GetDirectories() | Sort-Object Name)) {
            Get-Below $child ($Prefix + $child.Name + '/')
        }
    }

    Get-Below (Get-Item -LiteralPath $Directory) ''
}

function Test-YavPackage {
    <#
    .SYNOPSIS
        Compares the files of a package with its manifest. Returns the problems; none means the package is complete and unchanged.
    #>
    param([Parameter(Mandatory)][string]$Directory)

    $manifestPath = Join-Path $Directory 'package-manifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath)) { return @("package-manifest.json is missing in $Directory.") }

    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $problems = @()
    foreach ($file in $manifest.files) {
        $path = Join-Path $Directory ($file.path -replace '/', '\')
        if (-not (Test-Path -LiteralPath $path)) { $problems += "$($file.path) is missing."; continue }
        $hash = Get-YavSha256 -Path $path
        if ($hash -ne $file.sha256) { $problems += "$($file.path) differs from the file that was packaged." }
    }

    return $problems
}
