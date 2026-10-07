# Functions the build scripts share: the version of the product, the SHA-256 of a file, and the file of checksums
# that is published next to the package. Dot-source this file.
# Everything here runs in Windows PowerShell 5.1 as well as in PowerShell 7, because a new Windows has only the former.

Set-StrictMode -Version Latest

function Resolve-YavPath {
    # .NET resolves a relative path against the current directory of the process, which Set-Location does not
    # change, and PowerShell against its own current location.
    param([Parameter(Mandatory)][string]$Path)
    $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path)
}

function Get-YavProductVersion {
    <#
    .SYNOPSIS
        The version of the product: the Version that Directory.Build.props names. yav.exe reports its
        InformationalVersion with --version, which the file sets separately and which has to be the same;
        package.ps1 and the tests check that.
    #>
    param([Parameter(Mandatory)][string]$Repository)

    [xml]$props = [System.IO.File]::ReadAllText((Resolve-YavPath (Join-Path $Repository 'Directory.Build.props')))
    $versions = @($props.Project.PropertyGroup | Where-Object { $_.PSObject.Properties['Version'] } | ForEach-Object { [string]$_.Version })
    if ($versions.Count -ne 1 -or -not $versions[0]) { throw 'Directory.Build.props does not name exactly one Version.' }
    return $versions[0]
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
        $stream = [System.IO.File]::OpenRead((Resolve-YavPath $Path))
        try { $hash = $algorithm.ComputeHash($stream) }
        finally { $stream.Dispose() }
    }
    finally {
        $algorithm.Dispose()
    }

    [System.BitConverter]::ToString($hash).Replace('-', '').ToLowerInvariant()
}

function Get-YavPackageFiles {
    <#
    .SYNOPSIS
        The files that 'yav install' has to put next to yav.exe, as relative paths with '\': the license, the
        notices, the README, docs\*.md, licenses\*.txt and everything below examples. It is the rule of the
        EmbeddedResource items of src\Yav.Console\Yav.Console.csproj; BuildScriptTests compares the two.
    #>
    param([Parameter(Mandatory)][string]$Repository)

    $root = (Resolve-YavPath $Repository).TrimEnd('\')
    $files = New-Object System.Collections.Generic.List[string]
    foreach ($name in 'LICENSE.txt', 'THIRD-PARTY-NOTICES.md', 'README.md') { $files.Add($name) }
    foreach ($pattern in @(@('docs', '.md'), @('licenses', '.txt'))) {
        foreach ($file in [System.IO.Directory]::GetFiles((Join-Path $root $pattern[0]))) {
            # Compared here, not with a pattern: in Windows PowerShell '*.txt' also finds 'a.txt2'.
            if ([System.IO.Path]::GetExtension($file) -eq $pattern[1]) { $files.Add($pattern[0] + '\' + [System.IO.Path]::GetFileName($file)) }
        }
    }
    foreach ($file in [System.IO.Directory]::GetFiles((Join-Path $root 'examples'), '*', [System.IO.SearchOption]::AllDirectories)) {
        $files.Add($file.Substring($root.Length + 1))
    }

    return @($files | Sort-Object)
}

function Write-YavChecksumFile {
    <#
    .SYNOPSIS
        Writes <file>.sha256 next to the file, in the format sha256sum reads ("<sha256>  <name>"), and returns the SHA-256.
    #>
    param([Parameter(Mandatory)][string]$Path)

    $full = Resolve-YavPath $Path
    $hash = Get-YavSha256 -Path $full
    [System.IO.File]::WriteAllText("$full.sha256", "$hash  $([System.IO.Path]::GetFileName($full))`n", [System.Text.Encoding]::ASCII)
    return $hash
}

function Test-YavChecksumFile {
    <#
    .SYNOPSIS
        Compares a file with the checksum written next to it. Returns the problems; none means that the file is
        the one the checksum was written for.
    #>
    param([Parameter(Mandatory)][string]$Path)

    $Path = Resolve-YavPath $Path
    $name = [System.IO.Path]::GetFileName($Path)
    $sidecar = "$Path.sha256"
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return @("$name does not exist.") }
    if (-not (Test-Path -LiteralPath $sidecar -PathType Leaf)) { return @("$name.sha256 does not exist.") }

    # One line: the SHA-256, a blank, and then a blank or '*' before the name, as sha256sum writes it.
    $line = [System.IO.File]::ReadAllText($sidecar).Trim()
    $parts = [regex]::Match($line, '^(?<hash>[0-9a-fA-F]{64}) [ *](?<name>.+)$')
    if (-not $parts.Success) { return @("$name.sha256 does not have the form '<sha256>  <name>'.") }
    if ($parts.Groups['name'].Value -ne $name) { return @("$name.sha256 is the checksum of '$($parts.Groups['name'].Value)', not of $name.") }

    $actual = Get-YavSha256 -Path $Path
    if ($actual -ne $parts.Groups['hash'].Value.ToLowerInvariant()) {
        return @("$name has the SHA-256 $actual, but $name.sha256 says $($parts.Groups['hash'].Value).")
    }

    return @()
}
