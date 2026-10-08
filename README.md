# YAV Shell

[![CI](https://github.com/Yavinesh2025/yav-shell/actions/workflows/ci.yml/badge.svg?branch=main&event=push)](https://github.com/Yavinesh2025/yav-shell/actions/workflows/ci.yml)

A console application for Windows in which two models of your choice work on your code: **Model A**
implements, **Model B** reviews independently, your project's required checks run alongside, and only
a candidate that passed all of it is offered for applying. It starts with `yav`, runs inside the
console you already use, and looks like a shell:

```
YAV C:\Projects\MyApp> Fix the login bug and add regression tests.
[PREPARE] Project rules and workspace verified (isolated worktree, 214 files)
[CODE A]  <model> is implementing the task (effort <value>)
[CHECK]   Reviewing and testing candidate 89038be2149b (2 file(s): +1 ~1 -0)
[REVIEW B] No blocking findings reported
[TESTS]   Required check passed
[READY]   Changes available for inspection (/diff) and application (/apply)
```

## Start

You need Windows 11 x64, on which YAV Shell was tested (Windows 10 and Windows on ARM were not tried),
and at least one of the agent programs it drives, Codex CLI or Claude Code, installed and signed in, as
described below the steps.

1. **Get `yav.exe`.** It is one file. Download it, with `yav.exe.sha256`, from the
   [latest release](https://github.com/Yavinesh2025/yav-shell/releases/latest) and check it as
   [SECURITY.md](SECURITY.md#checking-a-download) says. (To check that a published `yav.exe` matches the
   source, you may build it yourself: see [Build and test](#build-and-test).)
2. **Run it.** Double-click it, or start it in a console without arguments. `yav.exe` is not
   code-signed: if Windows SmartScreen says "Windows protected your PC", choose **More info**, then
   **Run anyway** (where Smart App Control is on, Windows does not start it at all). It offers to
   install itself for your user account, without administrator rights; `yav install` installs it
   without asking.
3. **Open a new PowerShell window in the folder of a project and type `yav`.**
4. **Type what you want done**, as in the example above. Text without a slash is a request; `/help`
   lists the commands.

The first request asks, before anything is sent, for what only you can decide: which model implements
and which reviews, chosen from the models the agents list for your accounts, and, where it applies,
that a route that bills an API key per token may be used, and that files Git ignores will be missing
from the isolated copy of the project. A subscription the agent is signed in to (the ChatGPT plan of
Codex, a Claude subscription) is used without a question. A project without an approved required check
runs on Model B's review alone; YAV names the checks it found in one line (`/test detect` or `/test trust` approves them)
and runs no command of the project you have not approved. While an agent thinks or works, a line that
moves shows the stage, what the agent is doing and the time since the stage began.
Your answers are kept. After that, a request is all it takes.

YAV Shell contains no model and no coding agent. It drives the agent programs you have installed
(Codex CLI, Claude Code) through their documented interfaces and with the accounts they are signed in
to. Install at least one of them, the way its vendor recommends, and sign in with it:

```powershell
powershell -ExecutionPolicy ByPass -c "irm https://chatgpt.com/codex/install.ps1 | iex"   # Codex CLI, then: codex login
irm https://claude.ai/install.ps1 | iex                                                   # Claude Code, then: claude auth login (or start claude and sign in)
```

Both come from the vendors' documentation ([Codex CLI](https://learn.chatgpt.com/docs/codex/cli),
[Claude Code](https://code.claude.com/docs/en/setup)); open a new console after installing. `yav doctor`
shows what YAV needs and what it found. Priorities, in this order: correctness and security, then
elapsed time, then usage.

**Version 0.2.1. Not code-signed: Windows SmartScreen may stop a downloaded `yav.exe` the first time it
is started (step 2 says how to go on), and on a PC where Smart App Control is on, Windows does not
start it at all. The Codex app-server interface it uses is labelled experimental by OpenAI.**
YAV was tested with scripted agents. With real models it has made **two** runs of one small task, with
versions 0.1.0 and 0.1.1; versions 0.2.0, which changes how YAV is installed and how the first request
is set up, and 0.2.1, which asks fewer questions and shows progress, have made none. The installation of 0.2.0 by `yav.exe` was verified with the automated tests
(2637 of 2637 passed, on 2026-10-07) and the checks of the package, made with a `yav.exe` built on the
development PC (29 of 29 where there is no .NET, no Git and no agent, and 37 of 37 on a new Windows, in
Windows Sandbox); its guided first request only with the automated tests, against a scripted stand-in
for the agents. The `yav.exe` of a release is built again by the release workflow, whose release text
says what was run on it (not Windows Sandbox).
Read [what was verified and what was not](docs/verification.md) before relying on it.

## Installed, and removed

Installing copies `yav.exe` to `%LOCALAPPDATA%\Programs\YavShell`, together with the license, the
notices, the documentation and the examples it carries; adds that directory to the PATH of your user
account, so that `yav` works in every console opened from then on; and lists YAV Shell under
"Installed apps". Nothing else is changed. A console that was open before the installation does not
know the new PATH yet: open a new one, or do what the installation says for that console.

`yav.exe` brings the .NET runtime along, so nothing else has to be installed for it. It also runs
without being installed, from wherever it is. A copy installed with `yav install --no-register`, or any
`yav.exe` that runs from a directory holding a `yav-install.json` of YAV Shell, is not offered
installation.

`yav uninstall`, or "Installed apps" in the settings of Windows, removes the program, its PATH entry and
its entry under "Installed apps", and leaves anything else in its directory where it is. Your data in
`%LOCALAPPDATA%\YavShell` (settings, history, isolated workspaces) stays, unless you add `--remove-data`
and type `yes`; even then it is removed only when it holds nothing that YAV did not create
([user guide](docs/user-guide.md#install)). When YAV Shell is not installed, `yav uninstall --remove-data`
asks nothing and removes nothing; it says so, and that the data folder can be deleted by hand.

| | |
|---|---|
| [User guide](docs/user-guide.md) | installing, the first request, commands, keys, `yav.project.json`, `yav run`, JSON output, exit codes |
| [Security boundaries](docs/security-boundaries.md) | what is enforced, by whom, and what is not |
| [Adapters](docs/adapters.md) | what each agent interface can do, tested versions, limitations |
| [Decisions](docs/decisions.md) | engineering decisions that were made while building, and why |
| [Verification](docs/verification.md) | what was verified and how, what was exercised where, what remains unverified |
| [Test results](docs/test-results.md) | the last complete run of the automated tests, class by class |
| [Performance](docs/performance.md) | measured: start, prompt, commands, what YAV adds to a run |
| [Benchmarks](bench/README.md) | the benchmark tasks, how to run them, and the results with scripted agents |
| [Examples](examples/README.md) | `yav.project.json` for three kinds of projects, a request in a file, JSON output of a run |
| [Specification](Build%20YAV%20Shell%20%E2%80%94%20Maximum-Quality%2C.txt) | the specification YAV Shell was built to; each release carries it as `YAV-Shell-Specification.txt` |

## Build and test

Requirements: Windows 11 x64, the .NET SDK 10.0.401 or a later 10.0 feature band (`global.json`), Git.

```powershell
scripts\build.ps1                 # builds everything
scripts\test.ps1                  # the automated tests: fixtures only, no inference is requested
scripts\test.ps1 -Live            # also asks the installed agents: version, account, models, schema. No inference
scripts\summarize-tests.ps1       # writes docs\test-results.md (or the file -Output names) from the last run
scripts\mutation-check.ps1        # breaks the code in known ways and expects the tests to notice
scripts\package.ps1               # tests, then dist\yav.exe: one self-contained file that installs itself
scripts\verify-package.ps1        # uses dist\yav.exe the way a machine without .NET would: starts, installs and removes it
scripts\verify-package.ps1 -Sandbox   # the same on a new Windows, in Windows Sandbox
scripts\live-run.ps1              # ONE TASK WITH REAL MODELS. Its parts that ask a model consume usage and need -IAuthorizeUsage
```

The `yav.exe` that `scripts\build.ps1` puts into `artifacts\` needs the files beside it, so it cannot
install itself; `yav install` says so there. The single file that can is `dist\yav.exe`, made by
`scripts\package.ps1`, with its SHA-256 in `dist\yav.exe.sha256`. Nothing is code-signed.

`scripts\live-run.ps1` makes a scratch project from a task of the benchmark, sets the shell up the way
a user does, gives the request to `yav run`, and looks at the result in the shell. Its parts `setup`
and `inspect` ask no model and run without `-IAuthorizeUsage`; its parts `run` and `resume` ask the
models and do nothing without it. It names no model for you. It agrees only to the account routes
you name, and only to the kind you name (`-Acknowledge codex,claude` means the two subscriptions;
`claude:api-key` means a key billed per token): a route of another kind stops the setup. It lets the
project's checks run only with `-TrustChecks`, declines whatever an agent asks for, and ends the
program it started when it is stopped. It needs PowerShell 7.2 or later. `Get-Help scripts\live-run.ps1 -Full`
describes its parts.

Build output goes to `artifacts\` and packages to `dist\`. Both are marked so that Dropbox does not
synchronize them.

On GitHub, the workflows in [`.github/workflows`](.github/workflows) run these scripts on a Windows
runner, with the SDK that `global.json` names. Every push to `main` and every pull request to `main` runs
`scripts\test.ps1 -Configuration Release -HangSeconds 900` (the stand-in only; no model is asked;
a test that hangs ends the run after 15 minutes and is named),
`scripts\package.ps1 -SkipTests` and `scripts\verify-package.ps1`. Each push to `main` has a run of
its own, which a later push does not end; a newer push to a pull request ends the run before it.
The tests with the installed agents and the mutation check do not run there, and no run with real
models is made: `scripts\live-run.ps1` is started there only by its tests, against a stand-in for
`yav.exe` that asks no model. The mutation check can be started by hand. A pushed tag `v<version>`
builds that version from the tag in the same way, tests included, and publishes a GitHub Release
with `yav.exe`, `yav.exe.sha256`, the Word guide (`YAV-Shell-<version>-Documentation-and-User-Guide.docx`)
and the specification (`YAV-Shell-Specification.txt`), with `docs/release-notes/<version>.md` as its text.
GitHub records where the release workflow built `yav.exe`, and this checks a download, with the
version of the release in place of `<version>` ([SECURITY.md](SECURITY.md#checking-a-download)):

```powershell
gh attestation verify yav.exe --repo Yavinesh2025/yav-shell --signer-workflow Yavinesh2025/yav-shell/.github/workflows/release.yml --source-ref refs/tags/v<version> --deny-self-hosted-runners
```

## Layout

One program, built from modules that know each other only through the contracts in `Yav.Core`:

| Module | |
|---|---|
| `src/Yav.Console` | `yav.exe`: command line, the shell and its guided first request, rendering, input; in `Install/`, how it installs and removes itself |
| `src/Yav.Coordinator` | a run from preparation to a candidate that is ready; apply, undo, recovery |
| `src/Yav.Adapters` | Codex app server, Codex exec, Claude Code |
| `src/Yav.Workspace` | isolated workspaces, baselines, candidates, journaled apply and undo |
| `src/Yav.Validation` | `yav.project.json`, trust, running required checks |
| `src/Yav.Storage` | SQLite: runs, evidence, usage, what you approved |
| `src/Yav.Platform` | processes and job objects, the console, the Windows Credential Manager; in `Install/`, the PATH of the user and "Installed apps" |
| `src/Yav.Core` | contracts, the acceptance gate, profiles, prompt templates |
| `templates/` | the role instructions that are given to the models. Short, versioned, embedded at build time |
| `tests/Yav.Tests` | the automated tests |
| `tests/Yav.FakeAgent` | a scripted stand-in for the agent programs, which speaks their wire formats |
| `scripts/` | build, test, package and verification scripts |
| `bench/` | benchmark tasks, runner, results |

## License

Copyright (c) 2026 Yavinesh Rajagopal. YAV Shell is proprietary software that is **free to use**: anyone
may download and use `yav.exe`, personally or at work, free of charge. The source code is published to be
read and reviewed; copying, modifying or redistributing it needs the copyright holder's written permission.
Viewing and forking this repository on GitHub, as GitHub's Terms of Service permit for public repositories,
is permitted; a fork grants no rights beyond those the license states.
The terms are in [LICENSE.txt](LICENSE.txt). Components by others that are distributed with YAV Shell remain under their own licenses:
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
