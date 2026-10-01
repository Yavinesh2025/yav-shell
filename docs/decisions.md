# Decisions

Engineering decisions that were made while YAV Shell was built, each with its reason. None of them
was needed to be asked first; every one of them can be changed. Decisions marked **yours to review**
set a default for behavior that the specification left open.

## Defaults that are yours to review

| Decision | Reason | Where to change it |
|---|---|---|
| A required check that already failed before the task is **your decision**, not a repair for Model A | Sending it to Model A would make a task repair things it was never asked to touch, and spend usage on it. The failure is shown together with the fact that it predates the task | `/quality preexisting repair` |
| An effort value YAV cannot rank (`ultra`) **always blocks** until you choose the exact value, under every policy | Codex describes `ultra` as maximum reasoning *with automatic task delegation*. Whether that is "the maximum" you meant is a decision, not a lookup | `/effort a\|b <value>` |
| Two repair cycles | The specification's default | `/limits repairs <n>` |
| In Adaptive mode the run is marked Adaptive even for a task for which no lower effort was approved | It can never be called Strict Max by mistake. The effort that was used is recorded per run | `/adaptive off` |
| A subscription route needs your acknowledgement once per route | The providers' terms differ for third-party clients and change. YAV states what it found and leaves the decision with the account holder | `/login <provider>` |
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
  never writes to the terminal itself.

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

* **Self-contained, folder layout, ReadyToRun.** The single-file layout unpacks native libraries when
  it starts. Both were measured; see `performance.md`.
* **The installer is a PowerShell script for the current user.** An Inno Setup script exists as well;
  it becomes an installer only where Inno Setup is installed to compile it.
* **Nothing is code-signed.** Signing needs a certificate that belongs to whoever publishes the program.
* **YAV Shell is proprietary: all rights reserved** (`LICENSE.txt`), as its owner decided on
  2026-09-30. The licenses of what is distributed with it are in `THIRD-PARTY-NOTICES.md`.

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
  `main`, with no remote. The first commit was made when you asked for it; later changes are
  committed when you ask. Build output, packages and the records of the runs with real models are
  not in it (`.gitignore`).
