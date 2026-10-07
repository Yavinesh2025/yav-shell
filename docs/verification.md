# Verification

What was verified for version 0.2.0, how, and what was **not**; and what was verified for 0.1.0 and
0.1.1 before it, which is kept as their record. Everything was done on one machine, which
[performance.md](performance.md) describes. The command that repeats a check is given with it.

**Version 0.2.0** changes how YAV is installed and how its first run is set up. The package is one
file, `yav.exe`, that installs itself for your user account when it is run, and puts its directory on
the PATH of the account, so that `yav` starts it in every console that is opened afterwards. A request
that is typed before the shell is set up asks, right there, what only you can decide: which project,
which models, which account route, which checks, and whether the differences between a project and
its isolated copy are acceptable. Then it is sent. [Version 0.2.0](#version-020) says what was
checked for it. **No run with real models was made with 0.2.0**: none was authorized for it.

**Versions 0.1.0 and 0.1.1**, as recorded on 2026-09-29 and 2026-09-30. Version 0.1.0 was the first
that was packaged. The first run with real models found that it could not start a review by Claude
Code; 0.1.1 is the version in which that is corrected.
[The first run with real models](#the-first-run-with-real-models) tells what happened. After that
run, the code was read by reviewers that had not written it, and what they found was corrected;
[What the reviews found](#what-the-reviews-found) lists it. **Version 0.1.1, with those corrections,
made a second run with real models** on 2026-09-30, of the same task and with your authorization. It
went through without a person and was applied;
[The second run with real models](#the-second-run-with-real-models) tells what it showed and what
it did not touch.

## Version 0.2.0

The tests and the checks of the package below were made on 2026-10-07, after the last change to the
program: the automated tests from 19:15 to 19:25, the package built at 19:26, its check where there is
no .NET at 19:26, its check in Windows Sandbox from 19:26 to 19:28, and the mutation check from 19:29
to 21:43 (local time, UTC+08:00). After them only documents were changed, this one among them.
`yav.exe` carries the documents, so a `yav.exe` built from the final source has another SHA-256 than
the one below.

| What | How | Result |
|---|---|---|
| The automated tests | `scripts\test.ps1` | 2612 of 2612 passed on 2026-10-07. [test-results.md](test-results.md) lists them class by class. They use a scripted stand-in for the agents |
| That the tests can fail | `scripts\mutation-check.ps1` | 456 defects were put into the code one at a time; the tests noticed all 456. 112 of the entries are new for 0.2.0: 59 for installing (the installer, `yav install` and `yav uninstall`, the offer at start), 29 for the guided first run, 14 for review-only acceptance, 6 for the scripts that build and check the package, 2 for the state of a run, 1 for plain text where `GITHUB_ACTIONS` is set and 1 for how `yav doctor` names Windows Server. One entry of 0.1.1 was changed, and the 8 for 0.1.1's PowerShell installer went with it. The whole list was run, in slices. The run was stopped once, at about 20:30, when this machine ran low on memory; the 190 entries it had not finished were run again from the start |
| The package: one file | `scripts\package.ps1` | `dist\yav.exe`, 95.6 MB (100,272,056 bytes), SHA-256 `4f19327b057874b1d314006e3f27df5cab1ff138988d3c18164a05f4512b11cb`. Nothing is code-signed |
| The package where there is no .NET, no Git and no agent | `scripts\verify-package.ps1` | 29 of 29 checks, on this machine with a PATH that holds nothing but Windows. Among them: `yav install --dir`, with `--no-path` and `--no-register`, installs into a directory whose name has a blank, brackets and `ü`; what was installed is the program of the package and starts; `yav uninstall` removes what was installed and nothing else |
| The package on a new Windows | `scripts\verify-package.ps1 -Sandbox` | 37 of 37 checks, in Windows Sandbox without network: the 29 above; that no .NET is installed there; `yav install` with its defaults, with the PATH entry and the entry under "Installed apps"; `yav` found by its name in a console opened afterwards; and `yav uninstall`, after which none of it is left |
| The installed agents answer as YAV expects | `scripts\test.ps1 -Live` | **Not run for 0.2.0.** Codex CLI 0.160.1 and Claude Code 2.1.292 are installed now, newer than the versions YAV calls tested. [Fixture tests and live tests](#fixture-tests-and-live-tests) has what the six found for 0.1.1 |
| **A request worked on by real models** | `scripts\live-run.ps1` | **Not done for 0.2.0.** No run with real models was authorized for this version. The two runs of 0.1.0 and 0.1.1 are described below |
| A benchmark with real models | `yav-bench run --mode live` | **Not done.** It consumes usage of your accounts and is yours to start |

The checks of the package above were made with the `yav.exe` that `scripts\package.ps1` built on this
machine, SHA-256 `4f19327b057874b1d314006e3f27df5cab1ff138988d3c18164a05f4512b11cb`. The
`yav.exe` attached to the GitHub Release is not that file: the release workflow builds it again, from
the tag, on Windows Server 2025 (`windows-2025`), runs the automated tests and
`scripts\verify-package.ps1` there, and gives its own SHA-256 in the release text and in the
attestation GitHub records for it. That file was not run in Windows Sandbox. On the runner, the tests
that need PowerShell from the Microsoft Store are reported as skipped, that is, not run.

### Installing: one `yav.exe` that installs itself

When YAV Shell is not installed for your user account and `yav.exe` is started without arguments in
a console, it asks whether to install itself; `yav install` installs without asking. Started by a
double-click while an older version is installed, it offers to replace that one. It installs for the
current user, without administrator rights. The program and the files it brings along (the license,
the notices of the components that are part of it, the documentation and the examples) go to
`%LOCALAPPDATA%\Programs\YavShell`, or to the directory named with `--dir`, together with a list of
what was put there, `yav-install.json`. The directory is added to the PATH of the account unless
`--no-path` is given, and YAV Shell appears under "Installed apps" unless `--no-register` is given.
`yav uninstall` removes the entry in the PATH, the entry under "Installed apps", and the files the
list names; other files in the directory stay, and so does the directory then. Your data stays where
it is, unless `--remove-data` is given and you confirm it; even then a data directory that holds
anything YAV did not create, or whose database another YAV has open, is not removed, and nothing else
is either. A directory that 0.1.1's `install.ps1` made is updated and removed like one of 0.2.0.

| Checked | How | Result |
|---|---|---|
| Installing: a new installation, one over an earlier version and over one of 0.1.1, a damaged file put right, an interrupted one completed; refused before anything is written: a directory that holds something else, a YAV that runs from there, the data directory, the root of a drive, a path of 260 characters or more, a `;` with the PATH | tests with directories of their own, whose names have blanks, brackets and letters of other alphabets (`InstallerTests`); the registry is a stand-in in them | 53 of 53 passed |
| Removing: only what the list names; a directory that holds files of yours is kept; the entry under "Installed apps" only when it names this directory; the program that runs is left in place, with a list that names only it and the text of this removal, and deleted after it has ended by the real `cmd.exe`, which leaves a newer installation in the same directory alone | `InstallerTests`, `InstallCommandTests` | passed: the `InstallerTests` above, and 66 of 66 `InstallCommandTests` |
| The text of the PATH: the directory added at the end and only once, removed without touching anything else; `%VARIABLES%`, empty parts and the kind of the value kept | `PathListTests`, with every case of the tests of 0.1.1's PowerShell installer | 18 of 18 passed |
| `yav install` and `yav uninstall`: what they say, their exit codes, `--remove-data` (what it takes for a data directory of YAV, and what it refuses before it asks), a build of the source tree that cannot install itself and removes only an installation named with `--dir`, a `--dir` without a value | `InstallCommandTests`, `CommandLineTests` | passed: the `InstallCommandTests` above, and 51 of 51 `CommandLineTests` |
| The offer at start: when it is made, what a "no" does, a "no" that cannot be saved, a failure in it, a console that Windows made for `yav` alone, a window that was started hidden | `InstallOfferTests` | 51 of 51 passed |
| The real program, installed and removed where there is no .NET | `scripts\verify-package.ps1` | passed, on this machine and in Windows Sandbox: `yav install --dir` with `--no-path` and `--no-register`, into a directory whose name has a blank, brackets and `ü`; the installed program is that of the package and starts; the license, the notices, the license texts, the documentation and the examples are beside it (28 files), and its list names all 29; PATH and "Installed apps" are left alone; a directory that holds something else is refused; `yav uninstall` removes what was installed, keeps a file of yours and with it the directory, and keeps your data |
| The PATH and "Installed apps" of a real user account | `scripts\verify-package.ps1 -Sandbox`, in the user account of Windows Sandbox. YAV was not installed in the account of this machine | passed: `yav install` into `%LOCALAPPDATA%\Programs\YavShell`, on the PATH of the user once, under "Installed apps" with `"...\YavShell\yav.exe" uninstall`; `yav` found by its name in a console opened afterwards; after `yav uninstall` the directory, the program that removed itself included, the PATH entry and the entry under "Installed apps" are gone |

### The first run: asked when it is needed

A request that cannot start for a reason only you can settle is no longer refused with a list of
commands to type. The shell asks, one question at a time, and sends the same request afterwards:

| What kept the request from starting | What the shell asks |
|---|---|
| No project is selected | the folder of the project |
| Model A or Model B is not chosen, or both are the same model | the number of a model from the list the agents give for your accounts. YAV marks the provider's own default and does not choose |
| The model lists an effort YAV cannot rank, such as `ultra` of Codex | the exact effort, one of those listed |
| An agent is not signed in | for Codex, whether to start its own sign-in; for Claude Code nothing: it says how to sign in with Claude Code itself |
| An account route is not acknowledged | whether to use it, billed as stated; you type `yes` |
| The project has no approved check that is required | the checks of its `yav.project.json`, or those YAV found in it, for your approval (`yes`); where there are none, you decline them, or what you approved requires none, whether a candidate of this project may be accepted on the review alone (`yes`), but not while the approved checks cannot be read |
| The isolated workspace would differ from the project, for example because files ignored by Git would be missing from it | whether that is accepted for the project (`yes`) |

What you answer is recorded the way the commands record it (`/models`, `/effort`, `/login`,
`/test trust`, `/test detect`, `/open --accept-gaps`); consent needs the word `yes`, and a single key
grants nothing. Nothing of this is asked in `yav run`, or when the input comes from a pipe or a file:
nobody is there to answer, and the request is refused as before.

| Checked | How | Result |
|---|---|---|
| Every question, its answer, what it records, and the request sent again afterwards | `ShellSetupTests`, in the shell with a terminal that is kept in memory and the scripted stand-in for the agents | 39 of 39 passed |
| A "no" ends the questions, and nothing is sent | `ShellSetupTests` | passed, among the 39 above |
| Nobody is asked without a person: `yav run`, input from a pipe | `ShellSetupTests`, with input from a file and output that nobody sees, with and without a project; `CliCommandTests` for `yav run`; `scripts\verify-package.ps1`, `yav run` of the package without models | passed: nothing is asked and nothing is sent; `yav run` of the package ends with exit code 2 and names `model-a-missing`, `model-b-missing` and `gates-missing` |
| The questions with the installed agents | not tried | **Not done.** The installed agents were asked for nothing but their version (`codex --version`, `claude --version`) |

### Review-only acceptance for one project

New in 0.2.0. Under the default policy a project without an approved required check could not run at
all, unless `/quality gates optional` was set, which holds for every project. Now it can be accepted for
one project: a candidate of that project is accepted on the review of Model B alone, as long as the
project has no approved required check. As soon as a required check is approved, checks are required
again; an optional approved check does not set the acceptance aside. When the approved checks cannot be
read, the acceptance does not apply and the run is blocked (`review-only-unknown`).
`/quality gates required` withdraws the acceptance for the project that is selected, and `/quality`
shows it; a candidate accepted on the review alone in a run that started before the withdrawal is then
not applied.

| Checked | How | Result |
|---|---|---|
| Without the acceptance the run is blocked; with it the run starts, its policy does not require checks, a candidate is accepted on the review, and a note says so | `ReviewOnlyTests` | 14 of 14 passed |
| An approved required check makes checks required again, an optional one does not; withdrawing the acceptance blocks the run again; `yav run` behaves the same | `ReviewOnlyTests` | passed, among the 14 above |
| Approved checks that cannot be read block the run instead of being taken as none; a candidate accepted on the review alone is not applied after the acceptance was withdrawn, and is applicable again when it is accepted once more | `ReviewOnlyTests` | passed, among the 14 above |
| The acceptance is stored, read and withdrawn | `TrustStoreTests`, with a SQLite file of the test's own | 8 of 8 passed, two of them new in 0.2.0: the acceptance holds for its project until it is withdrawn, and when it was withdrawn is kept |

### Not verified for 0.2.0

* **No run with real models was made with 0.2.0.** None was authorized. That a request set up by the
  new questions leads to a run that works with real models was therefore not seen.
  The code of the adapters is unchanged since 0.1.1. What a run does after it has started changed in
  two places, apart from the wording of a message: a request that changes no file ends as Completed,
  where 0.1.1 left it in the state Checking, and `/apply` does not write a candidate accepted on the
  review alone once that acceptance was withdrawn after the run started. Before a run starts, the
  acceptance of the review alone applies, or blocks the run when the approved checks cannot be read.
* **The guided first run was exercised with the scripted stand-in.** The models it listed, the
  account routes it asked about and the checks it proposed came from the stand-in.
  The questions were not tried with the installed agents, not even up to the point where a request
  would have been sent: the installed agents were asked for nothing but their version.
* **Installing into the account of a real user.** The tests use a stand-in for the registry.
  The check where there is no .NET installs with `--dir`, `--no-path` and `--no-register`, and finds
  the PATH and "Installed apps" left alone. Windows Sandbox was run: there `yav install` wrote the
  PATH entry and the entry under "Installed apps" of the sandbox's own user account, and
  `yav uninstall` removed both. YAV 0.2.0 was not installed in the account of this machine, and not
  removed from it.
* **Starting `yav.exe` from Explorer.** YAV recognizes that Windows made the console for it alone by
  the number of processes that use the console.
  It also requires a console window that was not started hidden, because a program started without
  one (`CREATE_NO_WINDOW`) is alone in its console as well. A double-click on `yav.exe` in Explorer
  was not tried by hand, neither with the console host of Windows nor with Windows Terminal as the
  default terminal. The tests give the number of processes and the window to the decision as values
  (`InstallOfferTests`).
* **A console that is opened after the installation.** A console that was open before does not get
  the new PATH: a program keeps the environment it was started with.
  In Windows Sandbox, `cmd.exe` was started after the installation with the PATH that Windows puts
  together from that of the machine and that of the user, and found `yav` by its name. A console
  opened by hand, which gets its environment from Explorer, was not tried: that Windows hands the new
  PATH to it was not seen.

* **A downloaded `yav.exe`.** Windows may warn before a program from the internet starts that is not
  signed. That was not tried, because a file built on this machine is not marked as downloaded.
* **Updating an installation.** There is no earlier version that installs itself.
  An installation over an earlier one was tested with stand-ins: a list written by the test that
  names version 0.1.9 is updated by a new installation, which removes what only the earlier one had
  and names its version (`InstallerTests`); the offer to replace an older installation was tested
  with a stand-in for the console and the registry (`InstallOfferTests`).
* **A directory into which 0.1.1's `install.ps1` installed.** It holds `package-manifest.json`, not
  `yav-install.json`, and is registered under the same entry of "Installed apps". 0.2.0 reads that
  list as the one of an earlier installation: `yav install` updates the directory and removes what
  0.1.1 had and 0.2.0 does not (`uninstall.ps1` and `package-manifest.json` among it), and
  `yav uninstall` removes it. Nothing has to be done first. A Start menu entry that 0.1.1 made with
  `-Shortcut` is not touched; delete it yourself. The test
  `An_installation_of_yav_shell_0_1_1_is_updated_and_removed` writes such a directory and its entry,
  with a stand-in for the registry.

## In short: version 0.1.1

The record of 0.1.1, as it was made on 2026-09-29 and 2026-09-30. The package it describes is the
folder `dist\yav-shell-0.1.1-win-x64` with its portable zip, installed by `install.ps1`.

| What | How | Result |
|---|---|---|
| The automated tests | `scripts\test.ps1` | 2312 of 2312 passed on 2026-09-30. They use a scripted stand-in for the agents |
| That the tests can fail | `scripts\mutation-check.ps1` | 352 defects were put into the code one at a time; the tests noticed all 352, two of them only after a test was added or corrected ([What the checks found](#what-the-checks-found)) |
| The package where there is no .NET, no Git and no agent | `scripts\verify-package.ps1` | 20 of 20 checks |
| The package on a new Windows | `scripts\verify-package.ps1 -Sandbox` | 26 of 26 checks, in Windows Sandbox without network, with the package as it was before the last two changes (below); not repeated after them |
| The benchmark tool and its tasks | `yav-bench verify`, `yav-bench run` | 12 tasks are sound; 156 of 156 runs with scripted agents |
| The installed agents answer as YAV expects | `scripts\test.ps1 -Live` | 6 of 6. Handshakes only; no request reached a model. Claude Code had updated itself to 2.1.285 |
| **A request worked on by real models** | `scripts\live-run.ps1` | **Two runs, of the same small task, each with your authorization.** The first found two defects of version 0.1.0, which are corrected. The second, with 0.1.1, went through without a person in 3 min 27 s and was applied; the checks of the task pass in the project. Two runs are not a proof; see below what they did not touch |
| A benchmark with real models | `yav-bench run --mode live` | **Not done.** It consumes usage of your accounts and is yours to start |

For 0.1.1, the tests were run completely after the last change to the program. The mutation check
ran in four copies of the source side by side, each over a quarter of its entries; what was changed
after the copies had been made was checked again on the final source: every entry on a file that
changed, and the two mutations the tests had not noticed at first, after the correction. The
measurements, the benchmark and the check on a new Windows were made with the program as it was
before its last changes: the section that tells Model A which checks YAV runs itself, the copyright
in the properties of the program, the versions of the agents that are called tested, and how a turn
is reported that YAV ends together with the agent's process. None of them changes what they measure
or check, but they were not repeated. The second run with real models was made before the last two
of those changes. The tests and the mutations of the changed code were run after all of them.
Documents of the package, this one among them, were completed last; the package was then built
once more and checked where there is no .NET.

## Fixture tests and live tests

**Fixture tests** are all the automated tests. In them the agents are `tests/Yav.FakeAgent`, a
program that is started in place of `codex` and `claude`, speaks their wire formats over real
pipes and does what a script tells it: write a file, ask for approval, hang, crash, answer with
something that is not JSON. Everything else is real: processes, job objects, Git, files, SQLite, the
console.

| Layer | Tests, 0.1.1 | Tests, 0.2.0 | What is real in it |
|---|---:|---:|---|
| Core: acceptance gate, profiles, review parser, cleaning of terminal output, usage, timing | 233 | 234 | the code; no process, no file |
| Platform: processes, job objects, command lines, credentials, programs from the Store | 75 | 75 | Windows itself: child processes are started and ended, the Credential Manager is written under names of the tests' own |
| Storage | 32 | 34 | SQLite files, migrations |
| Validation: `yav.project.json`, trust, required checks | 57 | 57 | the checks are processes that run |
| Workspace: isolation, baseline, candidate, apply, undo, merge | 148 | 148 | Git repositories, worktrees and files in temporary directories |
| Adapters | 317 | 317 | the adapters, talking to the stand-in over pipes |
| Coordinator: a run from preparation to a candidate, repair, recovery; since 0.2.0 review-only acceptance | 308 | 322 | everything below the console |
| Console: parsing, rendering, the line editor, questions of agents, JSON, every command of the shell; since 0.2.0 the guided first run | 796 | 866 | the shell runs against a terminal that is kept in memory |
| Installation, new in 0.2.0: the installer, `yav install` and `yav uninstall`, the offer at start | - | 204 | directories and files in temporary directories; the registry is a stand-in. Real are the processes of Windows, which are asked which of them runs a program file, also by a short name or through a junction, and `cmd.exe` with `PING` and `FINDSTR`, running the command line that deletes the program after it has ended |
| The program itself, started with pipes | 23 | 24 | `yav.exe` as a process: `yav run`, `yav doctor`, input from a pipe, exit codes |
| The program itself, in a console | 48 | 48 | `yav.exe` in the pseudo console of Windows: keys go in, the screen is read; among them the driver of `scripts\live-run.ps1` |
| Scripts: the build scripts, `scripts\live-run.ps1`, version numbers; the examples | 213 | 221 | `live-run.ps1` runs against stand-ins for the agents; the examples are run through the program. In 0.1.1 Windows PowerShell 5.1 also ran the installer scripts, which are not part of 0.2.0 |
| Benchmark tool | 60 | 60 | the tasks' checks run with Python and Node.js |
| The tests' own tools: temporary directories | 2 | 2 | junctions in the file system |

**Live tests** talk to the agents that are installed. There are six, they run only with
`scripts\test.ps1 -Live`, and with none of them a request reaches a model. For 0.1.1 they were run
last on 2026-09-30 in the evening, after all changes and after the second run with real models; all
six passed, and both installed agents were reported as tested. The table shows what they found then.
For 0.2.0 they were not run. On 2026-10-07 the installed agents were asked only for their version:
Codex CLI 0.160.1 and Claude Code 2.1.292, both newer than the versions YAV calls tested (Codex 0.158
and 0.159, Claude Code 2.1.284 and 2.1.285), so YAV warns for them. `scripts\test.ps1 -Live` asks no
model, and you can run it.

| Test | What it found on 2026-09-30 |
|---|---|
| Codex answers the handshake and lists its models | **Codex CLI 0.159.2**, signed in with a ChatGPT plan; 8 models, a new default among them (`gpt-6.1-sol`); efforts up to `max`, for some models also `ultra`; a faster tier `priority`; the Windows sandbox of Codex is set up |
| Codex reports the settings in effect for a thread | for `read-only` and for `workspace-write`, in a thread that is not kept |
| Codex says who decides about access and what widens its sandbox | `approvalsReviewer: "user"`; no network, no folder outside the directory; the directory it reports is the one it was given |
| What YAV says to Codex is in the schema the installed Codex generates | every name of a request and a notification the adapter uses is among the 198 of the schema, and so are the parameters YAV sends |
| Claude Code reports its version, account route and models | **Claude Code 2.1.285**, signed in with a subscription; 12 models; efforts up to `max` |
| Claude Code confirms the effort and the boundary of a review | effort `max` from its answer to `get_settings`; the tools `Glob`, `Grep`, `Read` and `StructuredOutput`; permission mode `dontAsk` |

**Both agents updated themselves.** Claude Code went from 2.1.284, the version the first run was
made with, to 2.1.285 between 2026-09-29 and 2026-09-30; Codex went from 0.158.0 to 0.159.2 during
2026-09-30, between the live tests of the morning and the second run with real models. The live tests
pass with both, and the second run was made with both. Afterwards you had both newer versions
called tested: YAV now calls Claude Code 2.1.284 and 2.1.285, and Codex 0.158 and 0.159, tested, and
warns for every other version in `yav doctor` and at the start of a run.

The last test in the table was added after the first run with real models, and it is the check that
would have found the defects of that run beforehand. Claude Code says which tools a conversation has
only when the conversation gets its first prompt. The test therefore writes a prompt to the program,
with the address of the API set to a port of this machine that nobody listens on: the program says
what it would work with, cannot reach a model, and is ended.

What the live tests do not show: that a turn with a real model runs the way the stand-in plays it.
The stand-in was written from the generated schema of Codex 0.158.0 and from the published type
definitions of the Claude Agent SDK 0.3.284, and corrected where the first run with real models
showed that Claude Code does something else than its types suggest. Where else it differs from the
real agents would show in live runs only.

## The first run with real models

Made on 2026-09-29 with `scripts\live-run.ps1`, after you authorized one run of one small task and
agreed to both account routes. Everything it wrote is kept in
`artifacts\live-run\20260929-141605`: what the console showed, the JSON output, the data directory
with the record of the run, the project, and in `probes\` what was asked of the installed agents by
hand afterwards, with the scripts that asked.

A second run followed on 2026-09-30, with version 0.1.1; it has a section of its own below. No
benchmark with real models was made.

| | |
|---|---|
| Task | the smallest of the benchmark, `01-small-edit`: "The greeting should end with a period instead of an exclamation mark, and blanks around the name should be ignored." A Python project of three files, to which the set-up adds `yav.project.json` with one required check, `python -m unittest discover -s tests -t .` |
| Model A | `gpt-6-astra` through `codex-app-server` (Codex CLI 0.158.0), effort `max`, ChatGPT plan |
| Model B | `opus`, which is `claude-opus-5-5`, through `claude-cli` (Claude Code 2.1.284), effort `max`, Claude subscription |
| Policy | Quality Lock on, strict; two repair cycles; standard speed |

What happened, in order:

1. **Set-up in the shell**, in a pseudo console: `/models`, `/effort`, `/login` for both routes,
   `/test trust`, `/doctor`. No model is asked for any of it. YAV refused to take "maximum" for
   `gpt-6-astra`, because that model lists `ultra` as well; the exact value `max` was set.
2. **`yav run --json`** with version 0.1.0. Preparing took 13 seconds. Model A changed two files and
   then asked to run the tests *outside its sandbox*: inside the Windows sandbox of Codex, `python`
   and `py` were "not recognized", although the PATH it printed names the folder of Python 3.14.
   Nobody can answer in this mode, so YAV granted nothing and ended the run as
   **Approval Required**, exit code 3, after 88 seconds. No file of the project was changed. Git
   keeps a note about the worktree of the run in the project's `.git` folder (`.git\worktrees\w`).
3. **`/resume` in the shell**, where a question can be answered. Model A asked again, and the
   answer was *decline*. **It was not you who declined.** The program that drives the shell pressed
   the key, as it does for every question of an agent: it never grants anything, because only a
   person may. The record of the run names "user" as who decided, because the answer came from the
   keyboard of the console; read that row with this in mind. Model A finished without having run
   the tests and said so. YAV froze the candidate, noticed that a file of existing tests was
   changed, and ran the required check itself: passed. Then **YAV blocked its own review**: Claude
   Code had confirmed neither the effort nor the read-only boundary. The review was stopped 4.9
   seconds after it was started, 60 milliseconds after Claude Code said that the turn began. Claude
   Code reported no tokens for it and a charge of 0.
4. **Two defects of the adapter for Claude Code** were the cause; the table below names them. They
   were corrected, which made version 0.1.1.
5. **`/resume` with the package of 0.1.1 that was built at 15:10 UTC.** Nothing was sent to
   Model A. Model B reviewed for 1 minute 55 seconds with `Glob`, `Read` and `Grep`. It tried twice
   to look into the original project; Claude Code denied that, because `--restricted` confines its
   file tools to the directories it was given. The result was **Pass** without findings and with
   three limitations, one of them that it could not run the tests itself. The check passed again.
   The run was **Ready to Apply**.
6. **`/apply`** wrote the two files into the project. The checks of the task, run in the project
   afterwards by the driver and not by YAV: 4 tests, OK.

Two things the driver did in your place, which it does not do any more without being told:

* It answered **yes to `/test trust`**, which lets the required check of the project run. The check
  was the one of the benchmark task that you had chosen. `scripts\live-run.ps1` now answers yes
  only when it is started with `-TrustChecks`.
* It **declined** what Model A asked for, as said above. That stays: the driver declines, always.

| | Model A | Model B |
|---|---|---|
| Turns | 2 | 2, of which one was stopped before it used anything |
| Tokens | 207,948: input 12,225, read from cache 194,048, output 1,675, of it reasoning 594 | 97,971: input 16, read from cache 76,570, written to cache 11,179, output 10,206, of it reasoning 7,159 |
| Charge | not reported | 0.31 US dollars, which is Claude Code's estimate and not a bill; the route is a subscription |

[performance.md](performance.md) has the times.

**What this run exercised with real agents:** the commands of the set-up; preparation; the
confirmation of model, effort, sandbox and tier for both models; events as they arrive; commands an
agent runs; a question of an agent in both modes; continuing a run that had stopped, twice, the
second time with a newer version of the program; freezing; the notice about changed tests; review
and check side by side; a structured review result; the boundary of the reviewer, enforced by
Claude Code; usage and rate limits as reported; `/history`, `/status`, `/diff`, `/review show`,
`/usage`, `/latency` and `/apply`.

**What the run did not confirm, although its record says "Verified":** the account. Codex said
nothing about it in the conversation. For Claude Code the record says `credential source: none,
Verified`. All that was known is that Claude Code named no key, which a subscription, a token and
a cloud provider have in common. Since 0.1.1, YAV asks the conversation itself which account it
works with, calls the route verified only when the conversation names it, and calls "no key is
named" what it is: not confirmed.

**What was changed after 15:10 UTC on 2026-09-29** is most of what
[What the reviews found](#what-the-reviews-found) lists. The second run, below, was the first to
run with it.

## The second run with real models

Made on 2026-09-30 with `scripts\live-run.ps1`, after you authorized one more run of one small task
with the same models, efforts and account routes as the first. It was made with the program as it
was before two later changes, neither of which came up in it: the two newer versions of the agents
are called tested now, and a turn that YAV ends together with the agent's process is reported that
way, whatever the agent says while it ends. Everything the run wrote is kept in
`artifacts\live-run\20260930-100510`.

| | |
|---|---|
| Task | the same as in the first run, `01-small-edit` |
| Model A | `gpt-6-astra` through `codex-app-server` (Codex CLI **0.159.2**, which YAV called untested then and said so at the start of the run), effort `max`, ChatGPT plan |
| Model B | `opus`, which is `claude-opus-5-5`, through `claude-cli` (Claude Code **2.1.285**, untested then as well), effort `max`, Claude subscription |
| Policy | Quality Lock on, strict; two repair cycles; standard speed |

What happened, in order:

1. **Set-up in the shell**, as in the first run; no model was asked. Both account routes were
   acknowledged as the agents reported them: "ChatGPT plan (pro)" and "Claude subscription (max)".
2. **`yav run --json`, without a person.** Model A was told, for the first time, which check YAV runs
   itself. It read the project, changed two files, looked for a Python it could start inside its
   sandbox, found none, and said so in its report: "Unit tests could not run because no Python
   runtime was accessible within the sandbox." **It asked for nothing.** In the first run it had
   asked to run the tests outside its sandbox, and that had ended the run as Approval Required.
3. YAV froze the candidate, noticed that a file of existing tests was changed, and ran the required
   check itself: passed, in 138 ms. Model B reviewed at the same time, for 1 minute 42 seconds. It
   tried to look into the original project, and Claude Code denied that, as in the first run. The
   result was **Pass**, with one optional finding (a name that is not text now raises an error
   instead of being printed) and three limitations, one of them that it could not run the tests
   itself.
4. **Ready to Apply** after 3 minutes 27 seconds, exit code 0. `/apply` wrote the two files into the
   project, and the checks of the task, run there afterwards by the driver and not by YAV: 4 tests,
   OK.

What YAV confirmed before a model was given anything, now with the corrections of the reviews:

| | Model A (Codex) | Model B (Claude Code) |
|---|---|---|
| Model, effort, sandbox, tier | as requested, from the answer to `thread/start` | as requested; the effort from the answer to `get_settings` |
| The account the conversation works with | "ChatGPT plan (pro)", from `account/read` | "subscription (Claude Max)", from the answer to `initialize`: the check that the first run did not have |
| The directory it works in | the workspace | the workspace |
| Its boundary | you decide about access; no network; nothing writable outside the workspace | read-only: the tools `Glob`, `Grep`, `Read` and `StructuredOutput`, permission mode `dontAsk`, none of your settings loaded |

| | Model A | Model B |
|---|---|---|
| Turns | 1 | 1 |
| Tokens | 157,188: input 11,913, read from cache 143,616, output 1,659, of it reasoning 536 | 90,982: input 16, read from cache 66,478, written to cache 15,334, output 9,154, of it reasoning 5,824 |
| Charge | not reported | 0.32 US dollars, which is Claude Code's estimate and not a bill; the route is a subscription |

[performance.md](performance.md) has the times. **What neither run touched** is listed under
[Not verified](#not-verified).

## The consoles

The specification asks for the real program to be exercised in Windows Terminal, PowerShell and
CMD, with redirected output, JSON output and the return from a shell.

| | How it was exercised |
|---|---|
| A console | The **pseudo console of Windows** (ConPTY). It is the interface through which Windows Terminal, and every other modern terminal on Windows, hosts a program. A test creates one, starts `yav.exe` in it, sends what a keyboard sends and reads the screen from what the program draws |
| Started from PowerShell 7, Windows PowerShell 5.1 and CMD | each of the three is started in the pseudo console, `yav` is started from it, used, and left; the shell has to be usable afterwards |
| Keys | Enter, Shift+Enter, Ctrl+J, a line feed as a terminal sends it, Ctrl+C, Tab, arrows, Esc, pasted text of several lines, text in other scripts |
| `/shell` and `/exec` | a real `cmd` takes the console, writes a file, and gives the console back |
| A window that is made narrower | the pseudo console is resized while YAV runs |
| Redirected and JSON output | `yav.exe` with pipes: no control sequence in plain output, nothing but JSON in JSON output |
| A console that Windows made for `yav` alone, as when it is started from Explorer (0.2.0) | not in the pseudo console: the tests there start the build of the source tree, which never offers to install itself. The offer was exercised against a stand-in for the console and the registry (`InstallOfferTests`), which is told how many processes use the console and whether it has a window. A double-click in Explorer was not tried by hand |
| The questions of the first run (0.2.0) | in the shell with a terminal kept in memory (`ShellSetupTests`); not in the pseudo console |

**Windows Terminal itself was not driven.** Nobody clicked in its window, and no test did. What is
covered is what the program receives and draws; what is not covered is what belongs to the terminal
application: its fonts and the width it gives to characters, its own copy and paste, selecting with
the mouse, input methods for East Asian languages, and what it does with text when its window is
resized. The same holds for the window of the old console host (`conhost`).

## The package and the installer

| | |
|---|---|
| The package | one file, `dist\yav.exe`, with its SHA-256 in `dist\yav.exe.sha256`. It is self-contained: the program, .NET, the native library of SQLite, and the files it puts next to itself when it installs itself (the license, the notices, the documentation, the examples) are all in it. 95.6 MB (100,272,056 bytes), built on 2026-10-07 at 19:26 and checked the same day, on this machine and in Windows Sandbox |
| .NET | .NET 10.0.12, in the file. On a Windows without any .NET, in Windows Sandbox, `yav doctor` names it as the runtime that runs YAV: ".NET 10.0.12 in C:\yav-check (part of this installation)", where `C:\yav-check` is the directory `yav.exe` was started from there |
| `yav install`, `yav uninstall` and the offer at start | **part of the program, run and tested**: see [Installing](#installing-one-yavexe-that-installs-itself). No administrator rights are needed, and none are asked for |
| A Start menu entry | **none is made**, and one that 0.1.1 made is left as it is. YAV Shell is started by typing `yav` in a console; [decisions.md](decisions.md) says why |
| Code signing | **nothing is signed.** Windows may warn when a downloaded `yav.exe` is started for the first time; that was not tried, because a file that was built on the machine is not marked as downloaded |
| 0.1.1, as history | a folder, `dist\yav-shell-0.1.1-win-x64`, and its portable zip, with a manifest of every file and its SHA-256; the scripts `install.ps1` and `uninstall.ps1`, which were run and tested; and `installer\yav-shell.iss`, a script for Inno Setup that was never compiled, because Inno Setup is not installed on this machine. None of them is part of 0.2.0: the program installs itself |

For 0.2.0, `scripts\clean-machine-check.ps1`, which `scripts\verify-package.ps1 -Sandbox` runs in
Windows Sandbox, makes the checks of the environment without .NET there as well, and then installs
with the options a user gets by default (`yav install`). It finds the directory on the PATH of the
user once and the entry under "Installed apps"; starts `cmd.exe` with the PATH that Windows puts
together from that of the machine and that of the user, where `yav --version` is found by its name;
and removes everything with `yav uninstall`, after which the directory, the PATH entry and the entry
under "Installed apps" are gone. It does not open a console the way a person does, from Explorer or
the Start menu: the PATH it gives `cmd.exe` is read from the registry, so it shows that the PATH is
right, not that Windows hands it to a new console. `yav` is started hidden there and with its input
redirected, so the offer at start and a window that waits for Enter are not exercised.

For 0.1.1, the check on a new Windows installed with `-AddToPath -Shortcut`, found the PATH entry of
the user, the Start menu entry and the entry under "Installed apps", started `yav` by its name with
that PATH, and removed everything again. It did not open a second console after the installation to
see whether Windows hands the new PATH to it.

## Not verified

What no version has verified so far. What 0.2.0 adds to this list is under
[Not verified for 0.2.0](#not-verified-for-020).

* **Much of what was changed after the first run, with real models.** The second run went through
  the account and the directory each conversation reports, the effort and the boundary of the
  review, and the section that tells Model A which checks YAV runs. No agent asked a question in it,
  so the new way of answering one did not come up with a real model; neither did most of the
  hardening of the adapter for Codex, which is about what goes wrong. They were tested against the
  stand-in and, where that was possible without a model, against the installed agents.
* **Work with real models beyond two runs of one small task.** With real models there was no repair
  cycle, no review that asked for changes, no interruption, no steering, no approval that was
  granted, no `/undo`, no follow-up, no attachment, no request that waited in the queue, no limit
  that was reached and no faster tier. All of these are verified against the stand-in only.
* **That a program from the Store which an agent starts leaves YAV's job for every way of starting
  it.** A test shows it for one that Windows PowerShell starts with `Start-Process`; the investigation
  that preceded the fix saw it for one that `cmd` starts. Agents start programs in their own ways.
* **Other pairs of models.** Both runs had Codex as Model A and Claude Code as Model B. The roles
  the other way round, two models of one provider, and `codex exec` were not run with real models.
* **`yav run` without a person, beyond one small task.** It went through once, in the second run,
  where Model A had been told which checks YAV runs itself. Whether models ask for access anyway,
  for other tasks and in other projects, is not known; where one does, the run ends as Approval
  Required, as it must.
* **What the providers' terms allow for your accounts.** YAV shows the account route and asks you to
  acknowledge it. It cannot know whether a subscription covers use through a program like YAV.
* **Windows 10**, and Windows on ARM. Everything was done on Windows 11 x64, build 26200.
* **Windows Terminal as an application**, as described above.
* **A signed package**, as described above.
* **Large projects.** The projects of the tests have a few files. How long preparing, freezing and
  applying take for a repository with tens of thousands of files was not measured.
* **Long runs.** No test runs for more than a few minutes.
* **Git LFS and submodules in use.** That a repository has them is detected and leads to a question;
  a run in such a repository with the content present was not made.
* **An upgrade** from one version of YAV to the next. A run that 0.1.0 had begun was continued by
  0.1.1 with the same data directory; the two do not differ in what they store. Migrations of the
  database are tested from an empty database. What 0.2.0 stores that is new has forms that existed
  before: that a project may be accepted on the review alone is a row of the table that holds
  acknowledgements, and the answer to the offer to install is a setting.
  No check started 0.2.0 on a data directory of 0.1.1: the tests and the checks of the package each
  use a new one, and no test opens a database that 0.1.1 wrote. The schema of the database is that of
  0.1.1 (version 1, no new migration), and so is the format of the settings (version 1).

## What the checks found

Defects that were found by the checks of this document, late, and corrected. They are listed because
they show what kind of defect the checks notice, and what kind may still be there. The first five
rows were found while 0.2.0 was made, by running what was built; the others while 0.1.0 and 0.1.1
were made, and the installer scripts that some of those name are not part of 0.2.0.

| Found by | Defect |
|---|---|
| Running the program that is one file (0.2.0) | A `yav.exe` that is one file crashes at its next assembly load when it is moved while it runs, so the removal cannot move the program that removes itself out of the way. It leaves it in place and has it deleted after it has ended |
| Running the command line that deletes the program, with probe scripts and then in tests with the real `cmd.exe` (0.2.0) | CMD skips everything after an `IF (...)` that is false on the same line, so each `IF` stands in parentheses of its own now. FINDSTR, given a path with letters outside the code page, such as `C:\Users\李明\...`, does not open the file, so a program installed there would never have been deleted; it reads the list from its input now, which CMD opens |
| Trying how Windows counts the processes of a console (0.2.0) | A program started without a window (`CREATE_NO_WINDOW`) is alone in its console, as one started from Explorer is. One process alone would have been taken for a double-click; a console window is required as well |
| Preparing the workflows for GitHub (0.2.0) | Where the variable `GITHUB_ACTIONS` is set, as in every step on GitHub, Spectre.Console switched ANSI output on for a pipe, over YAV's choice of plain text. YAV turns these adjustments off now, and a test starts `yav.exe` with the variable set |
| A test of the shell (0.2.0) | A request whose answer changes no file was left in the state Checking, with an error, in 0.1.1. It ends as Completed now, and a test of the run pins it |
| A test in a real console | Enter was taken by the list of completions, so a command typed in full was not sent |
| A test in a real console | After the first line was sent, Ctrl+C was a signal again and a run could not be stopped |
| A test in a real console | A completion for a path crashed the program when the caret was outside the part to replace |
| A test in a real console | Esc does not empty the line in the editor that is used while no run is active. The help text and the guide said it does; they were corrected |
| A test of rendering | A long line of an agent, broken by the terminal, could begin a row with `[APPROVAL]` |
| The check of the package | `uninstall.ps1` could not be read by Windows PowerShell at all |
| Tests of the installer scripts | The scripts used a command that Windows PowerShell does not find when it was started by a program that was itself started from PowerShell 7. Installing failed there |
| A test written for `/replace` | In a project that is a directory of a larger repository, a local edit changed the file of the same name in the root of the repository |
| Measuring a run | The time before a run starts was measured and then not kept, so `/latency` left out the largest item |
| Running the benchmark | The benchmark tool looked for programs relative to the directory of a run and not to where it was started |
| Comparing the guide with the specification | The specification asks for local edits; the shell had no command for one. `/replace` was added |
| Going through the specification line by line | The time agents spend in their tools had a name among the stages and was never measured. It is measured now |
| Going through the specification line by line | `yav doctor` said that YAV searches in files by itself. It does not, and the line was removed |
| The mutation check | A line in the installer's functions could be removed without any test noticing: it did nothing. It was removed |
| The mutation check | Where Claude Code ended after it had said what it works with, and before it was given the prompt, a second one was started and given the prompt. The correction for a Claude Code that ends *while* it is asked did not cover it, and no test had looked. Nothing is sent now, and a test shows it |
| The mutation check | `/shell` asked only whether somebody can answer a question, not whether there is a console to give to the shell. In the shell of the tests, a terminal kept in memory, a mutation that let a command through made a real PowerShell start; it waited for keys of a console nobody sees and held the mutation run open for 40 minutes. `/shell` now needs a real console, which changes nothing where YAV runs in one, and a program that a test starts gets no keyboard |
| The mutation check | The test that YAV takes no screen of its own, and leaves the scrollback and the mouse to the console, looked only at what came after YAV's first line, because the pseudo console empties its screen by itself before that. A switch to another screen written before the first line was not seen. The test looks at everything now, except for that one sequence of the pseudo console |
| The complete test run | A test in which the stand-in closes its input failed once in three complete runs. What YAV wrote to it afterwards did not fail, because a program that another test had started at that moment had been given the end of that pipe: Windows gives a program that is started with inherited handles every handle that is inheritable at that moment. The test runs while no other test runs now. In YAV itself every program is started with a list of the handles it may have, except those of `/shell`, `/exec` and `/login`, and those three wait while a run is active |
| The complete test run | A test that counts how many questions to Git are open at the same time failed once while the machine was short of memory. YAV starts the processes one after the other and then waits for them together; where starting took long, the first had ended before the last began. The test now holds every answer back until no question has been asked for a second, which shows the same on any machine and still fails where the questions are asked one after the other |
| The complete test run | A test in which a turn ignores the interrupt, so that YAV ends the agent's process after the grace period, failed once. YAV closes the input of the process first, and the stand-in reported the turn as interrupted before it was gone; YAV then reported the turn as the agent had, as if it had stopped when asked. Once YAV has begun to end the process, the turn ends with it now, whatever the agent says meanwhile, and a test makes the stand-in report first every time |
| Cleaning up after the checks | The temporary directory of a test followed junctions when it made files writable to remove them. The directory of the test for junctions, which holds one that leads back into it, was left behind by every run, and where a junction led out of such a directory, files there would have been changed. Links are removed as links now, and two tests show it |
| The complete test run | Two tests looked at what the agent had received before the agent could have received it. They passed when run alone and failed once under load; they now wait for what they depend on |
| The complete test run | Two tests expected a span of time to be at least as long as a delay of 400 ms. It was 399.86 ms once: a delay can end a fraction early by another clock |
| **The first run with real models** | **Claude Code does not say in the message at the start of a turn which effort is in effect**, although the published types have a field for it. YAV read the field, found nothing, and under strict policy refused to start the review. The effort is now read from the answer to `get_settings`, before every prompt |
| **The first run with real models** | **With a schema for the result, Claude Code gives a conversation one tool more, `StructuredOutput`.** YAV did not know it, so it could not confirm that the reviewer has no tool that changes anything, and refused to start the review. It knows it now; any other tool it does not know still leaves the boundary unconfirmed |
| The first run with real models | The stand-in for Claude Code said what YAV expected to hear and not what Claude Code says. That is why 1636 tests had passed. It was corrected first, so that the tests failed the way the run had failed, and the adapter after that |
| The first run with real models | A tool of Claude Code was shown twice, the second time without what it was given; the tool a result is handed back with was shown as if it were work; paths were shown in full length. A tool has one line now, and a path inside the workspace is shown from there |
| The first run with real models | The record of a continuation was written over by the next one. The first one was saved by hand in time; every continuation has a file of its own now |
| Preparing the first run | A program that is started from a Claude Code session is given variables of that session, and would hand them on to the Claude Code it starts. `scripts\live-run.ps1` takes them out, so that the agents are started the way a person at a terminal starts them |

## What the reviews found

After the first run with real models, the code was read by reviewers that had not written it: thirteen
reviews, each of one area, each told to look for defects and not for style. What they reported was
checked before anything was changed. Every change came with tests, and the mutation check shows that
those tests fail when the change is undone. None of the defects below had been noticed by the tests
before, which is why they are listed. The second run with real models went through some of the
changes, the account and the directory of a conversation among them (see above); for the others,
the installed agents were asked what they could be asked without a model. These reviews were made
of 0.1.1.

**What 0.2.0 changed was reviewed in the same way**, by reviewers that had not written it. The units
of the work were reviewed before they were brought together: of 41 findings, 30 were confirmed, 20 in
the guided first run, several of them reported twice, and 10 in the installer. The source was
reviewed again after it was brought together, and 19 findings were confirmed. The design of CI/CD
and of the release was reviewed adversarially in three rounds: of 10 findings, 9 were confirmed. The
confirmed findings were corrected, apart from one note of the guided first run, which still says
that a request needs a decision of yours before it is known whether a question follows. The
corrections of each group were checked by a second agent before they were brought together, and the
mutation check got entries for them. Two corrections have no test, because a test cannot bring about
what they are for: a window of `yav uninstall` that is closed with its X button, and the list of an
installation flushed to the disk before it takes its name, for a loss of power. What was found, in
groups:

| Area | What was wrong | What it is now |
|---|---|---|
| **The guided first run: the project** | The answer to the question for the folder was opened whatever it was, a drive, the profile or Downloads among them, and the request was sent at once. What was attached before was dropped, and the request went on as a follow-up of the folder's last task | Such a folder is refused and asked for again; `/open` still selects it on purpose. The request keeps what was attached and is a task of its own |
| The guided first run: the questions | Model A could be given Model B's model under Quality Lock; an agent that was not found was asked about as if a model had to be chosen again; "Sending the request again" was said when nothing had been settled; the banner promised questions where nobody can be asked; a failure while an answer was recorded ended YAV | Model A has to be another model than Model B; an agent that is not found is left to its notes; a request is sent again only when something was settled; the banner promises questions only where somebody can answer; a failure is said, and the shell goes on |
| The guided first run: its tests | Tests that could not fail: one looked for a row that had been written before the command it was about, one did not look at what the request sent again did, and none showed that asking stops after three answers | Each looks at what it is about; three answers that are not on the list choose nothing and send nothing |
| **The installer** | An empty directory that another program uses ended a removal half-way; the root of a drive, a path of 260 characters or more and a directory with a `;` for the PATH were not refused before anything was written; the PATH was read once, seconds before it was written; the list of the installation was not flushed to the disk; a running YAV reached through a junction was not found; an installation of 0.1.1 was refused | The removal goes on and keeps the directory; the three are refused first; the PATH is read again right before it is written; the list is flushed before it takes its name; a program is compared by the path Windows resolves for it; 0.1.1 is updated and removed |
| The installer: its tests | Tests could have reached the installation of the user who runs them; two tests of short names guarded themselves against the wrong path | The stand-in for Windows stops anything outside the tests' own directory before it is written; the guards compare with the long name |
| **The source brought together** | `--remove-data` took any folder that held `yav.db` or `settings.json` for a data directory of YAV; what deletes the program after it has ended deleted by name after a fixed wait, the files of a new installation made meanwhile as well, and the list even when the program was still there; a window of a removal closed with its X button left the program behind; a window started hidden counted as one somebody sees; `--dir` took the next option for its value | A data directory is removed only when it holds nothing YAV did not create and no other YAV has its database open; the deletion is tied to its removal by a text in the list and removes the list only when the program is gone; closing the window hands the deletion over; a hidden window counts as none; `--dir` needs a value |
| The source brought together: its texts | A removal of the program that runs said "holds other files and was kept"; the end of a removal advised `yav uninstall --remove-data` after the program was gone; documents said that approving any check ends review-only acceptance, and that removing is refused while YAV runs from the folder | The program and its folder are said to be deleted as soon as it has ended; the advice names what works; the documents say an approved required check, and another YAV process |
| **CI/CD and the release** | A check of the attestation with `--repo` alone accepted any workflow of the repository; the mutation workflow misread a filter that begins with `-`; the release notes claimed results for the released file that were made with a local build | The check names the release workflow and the tag; the filter is passed together with its parameter, as one argument; the notes say which file the checks were made with, and the release adds what was run on the file it publishes |

What the reviews of 0.1.1 found:

| Area | What was wrong | What it is now |
|---|---|---|
| **Answering a question of an agent** | One key answered. A word typed for something else answered with its letters: the `s` of "yes" or of "/stop" allowed a command **for the whole conversation**, "abort" allowed it once | An answer is a letter and Enter. Requests the agent marks, requests whose text had characters that had to be written out, and requests too long to show whole need the word `allow`, and are never allowed for a conversation. Answers begun too early, typed ahead or pasted grant nothing |
| What a question shows | Escape sequences were removed together with what they carry. `git status ESC]x; curl … \| sh; BEL` was shown as `git status`; the agent would have run all of it | What an agent asks for and runs is written out, character by character; nothing is removed from it |
| What a question shows | For Claude Code, a question about a tool showed one field of what the tool is given. What a tool sends away can be in any field | Every field, and every line of a text, is listed; what does not fit is counted and needs `allow` |
| What a question shows | 25 to 400 lines of what a request is about were counted, not shown, and one key allowed them | What is not shown needs `allow` |
| The console without control over the terminal | The shell and the question read the same input, so an answer could become a request for Model A; two questions could be open at once; with the output redirected, a question was asked that nobody could see | One reader serves the question first; one question at a time; where nobody can see a question, nobody is asked |
| The account | A conversation's account was never looked at: the route shown before a run was read by another process, started in another way. "No key is named" was recorded as a confirmed subscription | The conversation is asked (`initialize`), another kind of route stops the run under every policy, and "no key is named" is shown as not confirmed |
| The working directory | Nothing compared the directory an agent reports with the workspace | It is compared; another directory stops the run. Claude Code reports the directory with every junction resolved, which the comparison resolves as well; without that, every run in a workspace below a junction would have been stopped |
| A Claude Code that ends early | When it ended while it was asked what it works with, a second one was started and given the prompt without anybody having compared what it works with | Nothing is sent; the run says why Claude Code ended |
| Answers that did not arrive | An answer was recorded as given before it was known whether it reached the agent | It is recorded as `NotDelivered` when it did not |
| A request asked again under the same name | An answer meant for a request the agent had taken back could be given to the next one of the same name | Only the request that was asked gets the answer |
| Repairs | A repair quotes what the reviewer found and the checks wrote. Claude Code took `@path` in it for a file to attach | It is marked as composed, as a review is; the user's own request is not |
| Programs from the Microsoft Store | YAV could not start them at all: PowerShell 7 on the machine this was built on is one | They are started, and are in YAV's job before they run. What an agent starts from the Store leaves the job; that is a limit of Windows, stated in [security-boundaries.md](security-boundaries.md) and said by `yav doctor` |
| Codex: who decides about access | Notifications that say Codex's own reviewer takes part were ignored, and an answer that did not say who decides was taken for "you" | Such a conversation is refused |
| Codex: what its sandbox reaches | A sandbox policy in a form YAV could not read was taken for one that reaches nothing | Such a conversation is refused |
| Codex: reading its messages | An error while one message was handled ended the reading, and the run then waited without a limit | The message is reported, and reading goes on |
| Codex: warnings | Warnings Codex gives before any conversation exists were lost, and so was the one that says its Windows sandbox cannot protect some folders | They are kept and shown in every later conversation |
| Codex: your own instructions | When your Codex configuration could not be read, your developer instructions were left out without a word, and for every later conversation | It is said, and the next conversation reads them again |
| `codex exec` | It reported the directory YAV had asked it to work in as if the agent had said it | Nothing it did not say is filled in |
| `yav doctor` | It would have reported the stand-ins Windows puts where Python is missing as programs of the Store | They are recognized and left out |
| Tested versions | Claude Code was called tested for every 2.1 release from 2.1.259 on | Only the releases that were tried: 2.1.284, and 2.1.285 since the second run with real models |
| `scripts\live-run.ps1` | It acknowledged whatever account route the provider reported for a provider you named; a run that failed ended the script with success; stopped or killed, it left `yav.exe` running; the variables of a hosting Claude Code session were set to empty instead of being removed; every part after setup failed at once when it was really run | Each is corrected and tested against a stand-in program |
| The mutation check | A build that failed counted as a mutation that the tests noticed; an entry whose text was gone stopped a run in the middle | A mutation is noticed only when tests ran and failed; every entry is checked before the first one is made |

## Asked for by the specification and not in this version

| | |
|---|---|
| A search of YAV's own in the files of a project, with ripgrep where it is installed | not implemented |
| Caches that are kept between tasks: an index of files, fingerprints, symbols; watching files for changes | not implemented |
| Checks that are aimed at what failed, during a repair | every cycle runs all required checks |
| Formatters and other rewriting tools as local edits | `/replace` makes literal replacements only |
| Estimates of prices | charges are shown when an agent reports them |
| A package that is signed | not signed, see above |
| A shortcut, which the specification names as optional next to the PATH entry | not made: YAV is started by typing `yav` in a console |

[decisions.md](decisions.md) gives the reason for each.

## How it was built

* Tests were written before the code they test, and were seen to fail first, with these exceptions,
  where the code was written together with its tests or before them: the scope of stored credentials,
  the indentation of continued rows, the fallback when the line editor fails, the statistics of the
  benchmark, parts of the shell's commands, and the last corrections after the reviews (the tests
  were written first, but not all were run before the correction was made). For these, the mutation
  check is the proof that the tests notice a defect.
* After the first run with real models the work was done by several agents side by side, each in a
  copy of the source of its own and on files of its own, and brought together afterwards; every one
  of them followed the rules above, and none of them asked a model anything.
* 0.2.0 was made toward a goal you set: "make the app to easily installed (run yav.exe), and easy to
  run the app (type yav in powershell), and to run a task, just type the prompt". It was made by
  agents side by side again, each in a worktree of its own and on files of its own: the installer,
  its commands and the offer at start, the guided first run, review-only acceptance, the packaging
  scripts and the documentation. The work was brought together afterwards. The design was written
  in the conversation and not put before you for approval, as the goal asked; the decisions are in
  [decisions.md](decisions.md).
  As for 0.1.1, none of them asked a model anything: every test used the scripted stand-in, and the
  installed agents were asked for nothing but their version (`codex --version`, `claude --version`).
* The design was not put before you in steps for approval, as the process I work by would have asked
  for. The specification asks for the implementation to begin at once and for decisions to be
  recorded; they are in [decisions.md](decisions.md), with the ones that are yours to review first.
* The project was put under version control on 2026-09-30, when you asked for it, with no remote.
  Later changes are committed when you ask.
  Version 0.2.0 was committed on 2026-10-07 and published, as you asked on that day, in the public
  repository [Yavinesh2025/yav-shell](https://github.com/Yavinesh2025/yav-shell), whose release
  workflow builds the `yav.exe` of each release from its tag.
* The .NET SDK 10.0.401 was installed for the current user
  (`%LOCALAPPDATA%\Microsoft\dotnet`), because the machine had runtimes and no SDK.

## Known limits of this version

* **A console that was open before the installation does not find `yav`.** Windows gives a program
  the environment of the moment it was started, and the PATH is part of it. Consoles that are opened
  after the installation find it. What `yav install` writes when it is done says how to make `yav`
  work in a console that was open already.
* **`yav.exe` is not signed**, as said above.
* **The single file unpacks the native library of SQLite** into a directory of your user account the
  first time it starts: `%TEMP%\.net\yav\<id>\`, one for each build, which `yav uninstall` does not
  remove ([performance.md](performance.md) says how it was seen).
* **There is no Start menu entry.** YAV Shell is started by typing `yav` in a console. One that 0.1.1
  made with `-Shortcut` is left as it is by 0.2.0, also when 0.2.0 removes that installation; delete it
  yourself.
* Lines that are wider than the window are broken by YAV. They do not flow anew when the window is
  made wider or narrower afterwards.
* In the line editor that is used while no run is active, Esc closes the list of completions and
  does not empty the line; Ctrl+C does.
* `/replace` replaces a text within one line. A text of several lines cannot be written on the line
  of a command.
* What YAV does around the agents takes about two seconds for a task, measured with 0.1.1 and agents
  that answer at once; see [performance.md](performance.md). Most of it is the starting of `git`.
* Usage that a provider reports late, after a turn has ended, can exceed a limit that was set.
* **An agent cannot always run the project's tests inside its sandbox.** Codex on Windows could not
  start a Python that is installed for the user, in both runs with real models. In the first run it
  asked to run them outside its sandbox, which ended `yav run` as Approval Required. Model A is now
  told which checks YAV runs itself and not to ask for wider access only to run them; in the second
  run it asked for nothing and said that it could not run the tests. An agent that asks anyway is
  asked about in the shell, where you decide, and ends `yav run` as Approval Required, because
  nobody is there to ask. Declining costs nothing but the agent's own look at the result: YAV runs
  the required checks itself, on the frozen candidate, in either case.
* A version of Claude Code that does not answer `get_settings` leaves the effort Requested /
  Unverified, which stops a run under strict policy. 2.1.284 and 2.1.285 answer it; which earlier
  versions do was not looked up. Only those two are called tested; every other version gets a
  warning.
* **A program from the Microsoft Store that an agent or a check starts is not ended with the run.**
  Windows takes it out of YAV's job. `yav doctor` says which of `pwsh`, `python` and `python3` come
  from the Store on the machine; on the machine this was built on, `pwsh` does.
* A workspace reached through a drive made with `subst` is taken for another directory than the one
  an agent reports, and the run stops. Junctions and symbolic links are resolved.
* An answer to an agent's question is a letter and Enter. The one extra key is what makes an answer
  something you meant: a single key that happened to be typed allowed things before.
* A file with a path of more than 260 characters is isolated, checked, applied and put back; YAV
  tells Git to allow such paths, whatever the repository says. That was tested on a Windows that has
  long paths turned on. Where they are turned off, the programs your checks start may fail on such
  a path, and a project whose own directory is that deep was not tried.
