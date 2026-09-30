<#
.SYNOPSIS
    One request, worked on by the models you name, from a scratch project to an applied change.
.DESCRIPTION
    THIS CONSUMES USAGE of the accounts the agents are signed in to. The parts that ask the models, run and
    resume, and "all", which contains run, do not start without -IAuthorizeUsage.

    It makes a scratch project from a task of the benchmark, in a directory of its own with a data directory of
    its own, and then does what a user does. The parts:

      setup    Asks no model. In the real shell, in a console: /models, /effort, /login, /test trust, /status,
               /doctor. An account route is acknowledged only when it is of the kind -Acknowledge names. The checks
               of the task are approved only with -TrustChecks: approved checks run on this machine, with your
               rights, whenever a candidate is checked. What was set up and approved is written to live-run.json in
               the directory.
      run      ASKS THE MODELS. yav run --project <scratch project> --prompt-file <the request of the task> --json.
               A directory holds one run; a second one there is refused. When the time given is up, or the script is
               stopped while the run goes on, yav.exe is ended, and the summary says so. yav.exe is tied to the
               script: if the script is killed, yav.exe and its agents end with it, and the directory says that a
               run was begun and left no summary.
      resume   ASKS THE MODELS. In the real shell: /resume, for a run that stopped because an agent asked for
               something and nobody could answer. What an agent asks for is declined: only a person grants
               access. To grant something, start yav yourself with the data directory of the run (it is printed)
               and answer at the keyboard. Its record says what the data of the run says was decided. A run that
               was begun and left no summary is not continued from here.
      inspect  Asks no model. In the real shell again: /history, /status, /diff, /review show, /usage, /latency
               and, with -Apply, /apply with the number of the run, after which the run has to be completed. Then
               the checks of the task are run in the project; with -Apply every one of them has to pass.

    "all" is setup, run and inspect. With -Apply, inspect is reached only when the run ended ready to apply.
    The parts after setup go on only in a directory the setup made (below artifacts\live-run, with live-run.json),
    take the task and the program from live-run.json, and refuse a -Task or -Yav that differs. They do nothing when
    the file of the task was changed after it was approved at setup. The script ends with a non-zero exit code when
    a part did not go through, and when the run did not go through: it did not end by itself, left no result,
    failed, or had its command line refused.

    Everything is kept in artifacts\live-run\<time>: what the console showed, the JSON output of the run and its
    summary, the data directory with the record of the run, and what the checks said at the end. The records of
    the parts that can be repeated are numbered: resume-1-..., inspect-1-..., and so on.
    Your own data directory of YAV is neither read nor changed.
.PARAMETER IAuthorizeUsage
    Your authorization, as the holder of the accounts, for the parts that ask the models.
.PARAMETER TrustChecks
    Approve the checks of the task in the shell (/test trust) during setup. Without it, setup stops before anything
    is done and names the commands that would be approved.
.PARAMETER ModelA
    Adapter and model that implement, for example "codex-app-server gpt-6-astra". YAV chooses no model for you.
.PARAMETER ModelB
    Adapter and model that review, for example "claude-cli opus".
.PARAMETER EffortA
    The effort for Model A: a value the model lists, or "maximum".
.PARAMETER EffortB
    The effort for Model B.
.PARAMETER Acknowledge
    The account routes you agree to, one for each provider the two models are billed through: codex or claude
    for its subscription, codex:api-key or claude:api-key for an API key that is billed per token. Setup stops
    before anything is done when a route the models need is not named. In the shell, /login is answered with yes
    only when the route the agent reports is of the kind you named; otherwise it is answered with no and setup
    stops, naming the route that was found. The routes that were acknowledged are shown with their labels and
    kept in live-run.json.
.PARAMETER Task
    The task of the benchmark to take, at setup. Default: the smallest one. The parts after setup take it from
    live-run.json.
.PARAMETER Part
    all, or one of setup, run, resume, inspect to go on in the directory given with -Directory.
.PARAMETER Directory
    The directory of a run that was set up, for the parts run, resume and inspect: the one the setup made below
    artifacts\live-run and printed.
.PARAMETER Yav
    The program to drive, at setup. Default: the newest package in dist. The parts after setup take it from
    live-run.json.
.PARAMETER Apply
    Apply the change with /apply in the part inspect.
.PARAMETER Minutes
    How long to wait for the run, or for the run that is continued, before it is stopped: 1 to 600. A run that is
    stopped has yav.exe ended; when it does not end within 30 seconds after that, the script says so.
.EXAMPLE
    scripts\live-run.ps1 -IAuthorizeUsage -TrustChecks -ModelA "codex-app-server gpt-6-astra" -EffortA max -ModelB "claude-cli opus" -EffortB max -Acknowledge codex,claude -Apply
.NOTES
    Needs PowerShell 7.2 or later (pwsh). Windows PowerShell 5.1 refuses to run it.
#>
#Requires -Version 7.2
[CmdletBinding()]
param(
    [switch]$IAuthorizeUsage,
    [switch]$TrustChecks,
    [string]$ModelA,
    [string]$ModelB,
    [string]$EffortA = 'maximum',
    [string]$EffortB = 'maximum',
    [string[]]$Acknowledge = @(),
    [string]$Task = '01-small-edit',
    [ValidateSet('all', 'setup', 'run', 'resume', 'inspect')]
    [string]$Part = 'all',
    [string]$Directory,
    [string]$Yav,
    [switch]$Apply,
    [ValidateRange(1, 600)]
    [int]$Minutes = 30
)

. "$PSScriptRoot\env.ps1"

function Get-LiveProperty {
    # A property that a line of JSON may lack. Strict mode does not allow reading a missing one directly.
    param($Object, [string]$Name)

    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Get-LiveFullPath([string]$Path) {
    # Also for a path that does not exist, and relative to where PowerShell is, not where .NET thinks it is.
    return $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path)
}

function Read-LiveRunOutput {
    <#
        What 'yav run --json' left in its output: the last line whose type is "result", and the number of the run,
        which any line may carry. A run that was stopped from outside leaves no result and can end in a line that
        was cut off, and a failure is reported without the number; neither may keep the summary from being written.
    #>
    param([Parameter(Mandatory = $true)][string]$Path)

    $result = $null
    $runId = $null
    if (Test-Path -LiteralPath $Path) {
        foreach ($line in [IO.File]::ReadAllLines((Get-LiveFullPath $Path))) {
            if ($line.Trim().Length -eq 0) { continue }
            try { $parsed = ConvertFrom-Json -InputObject $line -ErrorAction Stop } catch { continue }
            if ($parsed -isnot [System.Management.Automation.PSCustomObject]) { continue }
            $id = Get-LiveProperty $parsed 'runId'
            if ($id -is [string] -and $id.Length -gt 0) { $runId = $id }
            if ((Get-LiveProperty $parsed 'type') -eq 'result') { $result = $parsed }
        }
    }

    $ofResult = Get-LiveProperty $result 'runId'
    return [pscustomobject]@{
        resultWritten = $null -ne $result
        outcome       = $(if ($null -ne $result) { Get-LiveProperty $result 'outcome' } else { 'no result was written' })
        runId         = $(if ($ofResult -is [string] -and $ofResult.Length -gt 0) { $ofResult } else { $runId })
        state         = Get-LiveProperty $result 'state'
        reason        = Get-LiveProperty $result 'reason'
    }
}

function Get-LiveRunExitCode([string]$Outcome) {
    <#
        The exit code 'yav run' ends with for each way a run can end by itself (ExitCodes.For in Output\JsonOutput.cs),
        or null. 'failed' and 'invalid' have none here on purpose: a run that ended so did not go through.
    #>
    $codes = @{ ready_to_apply = 0; completed = 0; blocked = 2; approval_required = 3; rate_limited = 4; interrupted = 6; needs_reconciliation = 7 }
    if ($Outcome -and $codes.ContainsKey($Outcome)) { return $codes[$Outcome] }
    return $null
}

function Get-AfterRun {
    <#
        What follows the part run: whether "all" goes on to inspect, the exit code the script ends with, and why the
        run did not go through, if it did not. A run that did not end by itself, left no result, failed, had its
        command line refused, or ended with an exit code that is not the one of its outcome, ends the script with an
        error. So does, in "all" with -Apply, a run that did not end ready to apply: nothing is applied then.
    #>
    param([Parameter(Mandatory = $true)]$Summary, [Parameter(Mandatory = $true)][string]$Part, [switch]$Apply)

    $outcome = Get-LiveProperty $Summary 'outcome'
    $exitCode = Get-LiveProperty $Summary 'exitCode'
    $expected = Get-LiveRunExitCode $outcome
    $reason = $null
    if (-not [bool](Get-LiveProperty $Summary 'endedByItself')) { $reason = 'it did not end by itself' }
    elseif (-not [bool](Get-LiveProperty $Summary 'resultWritten')) { $reason = 'yav wrote no result' }
    elseif ($null -eq $expected) { $reason = "yav ended it with the outcome '$outcome' and the exit code $exitCode" }
    elseif ($null -eq $exitCode -or $exitCode -ne $expected) {
        $reason = "yav ended with the exit code $(if ($null -eq $exitCode) { 'that is not known' } else { $exitCode }), which is not the one of the outcome '$outcome' ($expected)"
    }

    $complete = $null -eq $reason
    $ready = $outcome -eq 'ready_to_apply'
    $refusedToApply = $Part -eq 'all' -and $Apply -and -not $ready
    return [pscustomobject]@{
        Inspect  = $Part -eq 'all' -and $complete -and -not $refusedToApply
        ExitCode = $(if (-not $complete -or $refusedToApply) { 1 } else { 0 })
        Reason   = $reason
    }
}

function Get-LiveRunState([string]$Directory) {
    <#
        What the directory says about its run: 'none' before one was begun, 'made' once its summary was written, and
        'begun' for a run that was begun and left no summary: the script that started it was ended before it could
        write one. What such a run did is not known to the script.
    #>
    if (Test-Path -LiteralPath (Join-Path $Directory 'run-summary.json')) { return 'made' }
    foreach ($name in 'run-begun.json', 'run-output.jsonl') {
        if (Test-Path -LiteralPath (Join-Path $Directory $name)) { return 'begun' }
    }

    return 'none'
}

function Get-LiveRunBegunText([string]$Directory) {
    # What is known of a run that was begun and left no summary, as the mark written before its program was started says it.
    $said = "A run was begun in $Directory and left no summary: the script that started it was ended while the run went on, before it could write one."
    $path = Join-Path $Directory 'run-begun.json'
    $begun = $null
    if (Test-Path -LiteralPath $path) {
        try { $begun = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json } catch { $begun = $null }
    }

    $processId = Get-LiveProperty $begun 'processId'
    if ($null -eq $processId) { return "$said It is not known whether its program was started." }
    if (Get-LiveProperty $begun 'tiedToScript') { return "$said Its program (process $processId) was tied to that script and ended with it." }
    return "$said Its program (process $processId) was not tied to that script. If it still runs, end it: Stop-Process -Id $processId."
}

function Assert-NoRunYet([string]$Directory) {
    # A second run would overwrite what the first one left, and the directory is the record of one run.
    switch (Get-LiveRunState -Directory $Directory) {
        'made' { throw "A run was made in $Directory already (run-summary.json); what it left is kept. A new run gets a new directory: -Part setup or -Part all." }
        'begun' { throw "$(Get-LiveRunBegunText -Directory $Directory) What it left is kept, and it is not run again. -Part inspect shows it; no model is asked. A new run gets a new directory: -Part setup or -Part all." }
    }
}

function Assert-LiveRunToContinue {
    # Resume continues only a run whose summary was written: of a run without one it is not known how far it got.
    param([Parameter(Mandatory = $true)][string]$Directory, [Parameter(Mandatory = $true)][string]$Program)

    switch (Get-LiveRunState -Directory $Directory) {
        'none' { throw "No run was made in $Directory yet. Part 'run' makes it." }
        'begun' {
            throw ("$(Get-LiveRunBegunText -Directory $Directory) It is not continued from here, because it is not known how far it got. " +
                "-Part inspect shows what it left; no model is asked. To continue it yourself, start yav with its data directory and use /resume there: " +
                "`$env:YAV_HOME = `"$(Join-Path $Directory 'home')`"; & `"$Program`" `"$(Join-Path $Directory 'project')`"")
        }
    }
}

function Write-LiveRunBegun {
    <#
        The mark that a run was begun in the directory. It is written before the program is started, and again once
        the program runs, with its process and whether it is tied to this script.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Directory,
        [Parameter(Mandatory = $true)][string]$Begun,
        [Parameter(Mandatory = $true)][string]$Program,
        $ProcessId = $null,
        [bool]$Tied = $false,
        [string]$Note = $null
    )

    [ordered]@{ begun = $Begun; program = $Program; processId = $ProcessId; tiedToScript = $Tied; note = $Note } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Directory 'run-begun.json') -Encoding utf8
}

function New-LiveProgramJob {
    <#
        A job object that ends the processes in it when its last handle is closed. Windows closes the handle also when
        this script is killed, so the program of the run, put into it right after its start, does not outlive the
        script and go on consuming usage. The agents are in a job of yav.exe and end with it.
    #>
    if (-not ('YavLiveRun.ProgramJob' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace YavLiveRun
{
    public static class ProgramJob
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct BasicLimits
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedLimits
        {
            public BasicLimits BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        private const uint KillOnJobClose = 0x2000;
        private const int ExtendedLimitInformation = 9;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateJobObjectW(IntPtr attributes, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr job, int informationClass, ref ExtendedLimits information, int length);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        public static IntPtr Create()
        {
            IntPtr job = CreateJobObjectW(IntPtr.Zero, null);
            if (job == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "No job object could be made.");
            }

            ExtendedLimits limits = new ExtendedLimits();
            limits.BasicLimitInformation.LimitFlags = KillOnJobClose;
            if (!SetInformationJobObject(job, ExtendedLimitInformation, ref limits, Marshal.SizeOf(typeof(ExtendedLimits))))
            {
                int error = Marshal.GetLastWin32Error();
                CloseHandle(job);
                throw new Win32Exception(error, "The job object could not be set up.");
            }

            return job;
        }

        public static void Assign(IntPtr job, IntPtr process)
        {
            if (!AssignProcessToJobObject(job, process))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The program could not be put into the job object.");
            }
        }

        public static void Close(IntPtr job)
        {
            CloseHandle(job);
        }
    }
}
'@
    }

    return [YavLiveRun.ProgramJob]::Create()
}

function Wait-LiveProgram {
    <#
        Waits for the program to end by itself, at most the given time, and says whether it did. It waits in short
        steps, because a wait inside one call cannot be stopped: Control+C takes effect only between the steps.
    #>
    param([Parameter(Mandatory = $true)]$Process, [Parameter(Mandatory = $true)][long]$Milliseconds, [int]$Step = 500)

    $watch = [Diagnostics.Stopwatch]::StartNew()
    while (-not $Process.WaitForExit($Step)) {
        if ($watch.ElapsedMilliseconds -ge $Milliseconds) { return $false }
    }

    return $true
}

function Stop-LiveProgram {
    <#
        Ends the program of the run and waits for it, for a bounded time. The agents belong to a job of yav.exe and end
        with it, so it alone is ended. The process object holds a handle of exactly that process, so no other process
        that was given its id since can be hit. Says whether it ended, and what to tell the holder when it did not.
    #>
    param([Parameter(Mandatory = $true)]$Process, [int]$Seconds = 30)

    $problem = $null
    try { $Process.Kill() }
    catch {
        # It ended in the meantime, or it cannot be ended; the wait tells which.
        $problem = $_.Exception.Message
    }

    $ended = [bool]$Process.WaitForExit($Seconds * 1000)
    return [pscustomobject]@{
        Ended   = $ended
        Message = $(if ($ended) { "yav.exe (process $($Process.Id)) was ended." }
            else { "yav.exe (process $($Process.Id)) did not end within $Seconds seconds after it was told to$(if ($problem) { " ($problem)" }). It may still run and consume usage. End it: Stop-Process -Id $($Process.Id)" })
    }
}

function Get-LiveRecordName {
    <#
        The name the records of a part that can be repeated are kept under: resume-1, resume-2 and so on, one higher
        than the highest that is there, so that a repetition overwrites nothing. The name is taken by making the first
        record of the part, the log of its test, which cannot be made twice: two parts that start at once in one
        directory get names of their own.
    #>
    param([Parameter(Mandatory = $true)][string]$Directory, [Parameter(Mandatory = $true)][string]$Part)

    $highest = 0
    $pattern = '^' + [regex]::Escape($Part) + '-(\d+)-'
    foreach ($file in @(Get-ChildItem -LiteralPath $Directory -File)) {
        $match = [regex]::Match($file.Name, $pattern)
        if ($match.Success -and [int]$match.Groups[1].Value -gt $highest) { $highest = [int]$match.Groups[1].Value }
    }

    for ($number = $highest + 1; ; $number++) {
        $log = Join-Path (Get-LiveFullPath $Directory) "$Part-$number-test.log"
        try {
            [IO.File]::Open($log, [IO.FileMode]::CreateNew).Dispose()
            return "$Part-$number"
        }
        catch {
            # Taken by a part that started at the same time; anything else is not a question of the name.
            if (-not (Test-Path -LiteralPath $log)) { throw }
        }
    }
}

function Test-LiveTestLog {
    <#
        True when the log of 'dotnet test' shows that exactly one test ran, the one of the part, and that it passed.
        A filter that matches no test, and a test that was skipped, end with exit code 0 as well.
    #>
    param([Parameter(Mandatory = $true)][string]$Path, [Parameter(Mandatory = $true)][string]$Test)

    if (-not (Test-Path -LiteralPath $Path)) { return $false }
    $text = Get-Content -LiteralPath $Path -Raw
    if ($null -eq $text) { return $false }
    $passed = @([regex]::Matches($text, '(?m)^\s*Passed\s+(\S+)') | ForEach-Object { $_.Groups[1].Value })
    $others = [regex]::Matches($text, '(?m)^\s*(Failed|Skipped)\s+\S').Count
    $total = [regex]::Match($text, '(?m)^\s*Total tests:\s*(\d+)\s*$')
    return ($passed.Count -eq 1) -and ($passed[0] -like "*LiveRunTests.$Test*") -and ($others -eq 0) -and $total.Success -and ($total.Groups[1].Value -eq '1')
}

function Set-LiveVariable([string]$Name, $Value) {
    # $null removes the variable. PowerShell hands [Environment] an empty text for $null, and a variable that is empty
    # is still there for every program started afterwards. The value is not typed, for the same reason.
    if ($null -eq $Value) { [Environment]::SetEnvironmentVariable($Name, [NullString]::Value) }
    else { [Environment]::SetEnvironmentVariable($Name, [string]$Value) }
}

function Remove-HostingSessionVariables {
    <#
        A Claude Code session gives these to the programs it starts (seen with 2.1.284). When this script is started
        from such a session they are not passed on: the agents are to be started the way a person at a terminal
        starts them. The last three are not Claude Code's own, and a user may have set them on purpose, so they are
        taken out only in such a session. Variables you set yourself to configure an agent are not among them.
        Returns what was taken out, for Restore-HostingSessionVariables.
    #>
    $names = @('CLAUDECODE', 'CLAUDE_CODE_ENTRYPOINT', 'CLAUDE_CODE_SESSION_ID', 'CLAUDE_CODE_CHILD_SESSION',
        'CLAUDE_CODE_SESSION_ATTENDED', 'CLAUDE_CODE_MESSAGING_SOCKET', 'CLAUDE_CODE_MESSAGING_TOKEN', 'CLAUDE_CODE_EXECPATH',
        'CLAUDE_PID', 'CLAUDE_EFFORT')

    # Asked before anything is taken out, because CLAUDECODE itself is taken out.
    if ($null -ne [Environment]::GetEnvironmentVariable('CLAUDECODE')) {
        $names += 'GIT_EDITOR', 'NoDefaultCurrentDirectoryInExePath', 'COREPACK_ENABLE_AUTO_PIN'
    }

    $takenOut = @{}
    foreach ($variable in $names) {
        $value = [Environment]::GetEnvironmentVariable($variable)
        if ($null -ne $value) {
            $takenOut[$variable] = $value
            Set-LiveVariable $variable $null
        }
    }

    return $takenOut
}

function Restore-HostingSessionVariables([hashtable]$TakenOut) {
    foreach ($variable in @($TakenOut.Keys)) { Set-LiveVariable $variable $TakenOut[$variable] }
}

function Read-LiveTask {
    <#
        The task of the benchmark, read once: its definition, and the hash of the file it was read from. The setup
        records the hash as what was approved; the parts after it take the task only while the file still has it.
    #>
    param([Parameter(Mandatory = $true)][string]$Task, [string]$Tasks = (Join-Path $RepoRoot 'bench\tasks'))

    $path = Join-Path $Tasks "$Task\task.json"
    if (-not (Test-Path -LiteralPath $path)) { throw "There is no task '$Task' in bench\tasks." }
    $bytes = [IO.File]::ReadAllBytes((Get-LiveFullPath $path))
    return [pscustomobject]@{
        Definition = ((New-Object System.Text.UTF8Encoding($false)).GetString($bytes).TrimStart([char]0xFEFF) | ConvertFrom-Json)
        Hash       = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
    }
}

function Assert-LiveTaskApproved {
    <#
        A part after setup takes the task only as it was approved at setup. The request that is sent and the commands
        of the checks that are run are read from the file again, and the file may have been changed since.
    #>
    param([Parameter(Mandatory = $true)][string]$Directory, [Parameter(Mandatory = $true)][string]$Task, [string]$Approved, [Parameter(Mandatory = $true)][string]$Found)

    if (-not $Approved) {
        throw "live-run.json of $Directory does not record the task file that was approved at setup; it was set up by an earlier version of this script. Whether bench\tasks\$Task\task.json is still what was approved is not known, so nothing was done. Set up a new run."
    }

    if ($Approved -ne $Found) {
        throw "bench\tasks\$Task\task.json was changed after $Directory was set up: its request or the commands of its checks may not be what was approved. What was approved is in live-run.json. Nothing was done; set up a new run to approve the task as it is now."
    }
}

function Get-LiveTaskChecks {
    # The commands the checks of a task run, as /test trust shows them before they are approved.
    param([Parameter(Mandatory = $true)]$Definition)

    foreach ($check in @(Get-LiveProperty $Definition 'checks')) {
        $arguments = @(Get-LiveProperty $check 'args') -join ' '
        ('{0}: {1} {2}' -f (Get-LiveProperty $check 'id'), (Get-LiveProperty $check 'command'), $arguments).TrimEnd()
    }
}

function ConvertTo-LiveRoutes {
    <#
        The account routes the holder agrees to, in the form the parts are given: "codex:subscription", "claude:api-key".
        A provider named alone means its subscription. A value -File passes as one text is split at its commas. What is
        not a route the live run acknowledges is refused, and so is a second route of the same provider.
    #>
    param([string[]]$Acknowledge)

    $routes = [System.Collections.Generic.List[string]]::new()
    foreach ($given in @($Acknowledge | ForEach-Object { "$_" -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_.Length -gt 0 })) {
        $parts = $given.ToLowerInvariant() -split ':', 2
        $provider = $parts[0]
        $kind = $(if ($parts.Count -eq 2) { $parts[1] } else { 'subscription' })
        if ($provider -notin 'codex', 'claude') {
            throw "-Acknowledge '$given': '$provider' is not a provider the live run knows the account of. Name codex or claude, alone for its subscription or with the kind of route, for example claude:api-key."
        }

        # A cloud provider, or a route whose account the agent does not report, says nothing about who is billed and how.
        if ($kind -notin 'subscription', 'api-key') {
            throw "-Acknowledge '$given': '$kind' is not a kind of route the live run acknowledges. Kinds: subscription (the default) and api-key."
        }

        if (@($routes | Where-Object { $_.StartsWith("${provider}:") }).Count -gt 0) {
            throw "-Acknowledge names more than one route of $provider. Name the one route of it you agree to."
        }

        $routes.Add("${provider}:$kind")
    }

    $routes.ToArray()
}

function Get-LiveModelProvider([string]$Model) {
    # The provider whose account route a model of this adapter is billed through, as /login names it.
    $adapter = ($Model.Trim() -split '\s+', 2)[0].ToLowerInvariant()
    if ($adapter -in 'codex-app-server', 'codex-exec') { return 'codex' }
    if ($adapter -eq 'claude-cli') { return 'claude' }
    throw "'$adapter' is not an adapter the live run knows the account of: codex-app-server, codex-exec or claude-cli."
}

function Assert-LiveRoutesCover {
    <#
        Every account route the two models are billed through has to be one the holder agreed to. It is refused before
        anything is done: the setup would stop at /login, and a run would be blocked.
    #>
    param([string[]]$Routes, [Parameter(Mandatory = $true)][string]$ModelA, [Parameter(Mandatory = $true)][string]$ModelB)

    foreach ($model in @(@('A', $ModelA), @('B', $ModelB))) {
        $provider = Get-LiveModelProvider $model[1]
        if (@($Routes | Where-Object { $_.StartsWith("${provider}:") }).Count -eq 0) {
            throw "Model $($model[0]) ($($model[1])) is billed through the account route of $provider, and -Acknowledge names no route of $provider. Name the one you agree to, for example -Acknowledge $provider for its subscription. Nothing was done."
        }
    }
}

function Complete-LiveRunSetup {
    <#
        Marks the setup as gone through and records the routes that were acknowledged in the shell, with the labels the
        program showed. The part setup writes them to setup-routes.json; a setup without them did not go through.
        Returns a line for each route, to be shown.
    #>
    param([Parameter(Mandatory = $true)][string]$Directory, [Parameter(Mandatory = $true)][System.Collections.IDictionary]$Setup)

    $path = Join-Path $Directory 'setup-routes.json'
    if (-not (Test-Path -LiteralPath $path)) {
        throw "The setup left no record of the account routes it acknowledged ($path), so it did not go through."
    }

    $routes = @(Get-Content -LiteralPath $path -Raw -Encoding utf8 | ConvertFrom-Json | ForEach-Object { $_ })
    $Setup['routes'] = @($routes | ForEach-Object {
            [ordered]@{ provider = $_.provider; kind = $_.kind; label = $_.label; route = $_.route }
        })
    $Setup['setUp'] = $true
    Save-LiveRunSetup -Directory $Directory -Setup $Setup
    foreach ($route in $routes) { '{0}: {1} ({2})' -f $route.provider, $route.label, $route.kind }
}

function New-LiveSetupRecord {
    <#
        What is set up and approved, as live-run.json keeps it: the task with the hash of its file, its request and the
        commands of its checks as they were approved, the program, the models and efforts, and the routes agreed to.
        It is marked as set up only once the setup went through.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Task,
        [Parameter(Mandatory = $true)]$TaskFile,
        [Parameter(Mandatory = $true)][string]$Program,
        [Parameter(Mandatory = $true)][string]$ModelA,
        [Parameter(Mandatory = $true)][string]$ModelB,
        [Parameter(Mandatory = $true)][string]$EffortA,
        [Parameter(Mandatory = $true)][string]$EffortB,
        [string[]]$Routes = @()
    )

    return [ordered]@{
        task          = $Task
        program       = $Program
        modelA        = $ModelA
        modelB        = $ModelB
        effortA       = $EffortA
        effortB       = $EffortB
        acknowledged  = @($Routes)
        checksTrusted = $true
        taskHash      = $TaskFile.Hash
        request       = Get-LiveProperty $TaskFile.Definition 'request'
        checks        = @(Get-LiveTaskChecks -Definition $TaskFile.Definition)
        time          = (Get-Date).ToUniversalTime().ToString('o')
        setUp         = $false
    }
}

function Save-LiveRunSetup {
    # What was set up: the task, the program, the models and efforts, the routes that were acknowledged, and when.
    param([Parameter(Mandatory = $true)][string]$Directory, [Parameter(Mandatory = $true)][System.Collections.IDictionary]$Setup)

    # Deep enough for the routes, which ConvertTo-Json would otherwise write as the name of their type.
    $Setup | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Directory 'live-run.json') -Encoding utf8
}

function Assert-LiveRunDirectory {
    <#
        A part after setup goes on only in a directory the setup made: below the folder of the runs, holding
        live-run.json. A mistaken -Directory, such as the folder above a real project, would otherwise have the
        request sent or the checks of the task run in that project.
    #>
    param([Parameter(Mandatory = $true)][string]$Directory, [Parameter(Mandatory = $true)][string]$Runs)

    $full = (Get-LiveFullPath $Directory).TrimEnd('\')
    $root = (Get-LiveFullPath $Runs).TrimEnd('\') + '\'
    if (-not $full.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Directory is not a directory the setup made: those are below $root. Name the directory the setup printed."
    }

    if (-not (Test-Path -LiteralPath (Join-Path $full 'live-run.json'))) {
        throw "$Directory holds no live-run.json, the record of a setup, so it is not continued."
    }
}

function Resolve-LiveRunSetup {
    <#
        What a part after setup goes on with: the task and the program that were set up, as live-run.json records
        them. What is named again has to be what was set up, so that a request is never sent for the project of
        another task. A directory without that record is not continued: what was approved there is not known.
    #>
    param([Parameter(Mandatory = $true)][string]$Directory, [Parameter(Mandatory = $true)][System.Collections.IDictionary]$Named)

    # Looked up among the keys: $PSBoundParameters, which the script passes, has no method Contains.
    $namedKeys = @($Named.Keys)
    $path = Join-Path $Directory 'live-run.json'
    if (-not (Test-Path -LiteralPath $path)) {
        throw "$Directory has no live-run.json, the record of what was set up and approved, so it is not continued. Set up a new run."
    }

    $recorded = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    if (-not (Get-LiveProperty $recorded 'setUp')) {
        throw "The setup of $Directory did not go through, so the directory is not continued. Set up a new run."
    }

    $fields = [ordered]@{ Task = 'task'; Yav = 'program'; ModelA = 'modelA'; ModelB = 'modelB'; EffortA = 'effortA'; EffortB = 'effortB'; Acknowledge = 'acknowledged' }
    foreach ($parameter in $fields.Keys) {
        if ($namedKeys -notcontains $parameter) { continue }
        $given = @($Named[$parameter] | Where-Object { $null -ne $_ } | ForEach-Object { [string]$_ })
        $was = @(Get-LiveProperty $recorded $fields[$parameter] | Where-Object { $null -ne $_ } | ForEach-Object { [string]$_ })
        $same = switch ($parameter) {
            'Yav' { [string]::Equals((Get-LiveFullPath ($given -join '')), ($was -join ''), [StringComparison]::OrdinalIgnoreCase) }
            'Acknowledge' { ((@(ConvertTo-LiveRoutes $given) | Sort-Object) -join ',') -eq ((@(ConvertTo-LiveRoutes $was) | Sort-Object) -join ',') }
            default { [string]::Equals(($given -join ''), ($was -join ''), [StringComparison]::Ordinal) }
        }

        if (-not $same) {
            throw "-$parameter '$($given -join ',')' is not what $Directory was set up with ('$($was -join ',')'). The parts after setup take it from live-run.json; leave it out."
        }
    }

    return [pscustomobject]@{
        Task     = [string](Get-LiveProperty $recorded 'task')
        Program  = [string](Get-LiveProperty $recorded 'program')
        TaskHash = [string](Get-LiveProperty $recorded 'taskHash')
    }
}

function Get-LivePartVariables {
    <#
        What a part is told: the variables LivePart in tests\Yav.Tests\Live\LiveRunTests.cs reads. Of the parts that are
        tests, only the one that continues a run asks the models, and only it is told that the usage was authorized.
        The script tells it so after it checked -IAuthorizeUsage, and the part does nothing without it.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Record,
        [Parameter(Mandatory = $true)][string]$Directory,
        [Parameter(Mandatory = $true)][string]$Program,
        [Parameter(Mandatory = $true)][string]$Task,
        [Parameter(Mandatory = $true)][string]$TaskHash,
        [string]$ModelA,
        [string]$ModelB,
        [string]$EffortA,
        [string]$EffortB,
        [string[]]$Routes = @(),
        [switch]$TrustChecks,
        [switch]$Apply,
        [string]$RunId,
        [int]$Minutes,
        [switch]$UsageAuthorized
    )

    return @{
        YAV_LIVE_RUN              = $Name
        YAV_LIVE_RECORD           = $Record
        YAV_LIVE_DIR              = $Directory
        YAV_LIVE_EXE              = $Program
        YAV_LIVE_TASK             = $Task
        YAV_LIVE_TASK_HASH        = $TaskHash
        YAV_LIVE_MODEL_A          = $ModelA
        YAV_LIVE_MODEL_B          = $ModelB
        YAV_LIVE_EFFORT_A         = $EffortA
        YAV_LIVE_EFFORT_B         = $EffortB
        YAV_LIVE_ACKNOWLEDGED     = ($Routes -join ',')
        YAV_LIVE_TRUST_CHECKS     = $(if ($TrustChecks) { '1' } else { '0' })
        YAV_LIVE_APPLY            = $(if ($Apply) { '1' } else { '0' })
        YAV_LIVE_RUN_ID           = $(if ($RunId) { $RunId } else { $null })
        YAV_LIVE_MINUTES          = "$Minutes"
        YAV_LIVE_USAGE_AUTHORIZED = $(if ($UsageAuthorized -and $Name -eq 'resume') { '1' } else { $null })
    }
}

function Get-LivePartFailure {
    <#
        What is said when a part did not go through: what it may have done before it stopped. A part that continues a
        run may have asked the models by then, so it is never said that nothing was done.
    #>
    param([Parameter(Mandatory = $true)][string]$Name, [Parameter(Mandatory = $true)][string]$Record, [Parameter(Mandatory = $true)][string]$Directory, [bool]$Ran)

    $what = $(if ($Ran) { "The part '$Name' did not go through." }
        else { "The part '$Name' did not run as it should: $Record-test.log does not show exactly one test of it that ran and passed." })
    $done = switch ($Name) {
        'resume' { "It may have asked the models before it stopped, so usage may have been consumed. What happened is in $Record-screens.txt." }
        'inspect' { "No model was asked. With -Apply, /apply may have changed the project before it stopped; $Record-screens.txt shows how far it got." }
        default { 'No model was asked. The run was not set up, and it is not continued.' }
    }

    return "$what $done See $Directory."
}

function Invoke-Console {
    <#
        Runs the test of one part with the variables that tell it what to do, and keeps the log of 'dotnet test'.
        Afterwards, also when the part failed, the variables are what they were before.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Record,
        [Parameter(Mandatory = $true)][string]$Test,
        [Parameter(Mandatory = $true)][string]$Directory,
        [Parameter(Mandatory = $true)][System.Collections.IDictionary]$Variables,
        [Parameter(Mandatory = $true)][string]$DotNet,
        [Parameter(Mandatory = $true)][string]$Tests
    )

    # PowerShell does not tell $name from $Name, so the variable of the loop has a name of its own.
    $before = @{}
    foreach ($variable in $Variables.Keys) { $before[$variable] = [Environment]::GetEnvironmentVariable($variable) }
    $log = Join-Path $Directory "$Record-test.log"
    try {
        foreach ($variable in $Variables.Keys) { Set-LiveVariable $variable $Variables[$variable] }
        & $DotNet test $Tests -c Debug --nologo -v quiet --filter "FullyQualifiedName~LiveRunTests.$Test" --logger 'console;verbosity=normal' 2>&1 |
            Out-File -LiteralPath $log -Encoding utf8
        $code = $LASTEXITCODE
        if ($code -ne 0 -or -not (Test-LiveTestLog -Path $log -Test $Test)) {
            Get-Content -LiteralPath $log -Tail 60 | Write-Host
            throw (Get-LivePartFailure -Name $Name -Record $Record -Directory $Directory -Ran ($code -ne 0))
        }
    }
    finally {
        foreach ($variable in $before.Keys) { Set-LiveVariable $variable $before[$variable] }
    }
}

function Get-LiveRunId([string]$Directory) {
    $summary = Join-Path $Directory 'run-summary.json'
    if (-not (Test-Path -LiteralPath $summary)) { return $null }
    return Get-LiveProperty (Get-Content -LiteralPath $summary -Raw | ConvertFrom-Json) 'runId'
}

# Everything that can be refused is refused here, before anything is started or created.
$asksModels = $Part -in 'all', 'run', 'resume'
if ($asksModels -and -not $IAuthorizeUsage) {
    throw 'This asks real models and consumes usage of your accounts. It does not start without -IAuthorizeUsage.'
}

$routes = @(ConvertTo-LiveRoutes -Acknowledge $Acknowledge)
$setsUp = $Part -in 'all', 'setup'
if ($setsUp) {
    if (-not $ModelA -or -not $ModelB) { throw 'Name both models: -ModelA "<adapter> <model>" -ModelB "<adapter> <model>". YAV chooses no model for you.' }
    if ($Directory) { throw '-Directory continues a run that was set up. A new run gets a new directory.' }
    $taskFile = Read-LiveTask $Task
    $definition = $taskFile.Definition

    # Approved checks run on this machine with the rights of the user. The account holder decides that, not the script.
    if (-not $TrustChecks) {
        # Written as lines of their own: the message of an error is reflowed, and a list in it would not be readable.
        Write-Host "Setting up approves the checks of task '$Task' in the shell (/test trust). Approved checks run on this machine, with your rights, whenever a candidate is checked:"
        Get-LiveTaskChecks -Definition $definition | ForEach-Object { Write-Host "    $_" }
        throw 'Approving the checks above is your decision: add -TrustChecks to approve exactly these. Nothing was done.'
    }

    Assert-LiveRoutesCover -Routes $routes -ModelA $ModelA -ModelB $ModelB
    if (-not $Yav) { $Yav = Get-ChildItem -LiteralPath (Join-Path $RepoRoot 'dist') -Directory -Filter 'yav-shell-*-win-x64' | Sort-Object LastWriteTime | Select-Object -Last 1 | ForEach-Object { Join-Path $_.FullName 'yav.exe' } }
    if (-not $Yav -or -not (Test-Path -LiteralPath $Yav)) { throw 'yav.exe was not found. Build the package with scripts\package.ps1, or name the program with -Yav.' }
    $Yav = (Resolve-Path -LiteralPath $Yav).ProviderPath
}
else {
    if (-not $Directory) { throw "Part '$Part' goes on in a directory that was set up. Name it with -Directory." }
    Assert-LiveRunDirectory -Directory $Directory -Runs (Join-Path $RepoRoot 'artifacts\live-run')
    if (-not (Test-Path -LiteralPath (Join-Path $Directory 'project'))) { throw "$Directory has no project; its setup did not get that far." }
    $Directory = (Resolve-Path -LiteralPath $Directory).ProviderPath
    $setup = Resolve-LiveRunSetup -Directory $Directory -Named $PSBoundParameters
    $Task = $setup.Task
    $Yav = $setup.Program
    $taskFile = Read-LiveTask $Task
    Assert-LiveTaskApproved -Directory $Directory -Task $Task -Approved $setup.TaskHash -Found $taskFile.Hash
    $definition = $taskFile.Definition
    if ($Part -eq 'run') { Assert-NoRunYet -Directory $Directory }
    if ($Part -eq 'resume') { Assert-LiveRunToContinue -Directory $Directory -Program $Yav }
    if (-not (Test-Path -LiteralPath $Yav)) { throw "The program $Directory was set up with is not there any more: $Yav" }
}

$request = Get-LiveProperty $definition 'request'
$tests = Join-Path $RepoRoot 'tests\Yav.Tests\Yav.Tests.csproj'
$exitCode = 0
$takenOut = @{}
try {
    $takenOut = Remove-HostingSessionVariables

    if ($setsUp) {
        $Directory = Join-Path $RepoRoot ('artifacts\live-run\' + (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss'))
        New-Item -ItemType Directory -Path $Directory | Out-Null
        $Directory = (Resolve-Path -LiteralPath $Directory).ProviderPath
    }

    $project = Join-Path $Directory 'project'
    $dataDirectory = Join-Path $Directory 'home'
    $summaryPath = Join-Path $Directory 'run-summary.json'

    # What every part is told; each adds its name, its record and what it needs besides.
    $partOf = @{
        Directory = $Directory; Program = $Yav; Task = $Task; TaskHash = $taskFile.Hash; ModelA = $ModelA; ModelB = $ModelB
        EffortA = $EffortA; EffortB = $EffortB; Routes = $routes; TrustChecks = [bool]$TrustChecks; Apply = [bool]$Apply; Minutes = $Minutes
    }
    $console = @{ Directory = $Directory; DotNet = $DotNet; Tests = $tests }
    Write-Host "Live run in $Directory"
    Write-Host "  Program:  $Yav"
    Write-Host "  Task:     $Task - $request"

    if ($setsUp) {
        Write-Host "  Model A:  $ModelA, effort $EffortA"
        Write-Host "  Model B:  $ModelB, effort $EffortB"
        Write-Host "  Routes you agree to: $($routes -join ', ')"
        Write-Host '  Checks of the task: approved by you (-TrustChecks)'

        # Written before the shell is started and marked as set up once it went through: a setup that failed is not continued.
        $setupRecord = New-LiveSetupRecord -Task $Task -TaskFile $taskFile -Program $Yav -ModelA $ModelA -ModelB $ModelB -EffortA $EffortA -EffortB $EffortB -Routes $routes
        Save-LiveRunSetup -Directory $Directory -Setup $setupRecord
        Write-Host 'Setting up in the shell (no model is asked)...'
        Invoke-Console @console -Name 'setup' -Record 'setup' -Test 'The_shell_is_set_up' -Variables (Get-LivePartVariables @partOf -Name 'setup' -Record 'setup')
        Write-Host '  Routes acknowledged in the shell:'
        Complete-LiveRunSetup -Directory $Directory -Setup $setupRecord | ForEach-Object { Write-Host "    $_" }
        Write-Host "  done: $(Join-Path $Directory 'setup-screens.txt')"
    }

    $inspect = $Part -eq 'inspect'
    if ($Part -in 'all', 'run') {
        Write-Host "Asking the models: yav run --json (at most $Minutes minutes)..."
        $output = Join-Path $Directory 'run-output.jsonl'
        $errors = Join-Path $Directory 'run-errors.txt'
        $requestFile = Join-Path $Directory 'request.md'
        $noInput = Join-Path $Directory 'no-input.txt'

        # Marked before anything else: a run that was begun and left no summary is known as such to the next part.
        $begun = (Get-Date).ToUniversalTime().ToString('o')
        Write-LiveRunBegun -Directory $Directory -Begun $begun -Program $Yav
        [IO.File]::WriteAllText($requestFile, $request, (New-Object System.Text.UTF8Encoding($false)))
        [IO.File]::WriteAllText($noInput, '')

        # What the run writes goes into the files while it runs, so that it can be read while it runs.
        $process = $null
        $job = [IntPtr]::Zero
        $tied = $false
        $jobProblem = $null
        $ended = $false
        $waited = $false
        $failure = $null
        $stop = $null
        $watch = [Diagnostics.Stopwatch]::StartNew()
        $saved = $env:YAV_HOME
        try {
            # Made before the program is started, so that the program is tied to the script right after its start.
            try { $job = New-LiveProgramJob } catch { $jobProblem = $_.Exception.Message }
            $env:YAV_HOME = $dataDirectory
            $process = Start-Process -FilePath $Yav -WorkingDirectory $project -NoNewWindow -PassThru `
                -ArgumentList @('run', '--project', "`"$project`"", '--prompt-file', "`"$requestFile`"", '--json') `
                -RedirectStandardInput $noInput -RedirectStandardOutput $output -RedirectStandardError $errors
            $null = $process.Handle
            if ($job -ne [IntPtr]::Zero) {
                try { [YavLiveRun.ProgramJob]::Assign($job, $process.Handle); $tied = $true } catch { $jobProblem = $_.Exception.Message }
            }

            Write-LiveRunBegun -Directory $Directory -Begun $begun -Program $Yav -ProcessId $process.Id -Tied $tied -Note $jobProblem
            if (-not $tied) { Write-Host "  yav.exe could not be tied to this script ($jobProblem). If the script is killed, end process $($process.Id) yourself." }
            $ended = Wait-LiveProgram -Process $process -Milliseconds ($Minutes * 60 * 1000)
            $waited = $true
        }
        catch {
            # A stop of the script is no failure of it.
            if ($_.Exception -isnot [System.Management.Automation.PipelineStoppedException]) { $failure = $_.Exception.Message }
            throw
        }
        finally {
            $env:YAV_HOME = $saved

            # Whatever ended the wait, the time given, a stop of the script or a failure, the program does not go on.
            if ($null -ne $process -and -not $process.HasExited) { $stop = Stop-LiveProgram -Process $process -Seconds 30 }

            # Whatever is left in the job ends with it.
            if ($job -ne [IntPtr]::Zero) { [YavLiveRun.ProgramJob]::Close($job) }
            $watch.Stop()

            # Whatever happened, what the run left is summed up, and what yav wrote as errors is shown.
            $read = Read-LiveRunOutput -Path $output
            $summary = [pscustomobject]@{
                endedByItself = $ended
                stoppedBy     = $(if ($ended -or $null -eq $process) { $null }
                    elseif ($waited) { "the script, when the $Minutes minutes that were given were up" }
                    elseif ($null -ne $failure) { "the script, which failed while it waited: $failure" }
                    else { 'the script, which was stopped while it waited' })
                stillRunning  = $null -ne $stop -and -not $stop.Ended
                stop          = $(if ($null -ne $stop) { $stop.Message } else { $null })
                tiedToScript  = $tied
                failure       = $failure
                exitCode      = $(if ($null -ne $process -and $process.HasExited) { $process.ExitCode } else { $null })
                seconds       = [math]::Round($watch.Elapsed.TotalSeconds, 1)
                outcome       = $read.outcome
                runId         = $read.runId
                reason        = $read.reason
                state         = $read.state
                resultWritten = $read.resultWritten
            }
            $summary | ConvertTo-Json | Set-Content -LiteralPath $summaryPath -Encoding utf8
            if ($null -ne $stop) { Write-Host "  $($stop.Message)" }
            $written = $(if (Test-Path -LiteralPath $errors) { [IO.File]::ReadAllText($errors).Trim() } else { '' })
            if ($written.Length -gt 0) {
                Write-Host '  yav wrote this as errors (run-errors.txt):'
                $written -split "`r?`n" | ForEach-Object { Write-Host "    $_" }
            }
        }

        Write-Host "  outcome: $($summary.outcome), exit code $($summary.exitCode), $($summary.seconds) s$(if ($summary.stoppedBy) { " - STOPPED by $($summary.stoppedBy)" })"
        if ($summary.reason) { Write-Host "  reason:  $($summary.reason)" }
        $after = Get-AfterRun -Summary $summary -Part $Part -Apply:$Apply
        $exitCode = $after.ExitCode
        $inspect = $after.Inspect
        if ($after.Reason) {
            Write-Host "  The run did not go through: $($after.Reason). What it left can be looked at:"
            Write-Host "    scripts\live-run.ps1 -Part inspect -Directory `"$Directory`""
            if ($summary.runId -and (-not $summary.endedByItself -or -not $summary.resultWritten)) {
                Write-Host "    scripts\live-run.ps1 -Part resume -IAuthorizeUsage -Directory `"$Directory`"    (continues it and asks the models again)"
            }
        }
        elseif ($summary.outcome -eq 'approval_required') {
            Write-Host '  In a run nobody can answer, yav asks nobody and declines what an agent asks for. To go on, decide what the agent asked for:'
            Write-Host "    scripts\live-run.ps1 -Part resume -IAuthorizeUsage -Directory `"$Directory`"    (declines it and continues)"
            Write-Host "    `$env:YAV_HOME = `"$dataDirectory`"; & `"$Yav`" `"$project`"    then /resume, to answer at the keyboard"
        }

        if ($Part -eq 'all' -and $Apply -and $summary.resultWritten -and $summary.outcome -ne 'ready_to_apply') {
            Write-Host "  The run did not end ready to apply ($($summary.outcome)), so nothing is applied and the part inspect is not started."
            Write-Host "  Once it is ready to apply: scripts\live-run.ps1 -Part inspect -Apply -Directory `"$Directory`""
        }
    }

    if ($Part -eq 'resume') {
        $runId = Get-LiveRunId -Directory $Directory
        if (-not $runId) { throw "The run in $Directory did not get as far as having a number, so there is nothing to continue." }
        $recordName = Get-LiveRecordName -Directory $Directory -Part 'resume'
        Write-Host "Continuing run $runId in the shell (at most $Minutes minutes). What the agents ask for is declined..."
        try {
            # Told that the usage was authorized: this part is reached only with -IAuthorizeUsage, checked at the start.
            Invoke-Console @console -Name 'resume' -Record $recordName -Test 'A_run_that_waits_for_an_answer' `
                -Variables (Get-LivePartVariables @partOf -Name 'resume' -Record $recordName -RunId $runId -UsageAuthorized:$IAuthorizeUsage)
        }
        finally {
            # Kept also when the part failed: whatever happened is in it.
            $screens = Join-Path $Directory "$recordName-screens.txt"
            if (Test-Path -LiteralPath $screens) {
                Get-Content -LiteralPath $screens -Encoding utf8 |
                    Where-Object { $_.StartsWith("Run ${runId}: ") -or $_.StartsWith('Questions of the agents') -or $_.StartsWith('The part failed') } |
                    ForEach-Object { Write-Host "  $_" }
                Write-Host "  kept: $screens"
            }
        }
    }

    if ($inspect) {
        if ((Get-LiveRunState -Directory $Directory) -eq 'begun') { Write-Host "  $(Get-LiveRunBegunText -Directory $Directory) What the shell shows is what is known of it." }
        $runId = Get-LiveRunId -Directory $Directory
        if ($Apply -and -not $runId) { throw "There is no run in $Directory whose change could be applied. Part 'run' makes one." }
        $recordName = Get-LiveRecordName -Directory $Directory -Part 'inspect'
        Write-Host "Looking at what the run left, in the shell (no model is asked)$(if ($Apply) { ', and applying it' })..."
        try {
            Invoke-Console @console -Name 'inspect' -Record $recordName -Test 'What_the_run_left' -Variables (Get-LivePartVariables @partOf -Name 'inspect' -Record $recordName -RunId $runId)
            Write-Host "  done: $(Join-Path $Directory "$recordName-screens.txt")"
        }
        finally {
            $judgement = Join-Path $Directory "$recordName-judgement.txt"
            if (Test-Path -LiteralPath $judgement) { Get-Content -LiteralPath $judgement | Select-Object -First 12 | ForEach-Object { Write-Host "  $_" } }
        }
    }

    Write-Host "Everything is kept in $Directory"
}
finally {
    Restore-HostingSessionVariables $takenOut
}

exit $exitCode
