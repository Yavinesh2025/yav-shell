# YAV Shell - user guide

YAV Shell is a console application for Windows. You type a request; **Model A** implements it in an
isolated copy of your project; **Model B** reviews the result in a conversation of its own while your
project's required checks run; and only a candidate that passed both is offered to you for applying.
Your project is written by `/apply` and by nothing else.

YAV Shell contains no model and no coding agent. It drives the agent programs you have installed
(Codex, Claude Code) and uses the accounts those programs are signed in to.

Contents: [Start](#start) · [First steps](#first-steps) · [What you type](#what-you-type) ·
[Keys](#keys) · [A run](#a-run) · [Commands](#commands) · [Quality Lock](#quality-lock) ·
[Checks of a project](#checks-of-a-project-yavprojectjson) · [Without a prompt](#without-a-prompt-yav-run) ·
[JSON output](#json-output) · [Where things are kept](#where-things-are-kept) · [Limits of what YAV can know](#limits-of-what-yav-can-know)

## Start

```
yav                          the shell, in the current directory
yav "C:\Projects\My App"     the shell, with a project
yav run --project <path> --task "<text>" [--json] [--apply] [--continue <task-id>]
yav run --project <path> --prompt-file task.md [--json]
yav doctor [--json]          what YAV needs, and what it found
yav --version | --help
```

YAV runs inside the console you start it from: Windows Terminal, the console of PowerShell, or the
console of CMD. It opens no window of its own.

Started from a place that is no project (the root of a drive, your profile, the Start menu), the shell
starts without a project and waits for `/open <path>`.

## First steps

1. `yav doctor` shows whether the agents are installed and signed in, and what is missing.
2. In the shell, `/models` lists what the providers offer for your accounts. Choose both roles:
   `/models a codex-app-server <model>` and `/models b claude-cli <model>`.
   YAV does not choose models for you and does not rank them.
3. `/login` shows through which account each agent works and how that is billed, and asks you to
   acknowledge the route once. YAV never sees a password or a token.
4. `/test detect` proposes required checks from what the project contains (or write
   `yav.project.json` yourself). Nothing runs before you approved it.
5. Type what you want done.

## What you type

| You type | It is |
|---|---|
| text without a slash | a request for Model A, or a follow-up to the task |
| `/name ...` | a command of YAV |
| `//text` | a request that begins with a slash |

YAV **never** runs what you type as a command of the operating system, whatever it looks like:
`dir`, `git status` or `del *.*` are requests. The only exceptions are what you give to `/exec` and
what you enter in the shell that `/shell` starts.

A command YAV does not know does nothing. YAV says so, names commands with a similar name, and runs
none of them.

## Keys

| Key | Effect |
|---|---|
| Enter | send the request or the command |
| Shift+Enter, Ctrl+J | start a new line |
| paste | several lines are one input; a line break that was pasted never sends it |
| Tab | complete a command, its words, or a path |
| Up, Down | earlier input, also of earlier sessions. With something typed: earlier input that begins with it |
| Ctrl+Left, Ctrl+Right | move by words |
| Esc | close the list of completions; while a run is active, empty the line |
| Ctrl+C | empty the line; on an empty line: stop the run, or - pressed twice - leave |

Shift+Enter depends on the console reporting the Shift key with Enter. Where it does not, use Ctrl+J.
Both were tested in the pseudo console of Windows (the interface Windows Terminal uses).

While a list of completions is open, Up and Down move in the list. Esc closes it.

Copying and pasting are done by the console, with its own keys and its own menu: YAV does not take
the mouse and does not switch to another screen, so what was written stays in the console's
scrollback and can be selected there.

While a run is active you can go on typing. What the run reports appears above your line and your
line stays as it is. A request you send then waits in the queue; it is not added to the running turn
unless you choose that with `/queue steer <n>` and the agent supports it.

When an agent asks for approval, YAV shows what it asked for and asks, for example:

```
Allow "npm install left-pad"? a, s, d or c, then Enter:
```

You answer with a letter **and Enter**; what you type is shown after the question. `a` allows once,
`s` allows for this conversation (only where it is offered), `d` declines, `c` declines and stops the
turn. Esc declines and Ctrl+C declines and stops the turn at once, without Enter. Anything else,
followed by Enter, grants nothing. A single key never grants anything, so a word you were typing for
something else - "yes", "/stop", "abort" - cannot allow what an agent asked for.

* **Some requests are allowed only with the word `allow` and Enter**, and only once, never for the
  conversation: those the agent marks as not to be allowed in passing, those whose text contains
  characters a terminal does not show, and those that are too long to be shown whole (more than 60
  lines, or a line of more than 2000 characters). The question says so.
* **Characters a terminal does not show are written out** in what is shown, as `\x1B` or `<U+202E>`,
  so that nothing the agent sends can hide a part of a command or turn it around.
* An answer that you began before the question could be read (within 0.6 seconds after it could
  first take keys) grants nothing, and keys you typed before the question was there are thrown away.
  Pasted text never answers.
* Without a console that can show the question - input or output redirected - nobody is asked, and
  the run ends as Approval Required.

## A run

```
YAV C:\Projects\MyApp> Fix the login bug and add regression tests.
[PREPARE] Project rules and workspace verified (isolated worktree, 214 files)
[CODE A]  <model> is implementing the task (effort <value>)
  ~ update src/login.cs
  │ what the model said
[CHECK]   Reviewing and testing candidate 89038be2149b (2 file(s): +1 ~1 -0)
[REVIEW B] <model> is reviewing candidate 89038be2149b in its own conversation, read-only (effort <value>)
[TESTS]   Required check passed
[REVIEW B] No blocking findings reported
[READY]   Changes available for inspection (/diff) and application (/apply)
```

* Lines that start in the first column are YAV's own. Everything an agent, a tool or a check wrote is
  indented behind a mark (`│`, `$`, `~`, `!`), on every row it needs, so it cannot pass for a line of
  YAV or for a question of YAV.
* The lines show events that happened. There is no percentage and no forecast.
* Review and required checks run side by side, both against the same frozen candidate.
* When the review has findings or a check fails, everything goes back to Model A in **one** request.
  That is a repair cycle; two are allowed by default (`/limits repairs <n>`).

A run ends in one of these states:

| State | Meaning | Next |
|---|---|---|
| Ready to Apply | review and required checks passed for exactly this candidate | `/diff`, `/apply` |
| Completed | applied, or answered without changing a file | |
| Blocked / Needs Attention | something needs your decision; the candidate is kept | what YAV names, then `/resume` |
| Awaiting Approval | an agent asked for something and waits for your answer | `a`, `s`, `d`, `c` or `allow`, then Enter |
| Interrupted | you stopped it; workspace and conversations are kept | `/resume` |
| Rate Limited | the provider's limit was reached | `/resume` later |
| Failed | the agent or a tool failed | `/history <run-id>` |
| Needs Reconciliation | YAV or an agent ended unexpectedly | `/resume`, `/doctor` |

Preparing, Implementing, Checking and Repairing are the states of a run that is working.

## Commands

`/help` lists them; `/help <command>` explains one. Commands marked *waits* are refused while a run
is active, because they would change the run or need the console.

### Project

| Command | |
|---|---|
| `/open <path> [--accept-gaps]` | select a project. *waits* |
| `/cd <path>` | change to a directory of the project, or to another project. *waits* |

`--accept-gaps` records that you accept that files ignored by Git are absent from the isolated
workspace. Without it, a run in such a project does not start until you accepted that or listed what to
copy under `replicateIgnored`.

### Models and policy

| Command | |
|---|---|
| `/models` | what each agent lists for your account; `A` and `B` mark your choice |
| `/models a\|b <adapter> <model>` | choose a role. A model that is not listed is not chosen, and no other is taken instead |
| `/models swap`, `/models refresh` | exchange the roles; ask the providers again |
| `/effort` | what is requested for each role and what "maximum" resolves to |
| `/effort a\|b <value>\|maximum` | set it. A value the model does not list is refused; nothing is lowered for you |
| `/login [codex\|claude]` | account route and billing; acknowledges the route; starts the provider's own sign-in when you are not signed in |
| `/login claude --api-key`, `--forget-key` | keep an Anthropic API key in the Windows Credential Manager, or remove it |
| `/quality` | Quality Lock and what a candidate has to pass |
| `/quality lock\|strict on\|off` | |
| `/quality gates required\|optional` | whether a project without approved checks may run |
| `/quality preexisting ask\|repair` | what happens to a required check that already failed before the task |
| `/speed [standard\|provider]` | the provider's faster serving of the same model. Shown with its billing and used only after you typed `yes` |
| `/adaptive [on\|off]` | off by default. When on, YAV asks at the start of each new task whether Model A may work at a lower effort for that task |
| `/optimization [on\|off]` | YAV's own additions to a request (starting references, reuse of valid evidence) |

All of these *wait*. A change applies to the next run; a run that is active keeps what it started with.

### Task

| Command | |
|---|---|
| `/new` | the next request starts an unrelated task with its own workspace and conversations. *waits* |
| `/resume [run-id]` | continue a run that stopped before it was ready. *waits* |
| `/attach [<path> \| clear]` | name a file to Model A with the next request. Files that look like they hold credentials are refused |
| `/replace <file> <text> <replacement> [--count <n> \| --all]` | replace a text in one file without a model; see below. *waits* |
| `/status` | the state of the run and, per role, what was requested and what the provider confirmed |
| `/stop` | interrupt the active run |
| `/queue [add <text> \| remove <n> \| clear \| steer <n>]` | the requests that wait |

In `/status`, a setting is **Verified** only when the provider reported it. Otherwise it is
**Requested / Unverified**, **MISMATCH**, **Unsupported** or **Unavailable**.

#### A change you spell out: `/replace`

```
/replace src/version.txt 1.2.3 1.2.4
/replace "docs/my notes.txt" "say ""hi""" "say ""good morning"""
/replace src/app.config timeout deadline --count 2
```

YAV makes the replacement itself, in the isolated workspace. Model A is not called and nothing is
sent for the change. Everything after that is as for any change: Model B reviews the result, the
required checks run, `/diff` shows what would be written, and `/apply` writes it.

* The text has to occur **exactly once**. `--count <n>` says that it occurs n times and that all of
  them are meant; `--all` takes every occurrence. When the file holds the text more or less often than
  you said, nothing is changed and the run says where the text is.
* Upper and lower case count. The file is named from the directory of the project and has to be inside it.
* Text with blanks is written in double quotes; a double quote inside them is written twice.
* A text of several lines, a pattern, or a change that needs thought is a request for Model A.

This command is not in the list the specification of YAV gives; the specification asks for such
changes to be made locally and names no command for it.

### Inspect and check

| Command | |
|---|---|
| `/diff [--stat \| --full \| --export <file>]` | what the task changed, compared with the state it started from |
| `/test` | run the required checks again for the candidate of the last run; without one, in the project itself |
| `/test <check>` | run one check in the project itself. That is not evidence for a candidate |
| `/test list \| detect \| trust` | show, propose, approve |
| `/test waive <check> <reason>` | accept the current candidate although this check did not pass. For this candidate only |
| `/review` | run the review of Model B again. *waits* |
| `/review show` | the review of the current candidate as the reviewer wrote it: summary, what was inspected, limitations and findings, in full |
| `/review approve <path>` | approve a change to a protected path, for the current candidate |

### Deliver

| Command | |
|---|---|
| `/apply` | write the candidate into the project. The evidence is evaluated again first |
| `/apply --merge` | combine edits you made meanwhile with the candidate, in isolation, and check the result again |
| `/discard [run-id]` | remove the isolated changes of a task. The project is not touched |
| `/undo [--skip-edited]` | reverse the most recent apply, file by file |

All of these *wait*. `/apply` never overwrites a file you edited since the task started, and `/undo`
never overwrites a file you edited since the apply. Neither uses `git reset` or `git clean`.

### Records

| Command | |
|---|---|
| `/history [<run-id>]` | the runs of the project; what happened in one |
| `/history export <run-id> <file>`, `delete <run-id>`, `prune` | |
| `/usage [run-id \| all]` | tokens per role, the billing route, and where each number comes from |
| `/latency [run-id]` | measured times |
| `/limits [minutes\|repairs\|tokens\|ratelimit\|queue <n>]` | |

What a provider did not report is shown as **Unavailable**, never as 0. Usage of a subscription counts
against its limits; YAV does not turn it into an amount of money. A limit keeps YAV from starting
another turn; it does not cut off a turn that is running and it is not a cap on what a provider charges.

`/latency` shows how long the console took to start and to answer commands, and for a run every
stage from the first look at the project to the apply. Stages that ran side by side are not added up.
The time a candidate waited for your decision is "waiting for you" and not part of the active time,
and what no stage accounts for is shown as such, so that the stages never pass for the whole.
The time the agents spent in tools they ran, such as commands, is measured from what they report
and shown apart. What is left of a turn is provider time: waiting, reasoning and answering, which
no provider tells apart. What is kept with a measurement never includes a request or a command.

### Local, without a model

| Command | |
|---|---|
| `/exec <command>` | run a command in your shell. *waits* |
| `/shell [pwsh\|powershell\|cmd]` | hand the console to a real shell until you leave it with `exit`. *waits* |

Both run **as you, with your rights, outside every agent sandbox and outside the isolated workspace**,
in the directory of the project. No model sees what you run or what it prints.

### Application

| Command | |
|---|---|
| `/settings [<name> <value> \| path]` | `shell`, `plain`, `verbose`, `telemetry`, `keep-runs`, `keep-workspaces`, `trust`, `workspace`, `adapter` |
| `/doctor` | the same report as `yav doctor` |
| `/help [command]`, `/?` | |
| `/exit`, `/quit`, `/q` | save the state and leave. A run that is active is stopped after you confirmed |

## Quality Lock

Quality Lock is on unless you turn it off. With it, YAV never by itself

* uses another model than the one you chose, or a lower effort,
* removes tools, switches the billing route, or skips the review.

Where a provider would not honor what you chose, the run stops and says so. With **strict** policy
(the default), a setting the provider did not confirm blocks the run; with relaxed policy it is shown
as Requested / Unverified and the run goes on.

Quality Lock holds the configuration and the acceptance requirements. It cannot make a model's answer
correct, and it cannot make two runs give the same result.

**Adaptive mode** is off unless you turn it on (`/adaptive on`). With it, YAV asks at the start of
each new task whether Model A may work at a lower effort for that task, and uses a lower effort only
when you named one. The model stays the same, Model B reviews at its own effort, and the run is
marked Adaptive, not Strict Max. A task that is long, or that names something where a mistake is
expensive - credentials, permissions, payments, migrations, deleting, deploying, a protected path -
is not offered a lower effort at all. A lower effort changes how the model reasons; the review and
the checks that follow do not make the result the same as it would have been.

"Maximum" effort resolves to the highest value the provider lists for the model. When a model lists a
value YAV cannot rank - Codex lists `ultra`, which adds task delegation and is not simply "more than
`max`" - YAV does not choose. The run does not start until you set the exact value with `/effort`.

## Checks of a project: `yav.project.json`

The required checks of a project are commands **you approved**. A `yav.project.json` in the root of a
project is a proposal until then: `/test trust` shows it and asks. When the file changes later, the
version you approved stays in effect until you approve the new one.

```jsonc
{
  "schemaVersion": 1,
  "gates": [
    { "id": "build", "kind": "build", "title": "Build", "command": "dotnet", "args": ["build", "--nologo"], "timeoutSeconds": 1800 },
    { "id": "test",  "kind": "test",  "title": "Tests", "command": "dotnet", "args": ["test", "--nologo"],
      "requires": ["tool:dotnet"] }
  ],
  "prepare": [
    { "id": "restore", "command": "dotnet", "args": ["restore"] }
  ],
  "protectedPaths": ["deploy/**", ".github/**"],
  "replicateIgnored": ["appsettings.Development.json"],
  "allowSecrets": [],
  "testPaths": ["tests/**"],
  "validation": { "execution": "copy" }
}
```

| Setting | |
|---|---|
| `gates[].id` | letters, digits, `-`, `_`; at most 40 characters |
| `gates[].kind` | `build`, `lint`, `typecheck`, `test`, `smoke`, `integration`, `custom` |
| `gates[].command`, `args` | the program and its arguments **as a list**. They are never joined into a command line and never given to a shell |
| `gates[].cwd` | a directory inside the project, relative to its root |
| `gates[].timeoutSeconds` | 1 to 86400; default 1800 |
| `gates[].required` | default `true` |
| `gates[].env` | variables for this command |
| `gates[].successExitCodes` | default `[0]` |
| `gates[].requires` | `env:NAME`, `tool:name`, `file:path`, `tcp:host:port`, `manual:what`. When one is not met the check is **Unverified**, which is not a pass |
| `prepare` | commands that run once in a new workspace, before Model A |
| `protectedPaths` | a change to one of these needs `/review approve <path>` |
| `replicateIgnored` | files ignored by Git that are copied into the workspace |
| `allowSecrets` | files that look like secrets and are copied nonetheless |
| `testPaths` | where existing tests are; changing or deleting one is pointed out to you and to the reviewer |
| `validation.execution` | `copy`: checks run in a disposable copy of the candidate. `candidate`: they run in the candidate's workspace, which is verified afterwards |

Anything unknown in the file is an error, because the file decides which commands run.

## Without a prompt: `yav run`

`yav run` performs one request and ends. Nobody can be asked, so nothing is granted: when an agent
asks for approval, the run ends as **Approval Required**.

| Exit code | |
|---|---|
| 0 | ready to apply, applied, or answered without changes |
| 1 | `yav doctor` found a problem |
| 2 | blocked: needs a decision of yours |
| 3 | approval required |
| 4 | rate limited |
| 5 | failed |
| 6 | interrupted |
| 7 | needs reconciliation |
| 64 | the command line was not understood |

Without `--apply` the project is not changed; the candidate waits in its workspace (`yav <path>`, then
`/diff` and `/apply`). With `--apply` a candidate that passed is written into the project.

The shell reads from a pipe or a file as well: `yav <path> < commands.txt`. Lines are then read one
after the other, each after the run before it has ended, and the end of the input lets a run finish.
Questions are answered with "no", because nobody is there to answer them, and a run in which an
agent asks for approval ends as Approval Required. The same holds when only the output is redirected
(`yav > out.txt`): a question that nobody can see is not asked.

## JSON output

With `--json`, standard output carries one JSON object per line and nothing else. Text that came from
agents and tools is cleaned of control sequences, with one exception: **what an agent asks for and
the commands it runs are passed on exactly as the agent gave them** (in `approval.requested`,
`pendingApprovals` and `agent.command.*`). There, every character a terminal would act on is written
as a JSON escape (`\u001b`), so the parsed value is exact and the printed line hides nothing.
Numbers a provider did not report are `null`.

Values YAV defines are written in lower case with underscores (`ready_to_apply`, `workspace_write`).
Values a provider defines - in `setting` and `agent.*` events - are written the way the provider
writes them (`workspace-write`).

Every line but the last is an event. It begins with `type`, `runId`, `at` and `event`:

```json
{"type":"event","runId":"20260929-084004-cqdm8","at":"2026-09-29T08:40:04.1230000+00:00","event":"state","from":"implementing","to":"checking","reason":null}
```

| `event` | Further fields |
|---|---|
| `run.started` | `taskId`, `request`, `profileHash`, `policy`, `qualityLock`, `followUp`, `modelA`, `modelB` |
| `state` | `from`, `to`, `reason` |
| `note` | `stage`, `level`, `message` |
| `setting` | `role`, `setting`, `requested`, `effective`, `status`, `source` |
| `candidate` | `candidateId`, `sequence`, `fingerprint`, `baselineFingerprint`, `acceptanceVersion`, `files`, `protectedPaths`, `existingTestsChanged` |
| `review` | `verdict`, `valid`, `sourceUnchanged`, `candidateFingerprint`, `model`, `summary`, `coverage`, `limitations`, `validationErrors`, `findings` |
| `gate.started`, `gate.output`, `gate.result` | |
| `acceptance` | `candidateFingerprint`, `accepted`, `issues` |
| `repair` | `cycle`, `maxCycles`, `findings`, `failedChecks` |
| `run.finished` | `state`, `disposition`, `reason` |
| `agent.*` | what an agent reported; `role` says which: `agent.configured`, `agent.message`, `agent.delta`, `agent.command.started`, `agent.command.output`, `agent.command.completed`, `agent.files`, `agent.tool`, `agent.usage`, `agent.rate_limits`, `agent.retry`, `agent.rerouted`, `agent.notice`, `agent.error`, `agent.turn.started`, `agent.turn.completed`, `agent.ended`, `agent.reasoning_summary` |
| `approval.requested`, `approval.withdrawn` | |

The last line is the result:

```json
{"type":"result","outcome":"ready_to_apply","exitCode":0,"runId":"...","taskId":"...","state":"ready_to_apply","reason":null,
 "finalMessage":null,"candidate":{...},"acceptance":{"accepted":true,"issues":[]},"pendingApprovals":[],"problems":[],"apply":null}
```

`outcome` is one of `ready_to_apply`, `completed`, `blocked`, `approval_required`, `rate_limited`,
`interrupted`, `needs_reconciliation`, `failed`, `invalid`. `examples\run-output.jsonl` is the output
of a complete run, made with a scripted stand-in for the agents; `examples\README.md` says how.

A request that is read from a file (`--prompt-file`) is taken as it is, without the byte order mark
and without the line break the file ends with. It has to be UTF-8 and at most 1 MB.

## Where things are kept

Everything YAV keeps is in one directory of your user account, outside every project:
`%LOCALAPPDATA%\YavShell`, or the directory named by the environment variable `YAV_HOME`.

| | |
|---|---|
| `settings.json` | your settings. Never a credential |
| `yav.db` | runs, evidence, usage, what you approved and acknowledged |
| `workspaces\` | the isolated workspaces |
| `blobs\` | the state of files before and after, for `/apply` and `/undo` |
| `logs\` | complete output of checks, diagnostics of the agents |
| `history.txt` | what you entered |

An API key, when you store one, is in the Windows Credential Manager. Any program that runs as you can
read it there; that is a boundary of the operating system, not of YAV.

YAV sends nothing anywhere. It has no telemetry; the setting exists and is off, and this version
contains no code that transmits data. Your requests, and what the agents read, go to the providers of
the agents you chose, through those agents.

## Limits of what YAV can know

* Highest effort takes time. YAV cannot make inference fast, and it cannot make a result free of defects.
* A review is a second opinion by another model. A pass means that model reported no blocking finding.
* A check that passed says what that check tests, and nothing more.
* YAV shows the restrictions an agent reports. It does not add a sandbox of its own to the agent's
  tools. `docs\security-boundaries.md` says what is enforced by whom.
* Provider time is shown as one interval where the provider does not tell waiting, reasoning and
  answering apart.
