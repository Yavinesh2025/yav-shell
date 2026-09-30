# YAV Shell

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

YAV Shell contains no model and no coding agent. It drives the agent programs you have installed
(Codex CLI, Claude Code) through their documented interfaces and with the accounts they are signed in
to. Priorities, in this order: correctness and security, then elapsed time, then usage.

**Version 0.1.1. Not code-signed. The Codex app-server interface it uses is labelled experimental by OpenAI.**
It was tested with scripted agents and has made **two** runs with real models, of one small task.
Read [what was verified and what was not](docs/verification.md) before relying on it.

## Use it

```powershell
scripts\package.ps1                      # builds dist\yav-shell-0.1.1-win-x64 and the portable zip
dist\yav-shell-0.1.1-win-x64\install.ps1 -AddToPath -Shortcut     # optional: install for your user account
yav doctor                               # what YAV needs, and what it found
cd C:\Projects\MyApp; yav                # the shell
```

The package brings the .NET runtime along. It is portable: `yav.exe` runs from wherever the package
was unpacked, and `install.ps1` is only needed for PATH, the Start menu and "Installed apps".

| | |
|---|---|
| [User guide](docs/user-guide.md) | commands, keys, `yav.project.json`, `yav run`, JSON output, exit codes |
| [Security boundaries](docs/security-boundaries.md) | what is enforced, by whom, and what is not |
| [Adapters](docs/adapters.md) | what each agent interface can do, tested versions, limitations |
| [Decisions](docs/decisions.md) | engineering decisions that were made while building, and why |
| [Verification](docs/verification.md) | what was verified and how, what was exercised where, what remains unverified |
| [Test results](docs/test-results.md) | the last complete run of the automated tests, class by class |
| [Performance](docs/performance.md) | measured: start, prompt, commands, what YAV adds to a run |
| [Benchmarks](bench/README.md) | the benchmark tasks, how to run them, and the results with scripted agents |
| [Examples](examples/README.md) | `yav.project.json` for three kinds of projects, a request in a file, JSON output of a run |

## Build and test

Requirements: Windows 11 x64, the .NET SDK 10.0.401 or a later 10.0 feature band (`global.json`), Git.

```powershell
scripts\build.ps1                 # builds everything
scripts\test.ps1                  # the automated tests: fixtures only, no inference is requested
scripts\test.ps1 -Live            # also asks the installed agents: version, account, models, schema. No inference
scripts\summarize-tests.ps1       # writes docs\test-results.md from the last run
scripts\mutation-check.ps1        # breaks the code in known ways and expects the tests to notice
scripts\package.ps1               # tests, then the package
scripts\verify-package.ps1        # runs the package the way a machine without .NET would
scripts\verify-package.ps1 -Sandbox   # the same on a new Windows, in Windows Sandbox
scripts\live-run.ps1              # ONE TASK WITH REAL MODELS. Its parts that ask a model consume usage and need -IAuthorizeUsage
```

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

## Layout

One program, built from modules that know each other only through the contracts in `Yav.Core`:

| Module | |
|---|---|
| `src/Yav.Console` | `yav.exe`: command line, the shell, rendering, input |
| `src/Yav.Coordinator` | a run from preparation to a candidate that is ready; apply, undo, recovery |
| `src/Yav.Adapters` | Codex app server, Codex exec, Claude Code |
| `src/Yav.Workspace` | isolated workspaces, baselines, candidates, journaled apply and undo |
| `src/Yav.Validation` | `yav.project.json`, trust, running required checks |
| `src/Yav.Storage` | SQLite: runs, evidence, usage, what you approved |
| `src/Yav.Platform` | processes and job objects, the console, the Windows Credential Manager |
| `src/Yav.Core` | contracts, the acceptance gate, profiles, prompt templates |
| `templates/` | the role instructions that are given to the models. Short, versioned, embedded at build time |
| `tests/Yav.Tests` | the automated tests |
| `tests/Yav.FakeAgent` | a scripted stand-in for the agent programs, which speaks their wire formats |
| `installer/`, `scripts/` | per-user install and uninstall scripts, Inno Setup script, build scripts |
| `bench/` | benchmark tasks, runner, results |

## License

Copyright (c) 2026 Yavinesh Rajagopal. All rights reserved; see [LICENSE.txt](LICENSE.txt).
Components by others that are distributed with YAV Shell remain under their own licenses:
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
