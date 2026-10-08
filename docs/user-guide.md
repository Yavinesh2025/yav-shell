# YAV Shell - user guide

YAV Shell is a console application for Windows. You type a request; **Model A** implements it in an
isolated copy of your project; **Model B** reviews the result in a conversation of its own while your
project's required checks run; and only a candidate that passed both is offered to you for applying.
Your project is written by `/apply` and by nothing else.

YAV Shell contains no model and no coding agent. It drives the agent programs you have installed
(Codex, Claude Code) and uses the accounts those programs are signed in to.

In short: run `yav.exe` once to install it, type `yav` in a console in the folder of a project, and
type what you want done.

Contents: [Install](#install) · [Start](#start) · [The first request](#the-first-request) · [What you type](#what-you-type) ·
[Keys](#keys) · [A run](#a-run) · [Commands](#commands) · [Quality Lock](#quality-lock) ·
[Checks of a project](#checks-of-a-project-yavprojectjson) · [Without a prompt](#without-a-prompt-yav-run) ·
[JSON output](#json-output) · [Where things are kept](#where-things-are-kept) · [Limits of what YAV can know](#limits-of-what-yav-can-know)

## Install

YAV Shell is one file, `yav.exe`. It brings the .NET runtime along; besides it you need only the agent
programs it drives (Codex, Claude Code, see [Installing the agents](#installing-the-agents)). Git is
recommended: without it, a project is worked on in a protected copy, its ignore files are not honored
and edits you make meanwhile cannot be merged.

**Run `yav.exe`**: double-click it, or start it without arguments in a console. When YAV Shell is not
installed for your user account, it says what installing changes and asks:

```
Install now? Y (or Enter) installs, N starts YAV without installing it
```

Installing

* copies `yav.exe` to `%LOCALAPPDATA%\Programs\YavShell`, together with the license, the notices of
  the components that are part of it, this documentation and the examples, which `yav.exe` carries in itself,
* adds that directory to the PATH of your user account, so that `yav` starts YAV Shell in every console
  that is started from then on,
* adds YAV Shell to "Installed apps" of Windows, where it can be removed again.

Nothing else is changed. No administrator rights are asked for, nothing is written outside your user
account, and your data (`%LOCALAPPDATA%\YavShell`, see [Where things are kept](#where-things-are-kept))
is left as it is: installing, replacing and removing leave its settings, history, workspaces and
database alone, unless you ask `yav uninstall --remove-data` to remove them (below). (`yav` started
without arguments opens that directory before it asks, and creates it on a first start, as every start
does; a "no" in a console that was already open is kept there.) There is no entry in the Start menu:
YAV runs in a console and is started with `yav`.

`yav.exe` asks this only where somebody can answer: not when its input or output is redirected, and
not in a console window that was started hidden. There the shell starts without asking, and
`yav install` and `yav uninstall` do not wait for Enter when they are done. Nor does it ask where it
is installed already: a copy installed with `--no-register`, or any `yav.exe` that runs from a
directory holding a `yav-install.json` of YAV Shell, is not offered installation.

Double-clicked, `yav.exe` runs in the console window Windows opens for it; after the installation the
window waits for Enter and closes. Started in a console you had open already, YAV installs and then
starts the shell in that console.

A console keeps the PATH it was started with. A console that is started after the installation finds
`yav`, for example a new PowerShell window from the Start menu. One that was open already does not;
in it, YAV is found after this line, which the installation prints with the directory it used:

```powershell
$env:Path += ';C:\Users\<you>\AppData\Local\Programs\YavShell'
```

A new tab or pane that you open in Windows Terminal 1.18 or later finds `yav` as well, also when Windows
Terminal was running already: it reads the environment afresh for each one (its setting
`compatibility.reloadEnvironmentVariables`, on unless you turned it off). A tab that was open already
does not, and neither does `wt` started from such a tab.

When you answer N in a console that was open already, YAV remembers it and no longer asks when it is
started in a console; `yav install` installs it whenever you want. When that answer cannot be saved,
YAV says so, and it asks again at the next start. A double-click asks every time YAV Shell is not
installed.

In a console that was open already, the shell always starts after the offer. In a window Windows
opened for `yav.exe`, the window closes after the installation, once you press Enter: with exit code 0
when it worked, and with 5 when it was refused or failed, the reason on the error output. Only when the
offer itself goes wrong - while it finds out what is installed, asks or saves your answer, or with an
error YAV did not expect - does YAV say which step it was in and what went wrong, and start the shell.

From a console:

```
yav install [--dir <path>] [--no-path] [--no-register]
yav uninstall [--dir <path>] [--remove-data]
```

| | |
|---|---|
| `--dir <path>` | the installation directory. Default for `install`: where "Installed apps" says YAV Shell is installed, otherwise `%LOCALAPPDATA%\Programs\YavShell`. Default for `uninstall`: that installation, otherwise the folder of the `yav.exe` that runs, when it holds `yav-install.json`. Refused by `install`, before anything is written: the root of a drive; a directory in which the path of `yav.exe` would have 260 characters or more; a directory whose name holds `;`, unless you add `--no-path`; a directory that is not empty and holds no readable `yav-install.json` (or the `package-manifest.json` of 0.1.1); your data directory, a directory that holds it or lies inside it; and, while YAV Shell is installed in another directory, a different `--dir` unless you add `--no-register` (install there again, or remove that installation first). `--dir` followed by something that begins with `-` is refused as a `--dir` without a value (exit code 64); a directory whose name begins with a dash is named with `--dir=<path>` |
| `--no-path` | leaves the PATH of your account as it is |
| `--no-register` | does not add YAV Shell to "Installed apps" |
| `--remove-data` | removes your data directory as well, and the API key YAV stored for it in the Windows Credential Manager. You are asked to type `yes`; where nobody can be asked, nothing is removed, the program neither. When YAV Shell is not installed, nothing is asked and nothing is removed; YAV says so, and that the data folder can be deleted by hand |

`yav uninstall`, and "Uninstall" under "Installed apps", remove what the installation put there - it
is listed in `yav-install.json` in the installation directory - together with the PATH entry and the
entry under "Installed apps". Files you put into that directory yourself stay, and so does the
directory then. Your data stays, and YAV says where it was kept: to remove it as well, delete that
folder (and the API key YAV stored, whose name in the Windows Credential Manager begins with
`YavShell/`).

`--remove-data` removes the data directory only when it is YAV's and nothing else's: it holds
`yav.db`, and at its top level nothing but what YAV creates there - the files `yav.db`, `yav.db-wal`,
`yav.db-shm`, `yav.db-journal`, `settings.json`, `settings.json.tmp-*`, `settings.unreadable-*.json`
and `history.txt*`, and the folders `logs`, `workspaces`, `blobs`, `exports` and `schemas` (hidden
files count as well); it is not the root of a drive or a folder of Windows or of your account, such
as your profile or `%LOCALAPPDATA%`; and no other YAV has its database open. This is checked before
you are asked, and when it does not hold, nothing is removed, the program neither, and YAV says why.
It is checked again just before the directory is deleted; `settings.json` and `yav.db` go last, so a
removal that stops part-way leaves a directory that `--remove-data` still takes. The API key is
removed on its own and said on its own; when the data directory does not exist any more but a key is
stored for it, you are asked about the key alone. Any answer but `yes` keeps your data and the key,
and the program is removed all the same.

Started from the installed program - as "Installed apps" does - the removal cannot delete the program
that runs: it removes everything else first, and the program and its `yav-install.json` stay until it
has ended. Then they are deleted, and the directory with them when nothing else is in it. A newer
installation made into the same directory in the meantime is not deleted. The deletion is handed to
a hidden command as the program ends, also when you close its window with the X button. When the path
of the program holds `%`, `!` or `"`, YAV cannot hand it over safely and says so: delete the folder
yourself once YAV Shell has ended (or, when files of yours stay in it, `yav.exe` and `yav-install.json`).

One thing is elsewhere: the first start of each version of `yav.exe` unpacks the native SQLite library
into `%TEMP%\.net\yav\<id>\` (about 2 MB; .NET does this for a program that is one file). Nothing of
yours is in it. `yav uninstall` does not remove it; it can be deleted while no `yav.exe` runs.

**Updating** is installing again: run the newer `yav.exe`. Double-clicked, it offers to replace an
older version that is installed; in a console, `yav install` replaces it. `yav install` started from the
installed program itself repairs the installation: the program stays as it is, and the files that
belong with it, the PATH entry and the entry under "Installed apps" are put right. While YAV Shell runs
from the installation directory in another console, it is neither replaced nor removed; leave it with
`/exit` first.

An installation of 0.1.1, which its `install.ps1` made and listed in `package-manifest.json`, is
recognised: `yav install` updates it in its directory, and removes what 0.1.1 had and 0.2.0 does not
(`uninstall.ps1` and `package-manifest.json` among it), and `yav uninstall` removes it. A Start menu
entry that 0.1.1 made with `-Shortcut` (`YAV Shell` in the Start menu of your account) is not touched;
delete it yourself.

After the files are written, YAV starts the installed `yav.exe` once with `--version`. When it does
not answer with its version, the PATH and "Installed apps" are left as they are, the message says why
(it could not be started, gave no answer within 60 seconds, or ended with an exit code, with the first
line of what it wrote to its error output), and `yav uninstall --dir "<dir>"` removes the files. Ctrl+C
stops an installation between two files, also one started from the offer at the start; YAV then says
that `yav-install.json` names what may have been written, and that `yav install --dir "<dir>"`
completes the installation and `yav uninstall --dir "<dir>"` removes it. A file whose writing was
interrupted is left as `<name>.partial-<8 hex digits>`; the next `yav install` or `yav uninstall`
deletes it.

`yav install` and `yav uninstall` end with exit code 0 when they did what was asked, with 5 when they
refused or failed (the reason is written to the error output), and with 64 for a command line they do
not understand.

`yav.exe` is not code-signed. After a download, Windows SmartScreen may stop it with "Windows protected
your PC"; "More info" and then "Run anyway" start it. Compare the SHA-256 of the file with the one in
`yav.exe.sha256` that comes with it before you do: in PowerShell, `Get-FileHash .\yav.exe`.

On a PC where Smart App Control is on (Windows Security, App & browser control), Windows blocks programs
that are not signed, however they are started, and offers no "Run anyway": `yav.exe` cannot be used
there. Microsoft describes this in
[Smart App Control](https://learn.microsoft.com/en-us/windows/apps/develop/smart-app-control/overview).

A `yav.exe` that was built from the source tree (`scripts\build.ps1`) needs the files next to it and
cannot install itself; `scripts\package.ps1` builds the one-file `dist\yav.exe`. Such a build removes
only an installation you name with `--dir`; `yav uninstall` without it removes nothing there and ends
with exit code 5.

## Start

```
yav                          the shell, in the current directory
yav "C:\Projects\My App"     the shell, with a project
yav run --project <path> --task "<text>" [--json] [--apply] [--continue <task-id>]
yav run --project <path> --prompt-file task.md [--json]
yav doctor [--json]          what YAV needs, and what it found
yav install [--dir <path>] [--no-path] [--no-register]
                             install this yav.exe for your user account
yav uninstall [--dir <path>] [--remove-data]
                             remove the installation; your data stays
yav --version | --help
```

YAV runs inside the console you start it from: Windows Terminal, the console of PowerShell, or the
console of CMD. It opens no window of its own; double-clicked, it runs in the console window Windows
opens for it. `yav` without arguments, started from a copy that is not installed, first offers to
install it ([Install](#install)).

Started from a place that is no project (the root of a drive, your profile), the shell starts without
a project. The first request then asks for the folder of the project, or you select one with `/open <path>`.
As the answer to that question, a drive, your profile, the Desktop, Documents, Downloads or a folder of
Windows is not taken, and the question is asked again; `/open <path>` selects such a folder deliberately.
The request is then a task of its own, and a file you attached before goes with it.

## The first request

Start `yav` in the folder of a project and type what you want done:

```
YAV C:\Projects\MyApp> Fix the login bug and add regression tests.
```

That is all it takes. What a first run needs and only you can decide, the shell asks right there, one
question at a time, and then it sends the request you typed. Your answers are kept, so the next request
just runs. The first attempt shows what kept it from starting (the `[BLOCKED]` lines); the questions
follow it.

What the shell can ask, and only when it is needed:

| When | It asks | Later, the same with |
|---|---|---|
| no project is selected | the folder of the project | `/open <path>` |
| Model A or Model B is not chosen | a number from the list of the models the agents list for your accounts: model, name, agent, effort values, and which one the provider marks as its default | `/models a\|b <adapter> <model>` |
| a model lists an effort value YAV cannot rank (Codex lists `ultra`) | the exact effort, one of the values the model lists | `/effort a\|b <value>` |
| an agent is not signed in | for Codex, whether to start Codex's own sign-in. For Claude Code it asks nothing and says how to sign in with Claude Code itself (`claude`, or `claude auth login`) | `/login codex\|claude` |
| an account route that bills an API key per token is not acknowledged | whether runs of YAV may use it, after it showed how the route is billed. A subscription the agent is signed in to (the ChatGPT plan of Codex, a Claude subscription) is used without this question | `/login` |
| files ignored by Git would be absent from the isolated workspace | whether you accept that, after it showed which ones | `/open --accept-gaps` |

* YAV does not choose models for you and does not rank them. The list is what the providers report;
  the default is marked because the provider says so. For each provider the list names one interface:
  for Codex the app server when it can be used, otherwise `codex exec`. `/models` lists all of them.
* Model B reviews what Model A made, so it has to be another model: with Quality Lock and strict
  policy (the defaults) the same model for both roles does not run, and the list does not take it.
* **Checks are not asked for.** A request in a project without an approved required check runs right
  away, on Model B's review alone, and the run says so. Where YAV finds checks it could propose, it
  names them in one line, with how to approve them (`/test detect`); it never runs a command of the
  project that you have not approved. `/quality gates required` makes checks required again
  ([Quality Lock](#quality-lock)).
* What grants or acknowledges something - an account route that bills an API key, checks, ignored
  files that stay behind - is answered with the word `yes` and Enter, never with a single key.
* A question you decline, or leave without an answer, changes nothing, and the request is not sent.
  Enter on an empty answer, or Ctrl+C, chooses nothing; Esc only clears what you typed. Three answers
  that are not on the list choose nothing either. Send the request again to be asked again, or decide
  with the command in the table. A file you attached to it stays attached to the next request.
* YAV never sees a password or a token. The sign-in is the provider's own program.
* What no question can settle - an agent that is not installed, an account route its provider does not
  permit, a repository YAV cannot isolate - the run names, together with what to do, as it does for
  every run. When no agent is installed at all, it says how to install one (below).
* Nothing is asked in `yav run`, or when input or output is redirected: such a run ends as Blocked and
  names what is missing ([Without a prompt](#without-a-prompt-yav-run)).

Every answer can be changed later with the commands: `/models`, `/effort`, `/login`, `/test`,
`/quality`, `/open --accept-gaps`. `yav doctor` shows what YAV needs and what it found, without a run.

### Installing the agents

YAV installs no agent and runs no installer. The vendors' own commands for Windows, run in PowerShell,
need no administrator rights:

| Agent | Install | Sign in |
|---|---|---|
| Codex CLI (OpenAI) | `powershell -ExecutionPolicy ByPass -c "irm https://chatgpt.com/codex/install.ps1 \| iex"` | `codex login` |
| Claude Code (Anthropic) | `irm https://claude.ai/install.ps1 \| iex` | start `claude` and sign in in the browser, or `claude auth login` |

Other ways the vendors name: `npm install -g @openai/codex`; `winget install Anthropic.ClaudeCode`, which
does not update itself (`winget upgrade Anthropic.ClaudeCode`), and `npm install -g @anthropic-ai/claude-code`.
Both installers change the PATH of your account, so start a new console afterwards, and then `yav` again.
The vendors' documentation: [Codex CLI](https://learn.chatgpt.com/docs/codex/cli) and
[Claude Code](https://code.claude.com/docs/en/setup).

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
* While an agent thinks or works, the last line of the run moves: it shows the stage the run is in,
  what the agent reports it is doing, and the time since the stage began, so a still screen means
  that nothing runs. It is drawn only in a console; `yav run`, JSON output and redirected output get
  no such line.
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
workspace. Without it, a run in such a project does not start until you accepted that - a request
typed in the shell asks for it - or listed what to copy under `replicateIgnored`. The acceptance holds
for exactly the files it named: when other ignored files appear later, you are asked again.

### Models and policy

| Command | |
|---|---|
| `/models` | what each agent lists for your account; `A` and `B` mark your choice |
| `/models a\|b <adapter> <model>` | choose a role. A model that is not listed is not chosen, and no other is taken instead |
| `/models swap`, `/models refresh` | exchange the roles; ask the providers again |
| `/effort` | what is requested for each role and what "maximum" resolves to |
| `/effort a\|b <value>\|maximum` | set it. A value the model does not list is refused; nothing is lowered for you |
| `/login [codex\|claude]` | account route and billing; acknowledges a route that bills an API key (a subscription needs no acknowledgement); starts the provider's own sign-in when you are not signed in |
| `/login claude --api-key`, `--forget-key` | keep an Anthropic API key in the Windows Credential Manager, or remove it |
| `/quality` | Quality Lock and what a candidate has to pass, and whether review-only acceptance was accepted for the selected project |
| `/quality lock\|strict on\|off` | |
| `/quality gates required\|optional` | whether a project without an approved required check may run on the review alone, for every project. `required` also makes checks required for the selected project, which by default runs on the review alone |
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
and shown apart: from when an agent reported the start of a tool's run to when it reported its end,
at the times YAV received those reports, not when it handled them. What is left of a turn is
provider time: waiting, reasoning and answering, which no provider tells apart. What is kept with a
measurement never includes a request or a command.

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

**Required checks** are required unless you change that. A candidate is ready only when the review
passed and every required check of the project passed for exactly that candidate. (A check is required
unless it says `"required": false`.) A project that has no approved required check is not held up:

* **Review-only by default.** A request in a project without an approved required check runs right
  away, without a question, and its candidate is accepted on Model B's review alone; every run says so.
  Where YAV finds checks it could propose, it names them in one line, with how to approve them
  (`/test detect`); it runs no command of the project that you have not approved. This applies only while the project has
  no approved required check: once you approve one (`/test detect`, `/test trust`), checks are required
  for it again, and should the project later require none, the acceptance applies again. An approved
  check that is optional does not set it aside, because a run does not run optional checks.
  `/quality` shows it, and `/quality gates required` makes checks required for the selected project:
  its requests then do not run until a required check is approved.
  * When the approved checks of the project cannot be read (the stored approval is damaged, or the
    project's `yav.project.json` cannot be used), the acceptance does not stand in for them: the run
    is blocked (`review-only-unknown`) until you correct the file or approve the checks again.
  * When you withdraw the acceptance after a run, that run's candidate, which was accepted on the review
    alone, is not applied: `/apply` writes nothing and says so. Run the task again to have it checked.
* **`/quality gates optional`**, for every project: a candidate of a project without an approved required
  check is accepted on the review alone, everywhere, until you set `/quality gates required` again.

A review is a second opinion of another model; it does not run the code. Review-only acceptance is
what it says, and no replacement for checks that run.

**Adaptive mode** is off unless you turn it on (`/adaptive on`). With it, YAV asks at the start of
each new task whether Model A may work at a lower effort for that task, and uses a lower effort only
when you named one. The model stays the same, Model B reviews at its own effort, and the run is
marked Adaptive, not Strict Max. A task that is long, or that names something where a mistake is
expensive - credentials, permissions, payments, migrations, deleting, deploying, a protected path -
is not offered a lower effort at all. A lower effort changes how the model reasons; the review and
the checks that follow do not make the result the same as it would have been.

"Maximum" effort resolves to the highest value the provider lists for the model. When a model lists a
value YAV cannot rank - Codex lists `ultra`, which adds task delegation and is not simply "more than
`max`" - YAV does not choose. The run does not start until you named the exact value: a request typed
in the shell asks for it, or you set it with `/effort`.

## Checks of a project: `yav.project.json`

The required checks of a project are commands **you approved**. A `yav.project.json` in the root of a
project is a proposal until then: `/test trust` shows it and asks. When the file changes later, the
version you approved stays in effect until you approve the new one.

A request typed in a project without an approved required check does not ask for one: it runs on
Model B's review alone ([Quality Lock](#quality-lock)). When the project has a `yav.project.json`, or
`/test detect` finds checks in what the project contains, the run says so in one line, with how to
approve them: `/test trust` or `/test detect`, which show the commands, and nothing runs before you
typed `yes`. Approved proposals are written to `yav.project.json` when the project has none, so that
they can be kept with the project. While the approved checks cannot be read, the run is blocked
instead (`review-only-unknown`).

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
asks for approval, the run ends as **Approval Required**. The questions of a first request are not
asked either: a run that needs a decision of yours - the models, an account route that bills an API key, the
exact effort, ignored files - ends as **Blocked** (exit code 2), and its result names what is missing
(`problems`). Decide it in the shell, where a typed request asks for it, or with the commands. A
subscription route and a project without an approved required check need no decision there either.

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
agent asks for approval ends as Approval Required. The questions of a first request are not asked: a
request that needs a decision of yours ends as Blocked. The same holds when only the output is
redirected (`yav > out.txt`): a question that nobody can see is not asked.

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

The program is kept apart from your data: in the installation directory (`%LOCALAPPDATA%\Programs\YavShell`
unless you named another one), with `yav-install.json`, the list of what the installation put there.
Nothing about your work is written into it, and removing YAV Shell leaves your data where it is unless
you ask for it to be removed (`yav uninstall --remove-data`, which removes the data directory only when
it holds nothing YAV did not create; see [Install](#install)). The API key YAV stored for it is
removed with it, or by `/login claude --forget-key`.

One thing is elsewhere: the first start of each version of `yav.exe` unpacks the native SQLite library
into `%TEMP%\.net\yav\<id>\` (about 2 MB; .NET does this for a program that is one file). Nothing of
yours is in it. A folder stays there for each version; `yav uninstall` does not remove it, and it can
be deleted while no `yav.exe` runs.

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
