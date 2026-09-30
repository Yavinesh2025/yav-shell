<#
.SYNOPSIS
    Builds the portable package of YAV Shell and, where the tool for it is installed, the installer.
.DESCRIPTION
    1. Publishes yav.exe for Windows x64 together with the .NET runtime, so that nothing has to be
       installed separately.
    2. Puts the program, the documentation, the examples and the per-user installer scripts into
       dist\yav-shell-<version>-win-x64 and writes a manifest with the SHA-256 of every file.
    3. Packs that directory into dist\yav-shell-<version>-win-x64-portable.zip.
    4. Builds dist\yav-shell-<version>-setup.exe from installer\yav-shell.iss when Inno Setup is
       installed. Otherwise it says that no installer was built.

    Nothing is code-signed. The manifest and the output of this script say so.
.PARAMETER SkipTests
    Does not run the automated tests before packaging.
.PARAMETER Layout
    'folder' (default) keeps the files next to yav.exe. 'single-file' packs them into yav.exe, which
    starts slower because it unpacks native libraries when it starts; see docs\performance.md.
#>
[CmdletBinding()]
param(
    [switch]$SkipTests,
    [ValidateSet('folder', 'single-file')]
    [string]$Layout = 'folder'
)

. "$PSScriptRoot\env.ps1"
. (Join-Path $RepoRoot 'installer\YavInstall.ps1')

$console = Join-Path $RepoRoot 'src\Yav.Console\Yav.Console.csproj'
[xml]$props = Get-Content (Join-Path $RepoRoot 'Directory.Build.props') -Raw
$version = [string]($props.Project.PropertyGroup | Where-Object { $_.PSObject.Properties['Version'] } | Select-Object -First 1).Version
if (-not $version) { throw 'The version could not be read from Directory.Build.props.' }

if (-not $SkipTests) {
    Write-Host 'Running the automated tests (fixtures only; no inference is requested)...'
    & (Join-Path $PSScriptRoot 'test.ps1') -Configuration Release
}

$name = "yav-shell-$version-win-x64"
$dist = Join-Path $RepoRoot 'dist'
$stage = Join-Path $dist $name
$publish = Join-Path $RepoRoot "artifacts\package\$Layout"
foreach ($path in $stage, $publish) {
    if (Test-Path $path) { Remove-Item -Path $path -Recurse -Force }
}

$arguments = @(
    'publish', $console, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '--nologo', '-v', 'minimal',
    '-o', $publish,
    '-p:PublishReadyToRun=true',
    '-p:DebugType=none', '-p:DebugSymbols=false',
    '-p:GenerateDocumentationFile=false'
)
if ($Layout -eq 'single-file') {
    $arguments += @('-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true')
}

Write-Host "Publishing yav.exe $version ($Layout, self-contained, win-x64)..."
& $DotNet @arguments
if ($LASTEXITCODE -ne 0) { throw "Publishing failed with exit code $LASTEXITCODE." }
if (-not (Test-Path (Join-Path $publish 'yav.exe'))) { throw "yav.exe was not produced in $publish." }

New-Item -ItemType Directory -Path $stage -Force | Out-Null
Copy-Item -Path (Join-Path $publish '*') -Destination $stage -Recurse -Force

foreach ($directory in 'docs', 'examples') {
    $from = Join-Path $RepoRoot $directory
    if (Test-Path $from) { Copy-Item -Path $from -Destination (Join-Path $stage $directory) -Recurse -Force }
}

foreach ($file in 'README.md', 'LICENSE.txt', 'THIRD-PARTY-NOTICES.md') {
    $from = Join-Path $RepoRoot $file
    if (Test-Path $from) { Copy-Item -Path $from -Destination $stage -Force }
}

foreach ($file in 'install.ps1', 'uninstall.ps1', 'YavInstall.ps1') {
    Copy-Item -Path (Join-Path $RepoRoot "installer\$file") -Destination $stage -Force
}

$signature = Get-AuthenticodeSignature -FilePath (Join-Path $stage 'yav.exe')
$signed = $signature.Status -eq 'Valid'
$files = @(Get-YavFileHashes -Directory $stage)
$manifest = [ordered]@{
    product       = 'YAV Shell'
    version       = $version
    runtime       = 'win-x64'
    layout        = $Layout
    selfContained = $true
    signed        = $signed
    signature     = if ($signed) { $signature.SignerCertificate.Subject } else { 'none: this package is not code-signed' }
    builtAt       = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    builtWith     = (& $DotNet --version)
    files         = $files
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -Path (Join-Path $stage 'package-manifest.json') -Encoding utf8

$zip = Join-Path $dist "$name-portable.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $stage -DestinationPath $zip -CompressionLevel Optimal
$zipHash = Get-YavSha256 -Path $zip
"$zipHash  $([IO.Path]::GetFileName($zip))" | Set-Content -Path "$zip.sha256" -Encoding ascii

$setup = $null
$iscc = @(
    (Get-Command iscc.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1

if ($iscc) {
    Write-Host "Building the installer with $iscc..."
    & $iscc "/DAppVersion=$version" "/DSourceDir=$stage" "/DOutputDir=$dist" (Join-Path $RepoRoot 'installer\yav-shell.iss') | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed with exit code $LASTEXITCODE." }
    $setup = Join-Path $dist "yav-shell-$version-setup.exe"
    if (-not (Test-Path $setup)) { throw "Inno Setup reported success, but $setup does not exist." }
    $setupHash = Get-YavSha256 -Path $setup
    "$setupHash  $([IO.Path]::GetFileName($setup))" | Set-Content -Path "$setup.sha256" -Encoding ascii
}

$size = (Get-ChildItem $stage -Recurse -File | Measure-Object -Property Length -Sum).Sum
Write-Host ''
Write-Host "Package:   $stage  ($($files.Count) files, $([Math]::Round($size / 1MB, 1)) MB)"
Write-Host "Portable:  $zip  ($([Math]::Round((Get-Item $zip).Length / 1MB, 1)) MB, SHA-256 $zipHash)"
if ($setup) {
    Write-Host "Installer: $setup"
}
else {
    Write-Host 'Installer: NOT BUILT. Inno Setup 6 is not installed; installer\yav-shell.iss is the script for it.'
    Write-Host '           The package installs without it: run install.ps1 from the unpacked package.'
}

Write-Host "Signed:    $(if ($signed) { 'yes, by ' + $signature.SignerCertificate.Subject } else { 'NO. Nothing in this package is code-signed.' })"
