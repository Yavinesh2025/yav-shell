<#
.SYNOPSIS
    Builds the package of YAV Shell: dist\yav.exe, one program that installs itself.
.DESCRIPTION
    1. Runs the automated tests (fixtures only; no inference is requested), unless -SkipTests.
    2. Publishes yav.exe for Windows x64 as one file that holds everything it needs: the .NET runtime, the
       native SQLite library, and what 'yav install' puts next to it (license, notices, documentation,
       examples). Nothing has to be installed separately, and the file runs from wherever it is.
    3. Writes dist\yav.exe.sha256, in the format sha256sum reads ("<sha256>  yav.exe").

    Started, dist\yav.exe offers to install itself for the current user; 'yav.exe install' does it without
    asking. Nothing is code-signed. The output of this script says so, and Windows may warn when a copy that
    was downloaded is started for the first time.
.PARAMETER SkipTests
    Does not run the automated tests before packaging.
.PARAMETER HangSeconds
    Given to scripts\test.ps1: when no test has begun or ended for that many seconds, the tests end and name
    the tests that were still running. 0, the default, sets no limit.
#>
[CmdletBinding()]
param(
    [switch]$SkipTests,
    [ValidateRange(0, 86400)]
    [int]$HangSeconds = 0
)

. "$PSScriptRoot\env.ps1"
. "$PSScriptRoot\package-tools.ps1"

$console = Join-Path $RepoRoot 'src\Yav.Console\Yav.Console.csproj'
$version = Get-YavProductVersion -Repository $RepoRoot

if (-not $SkipTests) {
    Write-Host 'Running the automated tests (fixtures only; no inference is requested)...'
    & (Join-Path $PSScriptRoot 'test.ps1') -Configuration Release -HangSeconds $HangSeconds
}

$dist = Join-Path $RepoRoot 'dist'
$publish = Join-Path $RepoRoot 'artifacts\package\single-file'
if (Test-Path $publish) { Remove-Item -Path $publish -Recurse -Force }

# The native SQLite library goes into the file as well. It is unpacked into a directory of the user when the
# program starts for the first time; see docs\performance.md for what that costs.
$arguments = @(
    'publish', $console, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '--nologo', '-v', 'minimal',
    '-o', $publish,
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:PublishReadyToRun=true',
    '-p:DebugType=none', '-p:DebugSymbols=false',
    '-p:GenerateDocumentationFile=false'
)

Write-Host "Publishing yav.exe $version (one file, self-contained, win-x64)..."
& $DotNet @arguments
if ($LASTEXITCODE -ne 0) { throw "Publishing failed with exit code $LASTEXITCODE." }

# Anything next to yav.exe would be missing wherever the program is copied without it.
$published = @(Get-ChildItem -LiteralPath $publish -Recurse -File)
$others = @($published | Where-Object { $_.Name -ne 'yav.exe' })
if (-not ($published | Where-Object { $_.Name -eq 'yav.exe' })) { throw "yav.exe was not produced in $publish." }
if ($others.Count -gt 0) {
    throw "The program is not one file: publishing also produced $(($others | ForEach-Object { $_.Name }) -join ', ')."
}

New-Item -ItemType Directory -Path $dist -Force | Out-Null
$program = Join-Path $dist 'yav.exe'
foreach ($old in $program, "$program.sha256") {
    if (Test-Path -LiteralPath $old) { Remove-Item -LiteralPath $old -Force }
}

Copy-Item -LiteralPath (Join-Path $publish 'yav.exe') -Destination $program
$hash = Write-YavChecksumFile -Path $program

$reported = (& $program --version | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $reported -ne "yav $version") {
    throw "dist\yav.exe did not start as expected: it ended with exit code $LASTEXITCODE and said '$reported'."
}

$signature = Get-AuthenticodeSignature -FilePath $program
$status = [string]$signature.Status
# A signature that is there but not valid (HashMismatch, NotTrusted, UnknownError, ...) is not "no signature".
if ($status -ne 'Valid' -and $status -ne 'NotSigned') {
    throw "The signature of dist\yav.exe is not valid: $status. $($signature.StatusMessage)"
}

Write-Host ''
Write-Host "Package:   $program  (version $version, $([Math]::Round((Get-Item $program).Length / 1MB, 1)) MB)"
Write-Host "SHA-256:   $hash  (in $program.sha256)"
Write-Host "Signed:    $(if ($status -eq 'Valid') { 'yes, by ' + $signature.SignerCertificate.Subject } else { 'NO. yav.exe is not code-signed.' })"
Write-Host ''
Write-Host 'Started, yav.exe offers to install itself for your account; yav.exe install does it without asking.'
Write-Host 'scripts\verify-package.ps1 checks the package the way a machine without .NET would use it.'
