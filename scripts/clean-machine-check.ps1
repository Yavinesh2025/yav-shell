<#
.SYNOPSIS
    The check that verify-package.ps1 runs: on this machine with a PATH that holds nothing but Windows, or inside
    Windows Sandbox. It runs in Windows PowerShell 5.1, because that is what a new Windows has.
.DESCRIPTION
    Every check is recorded with what it found. A check whose result is wrong does not stop the others. If
    something throws, the checks after it are not run, and 'The check ran to its end' fails with what was
    thrown. The result is written to -Results as JSON.

    yav is started hidden and with its input redirected, as a script starts it, so it cannot ask anything here:
    the offer to install itself at the start, and a window of its own that waits for Enter before it closes,
    are not exercised.

    Without -ShutDown nothing of the machine is changed: the program is installed with --no-path and
    --no-register only, into a directory below -Work, and the directory for temporary files is one below -Work
    as well.
#>
[CmdletBinding()]
param(
    # The yav.exe to check.
    [Parameter(Mandatory)][string]$Package,
    # The SHA-256 recorded for the package in its .sha256 file (dist\yav.exe.sha256). The copy checked here must have it.
    [Parameter(Mandatory)][string]$Sha256,
    # The version yav.exe has to report.
    [Parameter(Mandatory)][string]$Version,
    # Where the result is written as JSON, also when the check did not run to its end.
    [Parameter(Mandatory)][string]$Results,
    # The files the installation has to hold next to yav.exe, one line each, '<sha256>  <relative path>', as the
    # repository holds them. verify-package.ps1 writes it.
    [Parameter(Mandatory)][string]$Expected,
    # The directory for everything the check makes: the data directory, a project, and the directories that
    # 'yav install --dir' installs into; without -ShutDown also the directory for temporary files. Without -Work
    # a new one is made below %TEMP% and left there, which is what happens in Windows Sandbox: there -ShutDown
    # is given without -Work, temporary files go to the TEMP of the sandbox, and the installation with the
    # default options goes to %LOCALAPPDATA%\Programs\YavShell.
    [string]$Work,
    # Given only by the LogonCommand that verify-package.ps1 -Sandbox writes for Windows Sandbox, never on a real
    # machine: it shuts Windows down at the end, which ends the sandbox. Before that the check also looks for an
    # installed .NET, installs with the options a user gets by default - into the default directory, with the PATH
    # entry and the entry under "Installed apps" - and removes that installation again.
    [switch]$ShutDown
)

$ErrorActionPreference = 'Stop'
$checks = New-Object System.Collections.Generic.List[object]
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\YavShell'

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

function Get-FirstLine([string]$Text) {
    if (-not $Text) { return '' }
    return $Text.Trim().Split("`n")[0].Trim()
}

function Invoke-Yav([string]$Exe, [string[]]$Arguments, [string]$InputText, [hashtable]$Environment) {
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
    if ($Environment) {
        foreach ($name in $Environment.Keys) { $start.EnvironmentVariables[$name] = $Environment[$name] }
    }

    $watch = [Diagnostics.Stopwatch]::StartNew()
    $process = [Diagnostics.Process]::Start($start)
    if ($InputText) { $process.StandardInput.Write($InputText) }
    $process.StandardInput.Close()
    $output = $process.StandardOutput.ReadToEndAsync()
    $errors = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit(120000)) { $process.Kill(); throw "$Exe $($Arguments -join ' ') did not end within two minutes." }
    $watch.Stop()

    # The hidden CMD that 'yav uninstall' starts to delete the program after it has ended inherits the output handles
    # of yav and holds them open until it ends, about three seconds after yav. Each stream is waited for up to 30
    # seconds; after that its text is replaced by a note that says so, which every check that reads it shows.
    [void]$output.Wait(30000)
    [void]$errors.Wait(30000)
    $open = '(the output was still open 30 s after yav had ended)'
    [pscustomobject]@{
        ExitCode     = $process.ExitCode
        Output       = $(if ($output.IsCompleted) { $output.Result } else { $open })
        Error        = $(if ($errors.IsCompleted) { $errors.Result } else { $open })
        Milliseconds = $watch.ElapsedMilliseconds
    }
}

function Get-Failure([System.Management.Automation.ErrorRecord]$Record, [string]$Text) {
    # Why something could not be read, with the beginning of what it was.
    $line = Get-FirstLine $Text
    if ($line.Length -gt 120) { $line = $line.Substring(0, 120) + '...' }
    return "$($Record.Exception.Message) (it began with '$line')"
}

function Get-Listing([string]$Path, [switch]$Recurse) {
    # The files below a directory. A directory that does not exist has none; one that cannot be listed, wholly or in
    # part, is said in .Problem, so that it does not count as empty. .Relative holds the paths below the directory.
    $listing = [pscustomobject]@{ Files = @(); Relative = @(); Problem = '' }
    if (-not (Test-Path -LiteralPath $Path)) { return $listing }
    $listing.Files = @(Get-ChildItem -LiteralPath $Path -Recurse:$Recurse -File -Force -ErrorAction SilentlyContinue -ErrorVariable failed)
    if ($failed) { $listing.Problem = "could not be listed completely: $($failed[0].Exception.Message)" }
    # The full names spell the directory as Windows does, which may differ from $Path (a short name such as
    # TRAINE~1 in TEMP is spelled out), so they are cut at the directory as Get-Item spells it.
    $root = (Get-Item -LiteralPath $Path -Force).FullName.TrimEnd('\')
    $listing.Relative = @($listing.Files | ForEach-Object { $_.FullName.Substring($root.Length + 1) })
    return $listing
}

function Get-Registration {
    # The values of the entry under "Installed apps" that an installation writes, or '(none)'.
    if (-not (Test-Path $uninstallKey)) { return '(none)' }
    $entry = Get-ItemProperty -Path $uninstallKey
    return (@('DisplayName', 'DisplayVersion', 'InstallLocation', 'UninstallString') | ForEach-Object {
        $value = $entry.PSObject.Properties[$_]
        "$_=$(if ($value) { $value.Value } else { '(missing)' })"
    }) -join '; '
}

$where = if ($ShutDown) { 'Windows Sandbox: a new Windows without any software of this machine' } else { 'this machine, with a PATH that holds nothing but Windows' }
try {
    if (-not $Work) { $Work = Join-Path $env:TEMP ('yav check ' + [Guid]::NewGuid().ToString('N').Substring(0, 8)) }
    New-Item -ItemType Directory -Path $Work -Force | Out-Null
    $Work = [IO.Path]::GetFullPath($Work)
    $env:YAV_HOME = Join-Path $Work 'data'
    if (-not $ShutDown) {
        # The single file unpacks its native library below the directory for temporary files. Here that is a
        # directory of the check, so that nothing is left in the one of the user.
        $temp = Join-Path $Work 'temp'
        New-Item -ItemType Directory -Path $temp -Force | Out-Null
        $env:TMP = $temp
        $env:TEMP = $temp
    }

    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    $git = Get-Command git -ErrorAction SilentlyContinue
    Add-Check 'No .NET is on PATH' (-not $dotnet) $(if ($dotnet) { $dotnet.Source } else { '' })
    Add-Check 'No Git is on PATH' (-not $git) $(if ($git) { $git.Source } else { '' })
    if ($ShutDown) {
        $runtimes = Test-Path (Join-Path $env:ProgramFiles 'dotnet')
        Add-Check 'No .NET is installed on this Windows' (-not $runtimes) (Join-Path $env:ProgramFiles 'dotnet')
    }

    $yav = [IO.Path]::GetFullPath($Package)
    $exists = Test-Path -LiteralPath $yav -PathType Leaf
    Add-Check 'The package is one program, yav.exe' $exists $(if ($exists) { '{0:N1} MB' -f ((Get-Item -LiteralPath $yav).Length / 1MB) } else { $yav })
    $hash = if ($exists) { Get-Sha256 $yav } else { '' }
    Add-Check 'yav.exe is the program that was built' ($hash -eq $Sha256.ToLowerInvariant()) "SHA-256 $hash"

    # Without a signature there is no certificate to read from the file, and that is the one failure that means
    # "not signed". Anything else is recorded as what it is.
    $signer = $null
    $signatureProblem = ''
    try { $signer = [System.Security.Cryptography.X509Certificates.X509Certificate]::CreateFromSignedFile($yav).Subject }
    catch [System.Security.Cryptography.CryptographicException] { $signer = $null }
    catch { $signatureProblem = $_.Exception.Message }
    Add-Check 'yav.exe is not code-signed, as the package says' (-not $signer -and -not $signatureProblem) $(
        if ($signer) { "signed by $signer" } elseif ($signatureProblem) { "the signature could not be read: $signatureProblem" } else { 'no Authenticode signature' })

    $cold = Invoke-Yav $yav @('--version')
    Add-Check 'yav --version' ($cold.ExitCode -eq 0 -and $cold.Output.Trim() -eq "yav $Version") "$($cold.Output.Trim()), $($cold.Milliseconds) ms the first time"
    $again = @(1..5 | ForEach-Object { Invoke-Yav $yav @('--version') })
    $times = @($again | ForEach-Object { $_.Milliseconds } | Sort-Object)
    $odd = @($again | Where-Object { $_.ExitCode -ne 0 -or $_.Output.Trim() -ne "yav $Version" })
    Add-Check 'yav --version, started five more times' ($odd.Count -eq 0) $(
        "median $($times[2]) ms, slowest $($times[4]) ms" + $(if ($odd.Count -gt 0) { "; $($odd.Count) of them ended otherwise, first: exit code $($odd[0].ExitCode), '$(Get-FirstLine ($odd[0].Output + $odd[0].Error))'" } else { '' }))

    $help = Invoke-Yav $yav @('--help')
    Add-Check 'yav --help' ($help.ExitCode -eq 0 -and $help.Output -match 'yav run --project' -and $help.Output -match 'yav install') ''

    $bogus = Invoke-Yav $yav @('--bogus')
    Add-Check 'An option that does not exist ends with exit code 64' ($bogus.ExitCode -eq 64) (Get-FirstLine $bogus.Error)

    $doctor = Invoke-Yav $yav @('doctor', '--json')
    $report = $null
    $unread = ''
    try { $report = $doctor.Output | ConvertFrom-Json } catch { $unread = '; not JSON: ' + (Get-Failure $_ ($doctor.Output + $doctor.Error)) }
    Add-Check 'yav doctor --json gives a report' ($null -ne $report -and $report.type -eq 'doctor') "exit code $($doctor.ExitCode), $($doctor.Milliseconds) ms$unread"
    if ($report) {
        $runtime = $report.checks | Where-Object { $_.name -eq 'Runtime' } | Select-Object -First 1
        $folder = Split-Path -Parent $yav
        Add-Check 'The .NET runtime that runs yav is the one in yav.exe' ($runtime -and $runtime.detail -match 'part of this installation' -and $runtime.detail -like "*$folder*") "$(if ($runtime) { $runtime.detail })"
        $data = $report.checks | Where-Object { $_.name -eq 'Data directory' } | Select-Object -First 1
        Add-Check 'The data directory is the one that was named' ($data -and $data.detail -like "$env:YAV_HOME*") "$(if ($data) { $data.detail })"
        $agents = @($report.checks | Where-Object { $_.name -eq 'Installation' })
        Add-Check 'Agents that are not installed are reported, not assumed' ($agents.Count -gt 0 -and @($agents | Where-Object { $_.status -eq 'ok' }).Count -eq 0) (($agents | ForEach-Object { "$($_.area): $($_.status)" }) -join '; ')
    }

    # The database is SQLite, whose native library is part of the single file and unpacked when it starts.
    $database = Join-Path $env:YAV_HOME 'yav.db'
    Add-Check 'SQLite works from the single file: the database was created' (Test-Path -LiteralPath $database) $database
    $unpacked = Join-Path ([IO.Path]::GetTempPath()) '.net\yav'
    if ($env:DOTNET_BUNDLE_EXTRACT_BASE_DIR) { $unpacked = Join-Path $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR 'yav' }
    $unpackedListing = Get-Listing $unpacked -Recurse
    $library = @($unpackedListing.Files | Where-Object { $_.Name -eq 'e_sqlite3.dll' })
    Add-Check 'The native SQLite library was unpacked where .NET unpacks a single file' ($library.Count -gt 0) $(
        if ($library.Count -gt 0) { $library[0].FullName } else { "nothing below $unpacked" + $(if ($unpackedListing.Problem) { ", which $($unpackedListing.Problem)" } else { '' }) })

    $project = Join-Path $Work 'a project'
    New-Item -ItemType Directory -Path $project -Force | Out-Null
    Set-Content -Path (Join-Path $project 'app.txt') -Value 'one'
    $run = Invoke-Yav $yav @('run', '--project', $project, '--task', 'Change nothing.', '--json')
    $last = $null
    $unread = ''
    $lastLine = [string]($run.Output.Trim().Split("`n") | Select-Object -Last 1)
    try { $last = $lastLine | ConvertFrom-Json } catch { $unread = '; the last line is not JSON: ' + (Get-Failure $_ ($lastLine + $run.Error)) }
    Add-Check 'yav run without models is blocked and says why' ($run.ExitCode -eq 2 -and $last -and $last.outcome -eq 'blocked' -and @($last.problems).Count -gt 0) "exit code $($run.ExitCode): $(if ($last) { @($last.problems | ForEach-Object { $_.code }) -join ', ' })$unread"
    Add-Check 'The project was not changed' ((Get-Content (Join-Path $project 'app.txt') -Raw).Trim() -eq 'one' -and @(Get-ChildItem $project -Force).Count -eq 1) ''

    $shell = Invoke-Yav $yav @($project) "/help`n/status`n/exit`n"
    Add-Check 'The shell reads commands from a pipe and ends' ($shell.ExitCode -eq 0 -and $shell.Output -match 'YAV Shell' -and $shell.Output -match 'State saved') "$($shell.Milliseconds) ms"
    Add-Check 'Nothing but plain text is written to a pipe' (-not $shell.Output.Contains([string][char]27)) ''

    # Installed into a directory of the check: a name with blanks, brackets and a letter outside ASCII.
    $installed = Join-Path $Work ('installed here [' + [char]0x00FC + ']')
    $pathBefore = [Environment]::GetEnvironmentVariable('Path', 'User')
    # The values, not only whether the entry is there: a --no-register that did not work would overwrite the entry of
    # an installation this machine has, which is still there afterwards.
    $registeredBefore = Get-Registration
    $install = Invoke-Yav $yav @('install', '--dir', $installed, '--no-path', '--no-register')
    $program = Join-Path $installed 'yav.exe'
    $there = Test-Path -LiteralPath $program -PathType Leaf
    Add-Check 'yav install --dir installs into the directory that was named' ($install.ExitCode -eq 0 -and $there) "exit code $($install.ExitCode): $(Get-FirstLine ($install.Output + $install.Error))"
    Add-Check 'The installed program is the program of the package' ($there -and (Get-Sha256 $program) -eq $hash) ''
    $after = if ($there) { Invoke-Yav $program @('--version') } else { $null }
    Add-Check 'What was installed starts' ($after -and $after.ExitCode -eq 0 -and $after.Output.Trim() -eq "yav $Version") "$(if ($after) { $after.Output.Trim() })"

    # Every file of the repository that belongs next to yav.exe, with what the repository holds, and nothing else
    # but yav.exe and yav-install.json.
    $wanted = @{}
    foreach ($line in [IO.File]::ReadAllLines($Expected)) {
        if (-not $line.Trim()) { continue }
        $parts = [regex]::Match($line, '^(?<hash>[0-9a-f]{64})  (?<path>.+)$')
        if (-not $parts.Success) { throw "$Expected has a line that is not '<sha256>  <relative path>': $line" }
        $wanted[$parts.Groups['path'].Value] = $parts.Groups['hash'].Value
    }
    if ($wanted.Count -eq 0) { throw "$Expected names no file." }
    $installedListing = Get-Listing $installed -Recurse
    $present = $installedListing.Relative
    $missing = @($wanted.Keys | Where-Object { $present -notcontains $_ } | Sort-Object)
    $differ = @($wanted.Keys | Where-Object { $present -contains $_ -and (Get-Sha256 (Join-Path $installed $_)) -ne $wanted[$_] } | Sort-Object)
    $extra = @($present | Where-Object { -not $wanted.ContainsKey($_) -and $_ -ne 'yav.exe' -and $_ -ne 'yav-install.json' } | Sort-Object)
    $licenses = @($wanted.Keys | Where-Object { $_ -like 'licenses\*.txt' })
    $notes = @()
    if ($missing.Count -gt 0) { $notes += 'missing: ' + ($missing -join ', ') }
    if ($differ.Count -gt 0) { $notes += 'not as in the repository: ' + ($differ -join ', ') }
    if ($extra.Count -gt 0) { $notes += 'not expected: ' + ($extra -join ', ') }
    if ($licenses.Count -eq 0) { $notes += "$Expected names no file in licenses" }
    if ($installedListing.Problem) { $notes += "$installed $($installedListing.Problem)" }
    Add-Check 'The license, the notices, the license texts in licenses, the documentation and the examples are installed next to it, as the repository has them' (
        $there -and $notes.Count -eq 0) $(if ($notes.Count -gt 0) { $notes -join '; ' } else { "$($wanted.Count) files, $($licenses.Count) of them in licenses" })

    $manifest = $null
    $unread = 'yav-install.json could not be read'
    try { $manifest = Get-Content -LiteralPath (Join-Path $installed 'yav-install.json') -Raw | ConvertFrom-Json }
    catch { $unread = "yav-install.json could not be read: $($_.Exception.Message)" }
    $wrong = @()
    if ($manifest) {
        $wrong = @($manifest.files | Where-Object {
            $file = Join-Path $installed ($_.path -replace '/', '\')
            -not (Test-Path -LiteralPath $file -PathType Leaf) -or (Get-Sha256 $file) -ne $_.sha256
        } | ForEach-Object { $_.path })
    }
    Add-Check 'The installation lists every file it put there, as it is' ($manifest -and $manifest.version -eq $Version -and @($manifest.files).Count -gt 0 -and $wrong.Count -eq 0) $(if ($manifest) { "$(@($manifest.files).Count) files" + $(if ($wrong.Count -gt 0) { '; differ or missing: ' + ($wrong -join ', ') } else { '' }) } else { $unread })

    $registeredAfter = Get-Registration
    Add-Check 'With --no-path and --no-register, PATH and "Installed apps" are left alone' (
        [Environment]::GetEnvironmentVariable('Path', 'User') -eq $pathBefore -and $registeredAfter -eq $registeredBefore) $(
        if ($registeredAfter -ne $registeredBefore) { "the entry under ""Installed apps"" was $registeredBefore and is now $registeredAfter" } else { "entry under ""Installed apps"": $registeredAfter" })

    $foreign = Join-Path $Work 'not empty'
    New-Item -ItemType Directory -Path $foreign -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $foreign 'mine.txt') -Value 'mine'
    $refused = Invoke-Yav $yav @('install', '--dir', $foreign, '--no-path', '--no-register')
    $foreignLeft = @(Get-ChildItem -LiteralPath $foreign -Force | ForEach-Object { $_.Name })
    Add-Check 'A directory that holds something else is refused and left alone' (
        $refused.ExitCode -eq 5 -and $foreignLeft.Count -eq 1 -and (Get-Content -LiteralPath (Join-Path $foreign 'mine.txt') -Raw).Trim() -eq 'mine') "exit code $($refused.ExitCode): $(Get-FirstLine $refused.Error)"

    $other = Join-Path $Work 'not an installation'
    New-Item -ItemType Directory -Path $other -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $other 'yav.exe') -Value 'something of the user'
    $notRemoved = Invoke-Yav $yav @('uninstall', '--dir', $other)
    Add-Check 'yav uninstall leaves a directory alone that holds no installation' (
        $notRemoved.ExitCode -eq 5 -and (Get-Content -LiteralPath (Join-Path $other 'yav.exe') -Raw).Trim() -eq 'something of the user') "exit code $($notRemoved.ExitCode): $(Get-FirstLine $notRemoved.Error)"

    # Removed by the installed program itself, as "Installed apps" removes it, but hidden and without input like every
    # start here, so no window waits for Enter. '--dir' names the installation of the check: without it 'yav
    # uninstall' removes the installation that "Installed apps" names, before the one it runs from, and on this
    # machine that would be the user's own. A program cannot delete its own file while it runs: 'yav uninstall'
    # removes the other files of the installation, leaves yav.exe where it is with a yav-install.json that names only
    # it, and as its very last step starts a hidden CMD that deletes both about three seconds after yav has ended,
    # and then the directory if nothing else is in it ('my own file.txt' keeps it here). The loop waits for that
    # deletion. Invoke-Yav has usually waited for it already, because the CMD holds the output of yav open until it
    # ends, but the check does not rely on that.
    if ($there) { Set-Content -LiteralPath (Join-Path $installed 'my own file.txt') -Value 'mine' }
    $removed = if ($there) { Invoke-Yav $program @('uninstall', '--dir', $installed) } else { $null }
    $deadline = (Get-Date).AddSeconds(20)
    do {
        $leftListing = Get-Listing $installed -Recurse
        $left = @($leftListing.Files | ForEach-Object { $_.Name })
        if ($left.Count -le 1 -and -not $leftListing.Problem) { break }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)
    Add-Check 'yav uninstall removes what was installed and nothing else' (
        $removed -and $removed.ExitCode -eq 0 -and $left.Count -eq 1 -and $left[0] -eq 'my own file.txt' -and -not $leftListing.Problem) $(
        "$(if ($removed) { "exit code $($removed.ExitCode): $(Get-FirstLine ($removed.Output + $removed.Error)); " })left: $($left -join ', ')" +
        $(if ($leftListing.Problem) { "; $installed $($leftListing.Problem)" } else { '' }))
    Add-Check 'The data of the user is kept by yav uninstall' ($removed -and (Test-Path -LiteralPath $database)) $env:YAV_HOME

    if ($ShutDown) {
        # Windows Sandbox only: installed with the options a user gets by default, into the default directory with the
        # PATH entry and the entry under "Installed apps". It is still a start from a script, hidden and without input:
        # not the offer at the start, and no window that waits for Enter.
        $target = Join-Path $env:LOCALAPPDATA 'Programs\YavShell'
        $default = Invoke-Yav $yav @('install')
        Add-Check 'yav install installs into the default directory' ($default.ExitCode -eq 0 -and (Test-Path -LiteralPath (Join-Path $target 'yav.exe'))) "exit code $($default.ExitCode): $(Get-FirstLine ($default.Output + $default.Error))"

        $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
        $onPath = @(([string]$userPath).Split(';') | Where-Object { $_.Trim().TrimEnd('\') -ieq $target }).Count -eq 1
        Add-Check 'The directory is on the PATH of the user, once' $onPath ([string]$userPath)

        $entry = if (Test-Path $uninstallKey) { Get-ItemProperty -Path $uninstallKey } else { $null }
        Add-Check 'The installation appears under "Installed apps"' (
            $entry -and $entry.DisplayName -eq 'YAV Shell' -and $entry.DisplayVersion -eq $Version -and
            $entry.UninstallString -eq ('"' + (Join-Path $target 'yav.exe') + '" uninstall')) "$(if ($entry) { $entry.UninstallString })"

        # A console opened after the installation gets the PATH Windows puts together from the machine's and the user's.
        $fresh = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + $userPath
        $byName = Invoke-Yav (Join-Path $env:SystemRoot 'System32\cmd.exe') @('/d', '/c', 'yav --version') $null @{ PATH = $fresh }
        Add-Check 'yav is found by its name in a console opened after the installation' ($byName.Output.Trim() -eq "yav $Version") $byName.Output.Trim()

        # The command "Installed apps" runs to remove it, hidden and without input as well. The hidden CMD that yav
        # starts as its very last step deletes the program and its yav-install.json about three seconds after yav has
        # ended, and then the directory, which holds nothing else. That is waited for; then the directory has to be gone.
        $gone = Invoke-Yav (Join-Path $target 'yav.exe') @('uninstall')
        $deadline = (Get-Date).AddSeconds(20)
        while ((Test-Path -LiteralPath $target) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
        $stayed = Test-Path -LiteralPath $target
        $found = "exit code $($gone.ExitCode): $(Get-FirstLine ($gone.Output + $gone.Error))"
        if ($stayed) {
            $insideListing = Get-Listing $target -Recurse
            $inside = @($insideListing.Files | ForEach-Object { $_.Name })
            $found += "; $target is still there" + $(if ($inside.Count -gt 0) { ', with ' + ($inside -join ', ') } else { ', with no file' }) +
                $(if ($insideListing.Problem) { ", and $($insideListing.Problem)" } else { '' })
        }
        Add-Check 'After yav uninstall the installation directory is gone, the program that removed itself included' (
            $gone.ExitCode -eq 0 -and -not $stayed) $found

        # Nothing of it is left elsewhere either, after that wait: no yav that still runs from there, and no copy of
        # the program or file being written (yav-removed-*, *.partial-*) beside the directory or among the
        # temporary files.
        $leftovers = New-Object System.Collections.Generic.List[string]
        foreach ($running in @(Get-Process -Name 'yav' -ErrorAction SilentlyContinue)) {
            $runningPath = $running.Path
            if ($runningPath -and $runningPath.StartsWith($target + '\', [StringComparison]::OrdinalIgnoreCase)) { $leftovers.Add("a yav that still runs: $runningPath") }
        }
        foreach ($place in @((Split-Path -Parent $target), $env:TEMP)) {
            $placeListing = Get-Listing $place
            if ($placeListing.Problem) { $leftovers.Add("$place $($placeListing.Problem)") }
            foreach ($file in $placeListing.Files) {
                if ($file.Name -like 'yav-removed-*' -or $file.Name -like '*.partial-*') { $leftovers.Add($file.FullName) }
            }
        }
        Add-Check 'After yav uninstall nothing of the installation is left elsewhere' ($leftovers.Count -eq 0) ($leftovers -join '; ')

        $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
        $named = @()
        if (@(([string]$userPath).Split(';') | Where-Object { $_.Trim().TrimEnd('\') -ieq $target }).Count -gt 0) { $named += 'the PATH of the user' }
        if (Test-Path $uninstallKey) { $named += '"Installed apps"' }
        Add-Check 'After yav uninstall neither the PATH nor "Installed apps" names it' ($named.Count -eq 0) $(if ($named.Count -gt 0) { 'still named by ' + ($named -join ' and ') } else { '' })
    }
}
catch {
    Add-Check 'The check ran to its end' $false ($_.Exception.Message + ' at ' + $_.InvocationInfo.PositionMessage)
}
finally {
    try {
        [pscustomobject]@{
            where   = $where
            at      = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
            windows = [Environment]::OSVersion.VersionString
            version = $Version
            checks  = $checks
        } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $Results -Encoding UTF8
    }
    finally {
        # Also when the result could not be written: the sandbox ends either way instead of staying open, and
        # verify-package.ps1 says that it gave no result when its time is up.
        if ($ShutDown) { & "$env:SystemRoot\System32\shutdown.exe" /s /t 3 /f }
    }
}
