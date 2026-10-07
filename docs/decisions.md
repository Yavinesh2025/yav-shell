# Decisions

Engineering decisions that were made while YAV Shell was built, each with its reason. None of them
was needed to be asked first; every one of them can be changed. Decisions marked **yours to review**
set a default for behavior that the specification left open.

## Defaults that are yours to review

| Decision | Reason | Where to change it |
|---|---|---|
| A required check that already failed before the task is **your decision**, not a repair for Model A | Sending it to Model A would make a task repair things it was never asked to touch, and spend usage on it. The failure is shown together with the fact that it predates the task | `/quality preexisting repair` |
| An effort value YAV cannot rank (`ultra`) **always blocks** until you choose the exact value, under every policy | Codex describes `ultra` as maximum reasoning *with automatic task delegation*. Whether that is "the maximum" you meant is a decision, not a lookup. Since 0.2.0 the shell asks for the value when a request needs it | `/effort a\|b <value>` |
| Two repair cycles | The specification's default | `/limits repairs <n>` |
| In Adaptive mode the run is marked Adaptive even for a task for which no lower effort was approved | It can never be called Strict Max by mistake. The effort that was used is recorded per run | `/adaptive off` |
| A subscription route needs your acknowledgement once per route | The providers' terms differ for third-party clients and change. YAV states what it found and leaves the decision with the account holder. Since 0.2.0 the shell asks for it when a request needs it | `/login <provider>` |
| Input from a pipe is read line by line, each line after the run before it ended; the end of the input lets a run finish | Nobody is typing while a run works, so queuing and stopping, which are right at a keyboard, would make a script unusable | - |
| `yav run` never grants an approval | Nobody is there to ask. The run ends as Approval Required (exit code 3) | - |
| Model A **is told** which required checks YAV runs itself, and not to ask for access beyond its sandbox only to run tests | In the first run with real models, Model A asked for access outside its sandbox only to run the tests, which YAV runs anyway, and `yav run` ended as Approval Required. You decided on 2026-09-30 that it should be told. Its task, a repair and a continuation name the checks; where it cannot run tests inside its sandbox, it is asked to say so in its report. A project without required checks gets no such section. In the second run with real models Model A asked for nothing, said that it could not run the tests, and `yav run` went through | `src/Yav.Core/Templates/RuntimeTemplates.cs` (`AppendChecks`) |
| A program that drives the shell **declines** what an agent asks for | `scripts\live-run.ps1` continues a run in the shell without a person. A program that presses "allow" would be a way around the rule that only a person grants access | answer at the keyboard: start `yav` yourself and `/resume` |
| **An approval is answered with a letter and Enter**, not with a single key; some requests need the word `allow` | With one key, a word you were typing for something else answered the question: the `s` of "yes" or of "/stop" allowed a command for the whole conversation. A review of the code found it. Enter costs one key and makes an answer something you meant | - |
| **Another billing route than the one that was shown stops a run under every policy**, also with Quality Lock off | What is paid, and to whom, is not a matter of quality. You agreed to the route that was shown and to no other. Before this version a different route stopped a run only while Quality Lock was on | sign in so that the agent works with the route you want, then `/login <provider>` |
| **An agent that reports another working directory than the workspace stops a run under every policy** | The candidate is what is in the workspace, and the sandbox of an agent is drawn around the directory it works in. An agent that works elsewhere changes what nobody checks | - |
| **That no key is named is shown as Requested / Unverified**, not as Verified | A subscription, a token and a cloud provider all name no key. Only what a conversation says about its account confirms a route. It stops nothing by itself | - |
| Folders outside the workspace that an agent's own sandbox lets it write **stop a run while Quality Lock is on**; access to the network is **reported and stops nothing** | YAV asks for a sandbox that is limited to the workspace, so more than that is not what was asked for. It asks for no network and forbids none: many checks need it, and Codex leaves it to your configuration. Both are said in a warning, not only in a table | your agent's own configuration; `/quality lock off` |
| Claude Code is called **tested for the releases that were tried, 2.1.284 and 2.1.285**, and Codex for the series **0.158 and 0.159**; every other version gets a warning | Claude Code changes what it says from one release to the next, and both agents update themselves. A warning on most days is the price of not calling tested what was not tried. You had the two newer ones added on 2026-09-30, after the second run with real models had used both | `TestedVersions` in `src/Yav.Adapters/Claude/ClaudeCliAdapter.cs` and `src/Yav.Adapters/Codex/CodexAppServerAdapter.cs` |
| What a packaged program (Microsoft Store) starts **runs with the identity of that package** when YAV starts the program | It is what keeps those programs in the group YAV ends with a run. Left to the default of Windows, everything such a program starts leaves the group and outlives a stop | start a version of the program that is not from the Store |
| A local edit has a command of its own, `/replace`, which the specification does not list | The specification asks for explicit literal replacements to be made locally, but names no way to ask for one. Guessing it from the wording of a request would be what the specification forbids | - |
| `/replace` changes a text only when it occurs as often as you said, once by default | "Unambiguous" has to be decided by something that can be checked. Anything else is a task for Model A | `--count <n>`, `--all` |
| **`yav install` adds its folder to the PATH of your account** | You set the goal that `yav` works when you type it in PowerShell. A program that is not on PATH has to be started with its full path. Until 0.1.1 the entry was made only when it was asked for | `yav install --no-path`; `yav uninstall` removes the entry |
| **Enter installs** when a `yav.exe` that is not installed starts without arguments in a console | Running `yav.exe` is how you said it should be installed. It changes only your own account, and `yav uninstall` takes it back. A "no" given in a console you had already open is remembered, so that you are not asked at every start of a copy you use where it is | `installOfferDeclined` in `settings.json`; `yav install` |
| **No Start menu entry** | Typing `yav` is the way in. An entry would start YAV in your profile, which is no project, and making one needs code for Windows shortcuts that nothing else uses. "Installed apps" lists YAV Shell, so it can be removed from there | - |
| The questions of the first request show **one interface per provider**: the Codex app server when it is usable, otherwise `codex exec`, and Claude Code | Only the app server can ask you for approval and list models. Each Codex model listed twice, once per interface, made the list longer without adding a choice | `/models a\|b <adapter> <model>` |
| **Review-only acceptance is offered for a project** that has no approved check that is required: YAV has no check to propose for it, you declined what it proposed, or what you approved requires none | Without it such a project cannot run while checks are required. The other way, `/quality gates optional`, lets every project run without checks, also those that have some | `/quality gates required` withdraws it for the selected project; while an approved required check exists it is set aside, and it applies again when none is required |

## Installing and starting: version 0.2.0

On 2026-10-07 you set the goal of this version: "make the app to easily installed (run yav.exe), and
easy to run the app (type yav in powershell), and to run a task, just type the prompt". The decisions
in this section and the next serve it.

* **The package is one file, `yav.exe`, and it installs itself.** It holds the .NET runtime and every
  library, and as resources the license, the notices of the components inside it, the documentation
  and the examples, which the installation writes next to it. It replaces the folder of more than 220
  files, the scripts `install.ps1` and `uninstall.ps1`, and the Inno Setup script, which was never
  compiled. A downloaded folder has to be unpacked before anything in it runs, and a PowerShell script
  from a download runs only where the execution policy lets it: the default of Windows PowerShell on a
  Windows client, `Restricted`, runs no script at all. That is why the "Installed apps" entry of 0.1.1
  and the clean-machine check started PowerShell with `-ExecutionPolicy Bypass`.
* **The single file costs little, and most of it at the first start.** Measured with 0.1.0
  (`performance.md`): 125 ms instead of 66 ms for the first `yav --version`, because the native SQLite
  library is unpacked into a directory of your account then, and 61 ms instead of 58 ms after that.
  The folder had been chosen because it writes nothing when it starts; one file that can be run,
  copied and sent is what the goal asks for.
* **Everything is for the current user**: no administrator rights, nothing outside your account. The
  program goes to `%LOCALAPPDATA%\Programs\YavShell` unless you name another directory, and the PATH
  entry and the entry under "Installed apps" are values in your part of the registry.
  `security-boundaries.md` lists what is changed and what is never touched.
* **The PATH is changed as text.** The folder is added at the end, unless a part of the PATH names it
  already; nothing else in the text is changed, its `%VARIABLES%` and the kind of the value included.
  Windows is told that the environment changed, so that consoles opened from then on find `yav`. A
  console that was open already keeps the PATH it started with; when you install from one, YAV says
  how to add the folder in it.
* **The files are written as bytes, not copied as files.** A copied file takes along the mark Windows
  gives a download (its `Zone.Identifier` stream), and with it the questions Windows asks before it
  starts a downloaded program. You decided about the download when you ran it; the installed copy is
  what an installer writes, without the mark.
* **`yav-install.json` lists every installed file with its size and SHA-256.** A later version replaces
  those files and removes the ones it no longer has; removing YAV Shell removes exactly them. A file
  you put into the folder stays, and the folder stays with it. A folder that exists, holds something
  and was not made by the installation is not used. The list is written before the first file, naming
  every file that is about to be there, so that an installation that is interrupted can still be
  completed or removed; what an interrupted write leaves (`<file>.partial-<8 hex digits>`) is deleted
  by the next installation or removal.
* **An installation of 0.1.1 is taken over.** Its `install.ps1` used the same folder and the same entry
  under "Installed apps", and listed its files in `package-manifest.json`. 0.2.0 reads that list as the
  one of an earlier installation, so `yav install` updates it and `yav uninstall` removes it, instead of
  refusing a folder it did not make. A Start menu entry that 0.1.1 made on request is left alone: 0.2.0
  has no code for shortcuts.
* **What cannot work is refused before anything is written**: the root of a drive, which is no folder
  of its own; a folder in which the path of `yav.exe` would have 260 characters or more, because Windows
  starts no program from such a path, whether long paths are turned on or not; and, with the PATH, a
  folder whose name holds `;`, which the PATH would read as two folders, neither of them this one.
* **The running program stays where it is and is deleted after it has ended.** "Installed apps" removes
  YAV Shell by starting the installed `yav.exe`, and Windows does not delete the file of a program that
  runs. A running single-file program cannot be moved aside either: it reads parts of itself from its
  file while it runs, and after a move it failed at the next of them (measured). `yav uninstall`
  therefore removes everything else first; `yav-install.json` stays beside the program and names only
  it, so that the folder is still known as an installation if the last step does not happen. As the
  very last thing it does - after "Press Enter" in a window of its own, or when that window is closed
  with its X button - it starts a hidden command that deletes `yav.exe` once the program has ended, then
  that list only when the program is gone, and then the folder when nothing else is in it. It deletes
  only what this removal left: the list holds a random text of this removal, the command deletes nothing
  once the list no longer holds it, and a newer installation made into the same folder in the meantime
  writes a list without it. The command is CMD, started from the system folder and working there, and
  the programs it uses to wait and to read the list (`PING`, `FINDSTR`) are named by their full paths,
  so that no program of the same name in the folder or in the PATH is started. That step is best effort:
  it waits a fixed time rather than for the process. A path that holds `%`, `!` or `"` is not given to
  it (CMD would interpret it); you are told to delete the folder yourself then.
* **Installing is refused while another YAV process runs from that folder**, and so is removing; the
  program that installs or removes itself does not count (`yav install` from the installed program
  repairs it, and "Installed apps" removes YAV Shell by starting it). The file of a running
  program cannot be replaced, and a newer version could change the database under the running one,
  which does not open a database of a newer version.
* **A build of the source tree does not install itself.** Its `yav.exe` needs the files next to it. It
  says so and names `scripts\package.ps1`, which builds `dist\yav.exe`. **It removes only an
  installation named with `--dir`**: the tests start such a build with `uninstall`, and without `--dir`
  it would fall back to the installation of your account.
* **`--remove-data` removes only a directory that is YAV's alone.** `YAV_HOME` can name any directory,
  so a directory is removed only when it holds `yav.db` and nothing at its top level that YAV does not
  create, is not the root of a drive or a folder of Windows or of your account, and is not in use by
  another YAV. That is checked before you are asked, so that a refusal asks nothing and removes
  nothing, and again just before the deletion; `yav.db` and `settings.json` go last, so that a removal
  that stops part-way leaves a directory that is still recognised. The API key goes on its own, so that
  a failure of one does not keep the other. Without `--remove-data`, the advice is to delete the folder:
  once the program is gone, there is no installation left that `yav uninstall --remove-data` could remove
  first.
* **The offer at the start.** `yav` started without arguments, from the single file, with a console for
  input and output, while YAV Shell is not installed for your account or its program is gone, asks
  first whether to install it: Enter or `y` installs, `n` starts YAV without installing it. In a
  window Windows opened for `yav.exe` alone, as it does for a double-click in Explorer, the window
  waits for Enter after the installation and then closes; there every start asks, and an installed
  older version is offered to be replaced. In a console you had open, the shell starts after the
  installation, and a "no" is remembered (`installOfferDeclined` in `settings.json`), so that a copy
  you use where it is does not ask at every start. `yav install` installs at any time. A console window
  that was started hidden counts as none: nobody could answer there. The offer is a convenience, so
  nothing that goes wrong in it keeps the shell from starting; what failed is said, with the step it
  failed in, and a "no" that could not be saved is said to be asked again next time.
* **The version is 0.2.0.** What was verified as 0.1.1 - the two runs with real models, the package and
  its SHA-256 - belongs to a program that is not the one built now. The records of 0.1.0 and 0.1.1
  stay what they are: history.

## The first request: version 0.2.0

* **What a run needs from you is asked when a request needs it, not before.** A shell that has its models,
  acknowledgements and checks starts and runs as before, and someone who set things up with the commands
  is asked nothing. When the preflight of a run refuses a request for reasons only you can settle, the
  shell asks about them there, records the answers as the commands do (`/models`, `/effort`, `/login`,
  `/test trust`, `/test detect`, `/open --accept-gaps`), and sends the same request again. The
  preflight stays the one place that decides what blocks a run, so the questions cannot disagree with
  it, and a request that can run is not delayed by them.
* **The models are still your explicit choice.** The shell lists what the agents list for your accounts,
  numbered, and you type a number. YAV does not rank them and proposes none; the provider's own default
  is marked, as what it is: information. Model B cannot be the model of Model A where strict Quality
  Lock would refuse that pair.
* **Every grant is a typed `yes`**, as with the commands: an account route, the checks of a project, the
  differences of the isolated workspace, review-only acceptance. A "no" ends the questions and says which
  command does it later. The questions come in a bounded number of rounds, because one answer can bring
  up the next question: an account route is known only once a model of its provider is chosen.
* **Nobody is asked where nobody can answer.** `yav run` and input from a pipe or a file behave as
  before: the run is blocked and says what is missing.
* **A request without a project asks for the folder of one**, which is then opened as `/open` opens it.
* **Review-only acceptance is per project**, not the global `/quality gates optional`. Turned off globally,
  required checks would be off for every later project too, also for those that have checks YAV could
  propose, and nobody would be asked. Accepted for one project, it applies only while that project has
  no approved required check; approving one makes checks required again, and the acceptance applies
  again only when none is required any more. An optional approved check does not set it aside, because a
  run does not run it. When the approved checks cannot be read, it does not apply: the run is blocked
  (`review-only-unknown`) rather than accepted on the review alone. It is kept as an acknowledgement, like
  the accepted differences of a workspace, a run in which it applies says so, and
  `/quality gates required` withdraws it for the selected project. A withdrawal is recorded with its time,
  so that `/apply` does not write a candidate that was accepted on the review alone in a run that started
  before the withdrawal; the run stays ready, and accepting the review alone once more makes it
  applicable again.

## Platform and structure

* **C# on .NET 10, Windows 11 x64 only.** One program built from modules (Console, Coordinator,
  Adapters, Workspace, Validation, Storage, Platform) that reference only the contracts in `Yav.Core`.
  The Coordinator does not know any adapter, the database or the console.
* **The .NET SDK was installed for the current user** (`%LOCALAPPDATA%\Microsoft\dotnet`, not on PATH),
  because the machine had runtimes but no SDK 10 and installing machine-wide needs administrator
  rights. `scripts\env.ps1` finds it.
* **Build output is kept out of Dropbox synchronization** (`artifacts\`, `dist\`) with Dropbox's own
  ignore attribute, because the project directory is synchronized and build output is large and
  reproducible.
* **Dependencies are pinned** centrally (`Directory.Packages.props`) and locked per project
  (`packages.lock.json`).
* **Warnings are errors.**

## Console

* **One owner of the terminal** (`Screen`). Output of a run, output of a command and the line being
  typed all go through it, under one lock.
* **Two editors.** PrettyPrompt while no run is active (history, completion, several lines); a small
  editor of YAV's own while a run is active, because PrettyPrompt cannot share the screen with output
  that arrives meanwhile. Both read from one reader of the keyboard. When PrettyPrompt fails, the small
  editor takes over and the shell goes on.
* **Rows are broken by YAV and not by the terminal.** A row the terminal begins starts in the first
  column with whatever text happens to be there, so a long line of an agent could make a row that reads
  `[APPROVAL] ...`. YAV breaks every line that is wider than the window and begins each further row
  with the mark of the line (`│`) or with its indentation. The cost: such lines do not reflow when the
  window is resized afterwards.
* **Only the sixteen named colors** of the terminal's palette, so the user's theme decides how they
  look. `NO_COLOR` and `--plain` turn them off.
* **Enter always sends; only Tab takes a completion.** PrettyPrompt's default lets Enter take the
  selected completion, which held back a command that had been typed in full.
* **Control+C is always a key**, never a signal: it empties the line, stops a run, or - twice on an
  empty line - leaves. PrettyPrompt's default turns it back into a signal after a line was sent.
* **Spectre.Console formats tables into text**; the text is then written by `Screen`. Spectre.Console
  never writes to the terminal itself, and it does not adjust that text to the build service it
  believes it runs on: whether the text has escape sequences is decided by YAV's options alone.

## Agents

* **The Codex app server is labelled Experimental** everywhere, because OpenAI documents it as not
  supported for production workloads. `codex exec` is offered as the stable, non-interactive path.
* **Claude Code is driven through its documented command line** (`claude -p` with `stream-json`), with
  the message shapes taken from the published type definitions of the Agent SDK.
* **What a provider did not report is never filled in.** Settings stay Requested / Unverified, numbers
  stay Unavailable.
* **A setting the provider put into effect differently stops the turn before it is sent**, where the
  provider reports settings when a conversation is opened (Codex). A configuration that is not honored
  then costs no usage.
* **Stopping**: ask the provider to interrupt, wait a bounded time measured in real time, then end the
  process tree YAV started. Nothing else is ever ended.
* **Paths an agent reports are shown as they are in the project.** A path outside the isolated
  workspace keeps its full form, so it cannot be mistaken for a file of the project.

## Runs and evidence

* **A candidate is content**, not a directory: a manifest of complete SHA-256 hashes plus the stored
  bytes. `/apply` writes the stored bytes.
* **Evidence is bound by YAV** to the candidate fingerprint, the number of requirements, the hash of
  the run profile and the fingerprint of the toolchain. Evidence with another binding is stale.
* **A request that is added to a running turn is a requirement.** It is recorded, the number of
  requirements grows, and a candidate is not frozen while a requirement is on its way to the agent.
* **Review and required checks run side by side** against the same frozen candidate. When one of them
  finds that source changed meanwhile, the other is cancelled and the candidate is put back.
* **What is only read is read side by side**: the project and the agents before a run, and the
  questions to Git about a repository. Measured, that halved the time before a run starts. Nothing
  that writes runs at the same time as anything else.
* **Every moment of a run belongs to a measured stage or is shown as belonging to none.** The time a
  candidate waits for your `/apply` is recorded as waiting, not as work.
* **A path you give to a local edit starts at the directory you opened**, which need not be the root of
  the repository, and it does not leave that directory.
* **Model A is the only AI writer.** What a reviewer changed is detected by fingerprint and reverted.

## What was left out, and why

The specification describes more ways to save time than this version has. They are left out where
their use could not be shown without a number of runs with real models, which were not authorized,
or where they stand against something that comes first. Each of them can be added without changing what is there.

* **No search of YAV's own in the files of a project, and no use of ripgrep.** What YAV offers to
  Model A as a starting point are the paths a request names, verified to exist. The agents search
  with their own tools. A search by YAV would be a guess about what a model needs.
* **No index of files, fingerprints or symbols that is kept between tasks, and no watching of files.**
  A task prepares its workspace anew; a follow-up keeps the workspace and the conversations of its
  task. Every file is hashed completely whenever a candidate is frozen or compared: a fingerprint
  that trusts size and date would miss a change that keeps both, and correctness comes first.
* **Every repair cycle runs all required checks**, not first the ones that failed. The result is the
  same; a project with slow checks waits longer.
* **Local edits are literal replacements.** Formatters and other rewriting tools are run as what they
  are, commands: `/exec` in the project, or a check of the project.
* **No estimate of prices.** Charges are shown when an agent reports them, and as what they are.

## Storage and secrets

* **SQLite** in the data directory, with numbered migrations. A database written by a newer version is
  not opened.
* **Settings are a JSON file**; a file that cannot be read is put aside, not overwritten.
* **Secrets are kept in the Windows Credential Manager** and scoped to the data directory: a data
  directory other than the default one (`YAV_HOME`) has secrets of its own and can neither read nor
  replace those of the default one.

## Packaging

* **One self-contained file, ReadyToRun**: `dist\yav.exe`, with its SHA-256 in `dist\yav.exe.sha256`.
  Until 0.1.1 the package was a folder; "Installing and starting" above says why it is one file now.
* **The installer is `yav.exe` itself**: `yav install`, `yav uninstall` and the offer at the start. The
  PowerShell scripts and the Inno Setup script of 0.1.x were removed.
* **Nothing is code-signed.** Signing needs a certificate that belongs to whoever publishes the program.
  Windows may therefore warn when a downloaded `yav.exe` is started for the first time.
* **YAV Shell is proprietary and free to use** (`LICENSE.txt`). Its owner decided on 2026-09-30 that
  it is proprietary (all rights reserved), and on 2026-10-07, before publishing it on GitHub, that other
  people may use it: anyone may download and use `yav.exe` free of charge, personally or at work; the
  source is published to be read and reviewed, and copying, modifying or redistributing it still needs
  written permission. The licenses of what is distributed with it are in `THIRD-PARTY-NOTICES.md`.

## Process

* **Tests use a scripted stand-in for the agents** (`tests/Yav.FakeAgent`) that speaks their wire
  formats. No test requests inference. Tests that talk to the installed agents are opt-in and perform
  handshakes only.
* **No live inference was run while building**, because live runs consume usage and need your
  authorization. After version 0.1.0 was delivered you authorized one run of one small task. It was
  made on 2026-09-29 and led to version 0.1.1. On 2026-09-30 you authorized one more, of the same
  task, with the corrected version; `verification.md` tells what both showed. Nothing else was sent
  to a model, and the benchmark with real models was not run.
* **The stand-in says what the real agents were seen to say.** Where it was written from published
  types and the installed program does something else, the program is right. What was learned in
  the run with real models was put into the stand-in first, so that the tests failed the way the
  run had failed, and into the adapter after that.
* **What a live check needs from a model, it gets without one where that is possible.** Claude Code
  names the tools of a conversation only after a prompt. The live test that reads them sets the
  address of the API to a port of this machine that nobody listens on.
* **The repository is under version control since 2026-09-30, as you asked:** Git, on the branch
  `main`. The first commit was made when you asked for it; later changes are committed when you ask.
  Build output, packages and the records of the runs with real models are not in it (`.gitignore`).
* **It is published on GitHub, as you asked on 2026-10-07:**
  [Yavinesh2025/yav-shell](https://github.com/Yavinesh2025/yav-shell), a public repository. Pushes go to
  `main` only. CI runs on every push to `main` and every pull request to it, and a tag `v<version>`
  makes the release ([CI/CD](#cicd)).

## CI/CD

* **CI runs on a Windows runner of GitHub (`windows-2025`), with the scripts a developer runs.** YAV is
  a program for Windows, and its tests start consoles, job objects, Windows PowerShell and Git.
  `.github/workflows/ci.yml` calls `scripts\test.ps1`, `scripts\package.ps1` and
  `scripts\verify-package.ps1` as they are called on a PC, so that a result there means what it means
  here. It installs exactly the SDK that `global.json` names and restores in locked mode, because the
  lock files name a package whose version comes with the SDK (`Microsoft.NET.ILLink.Tasks`) and
  `yav.exe` carries the runtime of the SDK that built it: lock files written by another SDK are
  committed together with a `global.json` that names it. Text files are checked out with LF, as the
  repository holds them. The tests run with `-HangSeconds 900`: when no test has begun or ended for 15
  minutes, the test platform ends the run and names the tests that were still running. The summary of
  the tests goes into the results the run keeps (`artifacts\test-results\test-results.md`), not into
  `docs\test-results.md`: a run on GitHub changes no file of the repository.
* **The check of the package runs without `-Sandbox` there**, because Windows Sandbox does not exist on
  Windows Server; the check in Windows Sandbox stays a step on a PC before a release. It runs with the
  variable `GITHUB_ACTIONS`, as every step on GitHub does. Spectre.Console, which formats YAV's tables,
  switched ANSI output on where it saw that variable, over YAV's choice of plain output; YAV now tells
  it to make no such adjustment (`ProfileEnrichment.UseDefaultEnrichers = false`), and a test starts
  `yav.exe` with `GITHUB_ACTIONS=true` and checks that a pipe receives no control sequence.
* **Never on GitHub: the tests with the installed agents, and a run with real models
  (`scripts\live-run.ps1` runs only in its tests, against a stand-in for `yav.exe`). The mutation
  check only by hand.** The runner has no agent and no account, and what consumes usage is yours to
  authorize. The mutation check rewrites source files while it runs and takes hours:
  `.github/workflows/mutation.yml` runs it, or a part of it, when it is started by hand. A mutation
  that makes a test hang is ended when no test has begun or ended for 10 minutes (`-HangSeconds`, 600
  by default) and counts as killed by the time limit; its line in the log says so, and the summary of
  the workflow lists it.
* **A tag `v<version>` is a release.** `.github/workflows/release.yml` builds from the tag as CI does,
  tests included, and publishes a GitHub Release with `yav.exe`, `yav.exe.sha256`, the Word guide as
  `YAV-Shell-<version>-Documentation-and-User-Guide.docx` (from `docs/YAV Shell - Documentation and User
  Guide.docx`) and the specification YAV Shell was built from as `YAV-Shell-Specification.txt` (the file
  at the root whose name begins with `Build YAV Shell`), with `docs/release-notes/<version>.md` as its
  text. It refuses a tag that is not on `main` or does not name the version in `Directory.Build.props`,
  release notes, a guide or a specification that is missing, and documentation in which a placeholder
  for a result is left; it never replaces a release. The text of the release gives the SHA-256 of each
  document; the attestation is for `yav.exe` only. The job that builds can only read the repository; the
  jobs that sign the attestation and publish run none of its code.
* **Every action is pinned to the full SHA of a commit**, with its version in a comment, because a tag
  can be moved to other code and a SHA cannot. Dependabot proposes newer versions of the actions once
  a week, and no NuGet packages: their versions are pinned and change only with a full test run.
* **A release carries an attestation of where `yav.exe` was built**, because `yav.exe` is not
  code-signed: Windows cannot say who built it, and `gh attestation verify` can say that the release
  workflow of this repository did, from the tag, on a runner of GitHub. The published command - in the
  release text, `SECURITY.md` and the README - names the signer workflow
  (`--signer-workflow .../release.yml`), the tag (`--source-ref refs/tags/v<version>`) and
  `--deny-self-hosted-runners`, because `--repo` alone accepts an attestation signed by any workflow of
  the repository, from any ref.
