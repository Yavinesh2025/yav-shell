# Security boundaries

This document states what YAV Shell protects, by which mechanism, and what it does **not** protect.
It describes version 0.2.0. Where a boundary belongs to a provider's agent, YAV reports what that agent
says is in effect; it never invents a guarantee.

## Summary

| Boundary | Mechanism | What it is not |
| --- | --- | --- |
| Your project is not written during a run | The agent works in an isolated worktree or protected copy outside the project | Not a security sandbox. A Git worktree shares the repository's metadata |
| Only reviewed and tested content is applied | The candidate is frozen as content (manifest + SHA-256 + stored bytes); apply writes the stored bytes | Not a guarantee that reviewed code is free of defects |
| A project you accepted for review only | You grant it for one project with a typed `yes`; it applies only while the project has no approved required check and its approved checks can be read, and a run says when it applies; see [Trust](#trust) | Weaker than checks: a pass then means that Model B reported no blocking finding. No check of yours ran |
| Model B cannot change the candidate | Read-only sandbox (Codex) or removal of every tool that writes or runs code (Claude); verified from what the agent reports; changes are detected by fingerprint and reverted | For Claude this is tool restriction, not an operating-system sandbox |
| Model A's file access | The provider's own sandbox and approval policy | YAV adds no sandbox of its own. With Claude as Model A there is no operating-system sandbox |
| Child processes do not outlive YAV's work | Windows Job Objects with kill-on-close; every child is in its job before it runs | Job Objects group processes. They do not restrict file or network access. **A program from the Microsoft Store that an agent or a check starts leaves the job**; see [Process handling](#process-handling) |
| An agent works where it was started | The directory each conversation reports is compared with the workspace, with junctions and symbolic links resolved; another one stops the run under every policy | Only as good as what the agent reports. An agent that says nothing is shown as unconfirmed. A drive made with `subst` is not resolved |
| The account that is billed is the one that was shown | The account a conversation reports is compared with the route shown before the run; another kind of route stops the run under every policy | Only as good as what the agent reports: Claude Code answers `initialize`, Codex `account/read` and `account/updated`. "No key is named" is not taken for a confirmation |
| Only you decide about access | Codex conversations are opened with `approvalsReviewer: "user"` and refused when Codex reports anybody else deciding, or its own reviewer taking part | A refused conversation costs the turn; nothing in it is granted |
| Tool output cannot spoof YAV | Control sequences are removed from what agents write; what they ask for and run is shown with such characters written out, so nothing is hidden; agent text is shown behind a gutter; approval questions are drawn by YAV only | — |
| Access is granted only on purpose | An answer is a letter and Enter; some requests need the word `allow`; answers begun too early, typed ahead or pasted grant nothing | Not a protection against a person who allows without reading |
| Secrets | Windows Credential Manager; passed to one child process through its environment | An environment variable is inherited by whatever that agent starts |
| `/shell` and `/exec` | None: they run with your normal user rights | Not covered by any agent sandbox |
| Installing changes only your account | Files in the installation folder, listed with their SHA-256 in `yav-install.json`; the PATH and the "Installed apps" entry in your part of the registry (`HKCU`); see [Installation](#installation) | Not code-signed: nothing proves who built `yav.exe`. Compare it with the `yav.exe.sha256` of the build |

## The isolated workspace

* A Git repository with at least one commit gets a detached **worktree** under
  `%LOCALAPPDATA%\YavShell\workspaces\<id>\w`. A project that is not a Git repository gets a **protected copy**.
* Your uncommitted work (staged, unstaged and untracked files that are not ignored) is copied into the
  workspace and becomes part of the **baseline**. Changes are attributed to a task relative to that baseline,
  not relative to `HEAD`. Nothing in your project is committed, stashed, reset or cleaned.
* Ignored files are not copied unless the approved project configuration lists them under
  `replicateIgnored`. Paths that look like secrets (`.env`, key files, credential stores) are never copied
  unless they are also listed under `allowSecrets`.
* When the isolated workspace would not be an equivalent reproduction of the project (ignored runtime
  files, submodules, Git LFS without `git-lfs`, symbolic links, paths that differ only by letter case), the
  run does not start until you accept exactly those differences. The shell asks for that when a request
  needs it; `yav run` stays blocked.
* **A worktree is not a sandbox.** It shares branches, tags, the stash and the object store with your
  repository. YAV records the repository's references before a run and compares them afterwards; a run whose
  agent changed them is not offered for application. YAV itself never runs `git reset --hard`,
  `git clean`, `git stash` or `git commit` in your repository.
* **In-place mode** lets the agent work directly in the project. It needs a separate acknowledgement per
  project, and it is never chosen automatically.

## The candidate and its evidence

* After Model A's turn the workspace is **frozen**: every file is hashed completely (no shortcut through
  size or modification time) and its content is stored. The fingerprint is the SHA-256 of the canonical
  manifest.
* A review and every check result are bound **by YAV** to four values: the candidate fingerprint, the
  acceptance version (number of requirements), the hash of the immutable run profile, and the fingerprint of
  the toolchain. A result that differs in any of them is stale and counts as missing. The binding never
  depends on an identifier that a model echoes back.
* Required checks run in a **disposable copy** made from the frozen content, so a check cannot change the
  candidate. With `validationExecution: candidate` they run in the candidate workspace instead; then the
  fingerprint is compared after every check, and a check during which source changed is recorded as an
  error, never as a pass.
* `/apply` evaluates the evidence again, compares every target file in the project with the baseline, and
  refuses when a file was edited meanwhile. It writes through a journal that records what each file contained
  before. A failure in the middle rolls back; a crash in the middle is reconciled at the next start.
* `/undo` restores a file only when it still contains exactly what the apply wrote.

## Changes YAV makes itself: `/replace`

* The replacement is made in the isolated workspace, never in the project. It becomes a candidate like
  any other and reaches the project through `/apply` only, after review and required checks.
* The file is named from the directory you opened and has to be inside it. A path that is absolute,
  names a drive or leaves that directory is refused, also when it would stay inside the repository.
* A link, a binary file and a file that is not UTF-8 text are refused. The file is replaced by a
  complete new file, so a failure cannot leave half of a replacement behind.
* Nothing is changed when the text occurs more or less often than you said.

## Model B's read-only boundary

Enforcement, not a prompt:

* **Codex**: the review conversation is opened with `sandbox: read-only` and `approvalPolicy: never`.
  YAV reads the sandbox policy Codex reports for the conversation. Anything other than `read-only`
  means the review is **not started**.
* **Claude Code**: the review process is started with `--restricted`, `--tools Read,Glob,Grep`,
  `--disallowedTools mcp__*`, `--strict-mcp-config`, `--permission-mode dontAsk` and
  `--permission-prompts none`. YAV derives "read-only" from the tool list in Claude Code's own `init`
  message: every tool in it has to be one that YAV knows to change nothing. With Claude Code 2.1.284 the
  list is `Glob`, `Grep`, `Read` and `StructuredOutput`, the tool a structured result is handed back
  with. If the list contains anything else, a tool of a later version that YAV does not know included,
  the review is not started. This is tool restriction; Claude Code applies no operating-system sandbox
  in this mode.
* `--restricted` also confines Claude Code's file tools to the directories the review was given: the
  workspace and the directory with the evidence. In both runs with real models the reviewer tried to
  look into the original project, and Claude Code denied it. That boundary is Claude Code's; YAV
  relies on it and does not enforce it a second time.
* A request for more access from the reviewer is declined without asking you.
* After every review the workspace fingerprint is compared with the candidate. If it differs, the review is
  invalid, what was changed is put back from the frozen content, and the review is repeated once. Model A is
  the only AI writer.

## Windows sandbox state of Codex

On Windows, Codex gives a conversation a read-only policy when its own sandbox is not set up, even when a
writable workspace was requested. YAV detects this (`/doctor`, and the policy reported for each
conversation) and blocks the run with an explanation instead of letting Model A work without being able to
write. YAV does not set up, weaken or bypass Codex's sandbox.

## Process handling

* Executables are resolved to a full path before they are started. The working directory and relative
  `PATH` entries are never searched, so a file named `git.exe` or `codex.cmd` planted in a project is not
  run by YAV.
* Arguments are passed as a list. `.cmd` and `.bat` launchers are started through `cmd.exe /d /s /c`
  with arguments that contain `"`, `%`, `!` or line breaks rejected, because `cmd.exe` would
  reinterpret them.
* Prompts travel over standard input, never on a command line.
* Each child is in a Job Object before it runs and inherits only its three pipe handles. Stopping a run
  asks the provider to interrupt first, waits a bounded time, and then ends **only the process tree YAV
  started**. Unrelated shells, editors and agents are not affected.
* **Programs from the Microsoft Store (MSIX packages)** are a special case. Windows puts such a program
  into a job of its package while it creates it, and refuses a job the caller names. YAV then creates
  the program suspended, puts it into its own job, and only then lets it run, so that it is in the job
  before its first instruction. It also keeps what that program starts inside the package, which is
  what keeps those programs in the job; they then run with the identity of the package. This was
  tested with PowerShell 7 from the Store, which is the PowerShell of the machine YAV was built on.
* **The limit:** when a program that YAV did not start itself starts a program from the Store, the
  Store program leaves YAV's job, and so does everything it starts. This holds for an agent that runs
  `pwsh` or the `python` alias of the Store, and for a check that does. Windows does that, and YAV
  cannot prevent it from outside. Such a program keeps running when a run is stopped or runs out of
  time. `yav doctor` names the programs agents are likely to start (`pwsh`, `python`, `python3`) when
  they come from the Store here. A test keeps watching this limit: it fails the day Windows keeps such
  a program in the job, so that this text can be corrected.

## Secrets

* An API key is stored in the Windows Credential Manager under `YavShell/<name>`. It is never written to
  settings, the database, logs or transcripts. It is typed without being shown, and it is not kept in
  the history of what was entered.
* A data directory other than the default one (`YAV_HOME`) keeps its secrets under
  `YavShell/<scope>/<name>`, where the scope is derived from the directory. A second installation, or a
  test, can therefore neither read nor replace the key of the first. This separates installations of
  YAV from each other; it is no protection against other programs, which can read the Credential
  Manager of the user they run as.
* It reaches the agent only through that process's environment. **An environment variable is inherited
  by every command the agent runs.** For that reason YAV refuses to start Claude Code as Model A with an API
  key in a project you have not marked as trusted. The reviewer has no tool that runs code, so it is allowed.
* Agent output is scanned for the key before it is parsed; a match is replaced by `[redacted]`.
* YAV never reads, copies or relays a provider's login tokens. Logging in is done by the provider's own
  command in the foreground.

## Trust

* **Project trust** decides whether repository-controlled agent configuration is loaded (for Claude Code:
  `--setting-sources user` instead of `user,project,local`). It does not make anything in the repository
  an instruction to YAV.
* **Gate configuration** (`yav.project.json`) is executed only in the version you approved. YAV keeps its
  own copy of the approved content. A file that differs is reported; the approved version stays in effect.
  The file is always a protected path, so a candidate that changes it needs your explicit approval.
* Repository content and tool output are passed to the models between `<reference-data>` delimiters.
  Text inside that looks like a delimiter is neutralized first.
* **Review-only acceptance** is a weaker acceptance that you grant for one project. While required checks
  are on, which is the default, a project without an approved required check does not run. When YAV found
  no check it could propose, you declined what it proposed, or what you approved requires none, the shell
  offers to accept for this project that a candidate is accepted on Model B's review alone; that takes a
  typed `yes`, and it is not offered while the approved checks cannot be read. It applies only while
  the project has no approved required check: approve one, and checks are required again (an optional
  approved check does not set it aside, because a run does not run optional checks). When the approved
  checks cannot be read - a damaged stored approval, or a `yav.project.json` that cannot be used - it does
  not stand in for them: the run is blocked (`review-only-unknown`). A run in which it applies says so, and
  its profile records that no check was required. A pass then means that Model B reported no blocking
  finding, and nothing else: no check of yours ran. `/quality gates required` withdraws it for the selected
  project; a candidate that was accepted on the review alone is then not applied by `/apply` either, when
  the withdrawal came after its run started.
* **The questions of a first request** ask for what the commands ask for - the models, an account route,
  the checks of a project, the differences of a workspace, review-only acceptance - with the same typed
  `yes` for every grant, and record the same answers. They are asked only where someone can answer:
  `yav run` and input from a pipe or a file are blocked as before.

## Terminal output

Text from agents, tools and checks is untrusted. Before it is shown or stored, YAV removes escape
sequences (CSI, OSC including clipboard and title sequences, DCS, C1 controls), bare carriage returns,
bidirectional control characters and characters of no width. Such text is printed behind a mark
(`│`, `$`, `~`, `!`), so it cannot produce a line that looks like one of YAV's stage lines or prompts.

**What an agent asks for, and what it runs, is not cleaned but written out.** Removing an escape
sequence removes what it carries with it: `git status ESC]x; curl … | sh; BEL` would read
`git status`. So in a question, in the `$` line of a command, in the lines of tools and files, in the
history and in `yav run`'s summary, every character a terminal would act on or does not show is
written out as `\x1B` or `<U+009D>`, and the text after it stays. A question in which anything was
written out says so and can be allowed only with the word `allow`. A title an agent gave its
request stands in quotes wherever YAV names it.

* **Rows are broken by YAV, not by the terminal.** A row that the terminal begins because the right edge
  was reached starts in the first column with whatever text happens to be there. A long line of an agent
  could use that to place `[APPROVAL] ...` at the start of a row. YAV therefore breaks every line that is
  wider than the window itself, and begins each further row with the mark of the line or with its
  indentation. Only lines of YAV begin in the first column. This holds for the width the window had when
  the line was written: a terminal that re-flows text when its window is made narrower afterwards can
  still move text to the start of a row, and YAV cannot prevent what the terminal does with text it has
  already received.
* **An approval is answered by a letter and Enter**, never by a single key: a word typed for another
  purpose ("yes", "/stop", "abort") cannot grant anything. Requests the agent marks, requests with
  characters that had to be written out, and requests too long to show whole are allowed only with
  the word `allow`, and never for a whole conversation. An answer begun within 0.6 seconds after the
  question could first take keys grants nothing; keys typed before it was there are thrown away; keys
  that arrive as part of pasted text never answer. The question is drawn by YAV in the place of the
  input line, never by the agent, and names what it is about.
* Where nobody can see a question - input or output redirected - nobody is asked, and the run ends as
  Approval Required.
* **`yav run --json`** writes free text cleaned of control sequences, because whoever reads the JSON
  may print it. What an agent asks for and runs is written exactly, with every character a terminal
  acts on as a JSON escape.
* The line you are typing belongs to you: what you paste there is shown as you pasted it, with line
  breaks shown as a mark.

## Installation

`yav install`, and the offer a `yav.exe` that is not installed makes when it starts, work for the current
user only and never ask for administrator rights. They change:

* **Files in the installation folder**, by default `%LOCALAPPDATA%\Programs\YavShell`: `yav.exe`, the
  license, the third-party notices, the documentation, the examples, and `yav-install.json`, which lists
  each of these files with its size and SHA-256. They are written as bytes from the program that runs, so
  the installed copy does not carry the mark Windows gives a download.
* **`HKCU\Environment`, value `Path`**: the folder is added at the end, unless a part of the value names it
  already. The rest of the text, its `%VARIABLES%` and the kind of the value stay as they were.
  `--no-path` leaves the value alone. Windows is then told that the environment changed
  (`WM_SETTINGCHANGE`): Explorer, and other programs that handle the message, hand the new PATH to what
  they start from then on. A console that is open keeps the PATH it started with.
* **`HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\YavShell`**: the entry under "Installed
  apps", with `"<folder>\yav.exe" uninstall` as the command that removes it. `--no-register` leaves it out.

Refused before anything is written: the root of a drive; a folder in which the path of `yav.exe` would
have 260 characters or more, from which Windows would not start it; and, unless `--no-path` is given, a
folder whose name holds `;`, which would split it into two entries of the PATH, and a PATH that is not
stored as text. The PATH is read again right before it is written, so that a change another program made
meanwhile stays.

They never touch:

* **The data directory** (`%LOCALAPPDATA%\YavShell`, or the one `YAV_HOME` names): installing, replacing
  and removing leave its settings, history, workspaces and database as they are. `yav` started without
  arguments opens the data directory before the offer asks, and creates it on a first start, as every
  start of YAV does; a "no" to the offer in a console that was already open is kept there
  (`installOfferDeclined` in `settings.json`). Only `yav uninstall --remove-data` removes it, after you
  typed `yes`; where nobody can be asked, it removes nothing, the program neither. The program is never
  installed into the data directory or into a directory that holds it.
* The PATH of the machine, `HKEY_LOCAL_MACHINE`, `Program Files`, services, scheduled tasks, programs that
  start with Windows, file associations and the Start menu (a Start menu entry that 0.1.1 made stays).
* Files in the installation folder that the installation did not put there. A folder that exists, is not
  empty and holds neither a `yav-install.json` nor the `package-manifest.json` of 0.1.1 is not used at all;
  what an installation that was interrupted before its first list leaves (`yav-install.json.partial`) does
  not count.

**`--remove-data`** has to tell a data directory of YAV from any other, because `YAV_HOME` can name any
directory. It removes one only when it holds `yav.db` and, at its top level, nothing YAV does not create:
the files `yav.db`, `yav.db-wal`, `yav.db-shm`, `yav.db-journal`, `settings.json`, `settings.json.tmp-*`,
`settings.unreadable-*.json` and `history.txt*`, and the folders `logs`, `workspaces`, `blobs`, `exports`
and `schemas`; hidden and system entries count. The root of a drive and the folders of Windows and of your
account (your profile, Desktop, Documents, `%LOCALAPPDATA%`, `%APPDATA%`, `ProgramData`, both Program Files
folders, the Windows and system folders, the programs folder of your Start menu) are never taken for one,
and neither is a directory whose `yav.db` another process has open, as another YAV Shell would. All of this
is checked before you are asked; when it does not hold, nothing is removed, the program neither. It is
checked again just before anything is deleted; `yav.db` is then held open alone while the rest goes, and it
and `settings.json` are deleted last, so that a removal that stops part-way leaves a directory that is still
recognised. A link inside it is removed as a link; what it leads to is not entered. The API key YAV kept in
the Credential Manager for that directory is removed on its own and reported on its own, also when the
directory itself is gone already.

Installing and removing are refused while another YAV Shell process runs from that folder; the program that
installs or removes itself does not count. `yav uninstall` removes exactly the files `yav-install.json` lists,
the PATH entry for that folder, and the "Installed apps" entry when it names that folder, and deletes what an
interrupted write left beside them (`<file>.partial-<8 hex digits>`). A folder that still holds files of yours
is kept. A `yav.exe` built from the source tree, which the tests start, removes only an installation named
with `--dir`, so that it cannot reach the installation of your account.

A running program cannot delete its own file: when the removal is done by the installed program, as when
"Installed apps" starts it, everything else is removed first. The program and its list stay until it has
ended; the list then names only the program and holds a random text that ties it to this removal. As the
process ends - also when its window is closed with the X button - a hidden `cmd.exe` is started from the
system folder, with the system folder as its working directory, `/d` (no AutoRun commands) and `/v:off` (no
delayed expansion). It waits a few seconds, and only while the list still holds that text it deletes the
program (once more after ten seconds when the program is still in use), then the list only when the program
is gone, and then the folder when it is empty (`rmdir` without `/s`). `PING` and `FINDSTR`, which it uses to
wait and to read the list, are named by their full paths in the system folder, so that a program of the same
name elsewhere is never started. A newer installation made into the same folder in the meantime writes a list
without that text and is left alone. The command is given only paths without `%`, `!` and `"`, which CMD
would interpret; for any other path you are told to delete the folder yourself. This step is best effort and
waits a fixed time, not for the process.

**The offer is the one question where Enter alone means yes.** Installing changes only your own account, it
is what running a downloaded `yav.exe` is for, and `yav uninstall` takes it back. It is asked only with a
console for input and output that somebody sees: never from a pipe, and not in a console window that was
started hidden. Every grant of access, consent or acknowledgement still needs a typed `yes`.

**Nothing is code-signed.** Windows cannot tell you who built `yav.exe`, and it may warn when a downloaded copy
is started for the first time; where Smart App Control is on, Windows does not start an unsigned program at all,
whichever way it is started. `dist\yav.exe.sha256` records the hash of the file that was built; compare it
with the file you received. The hashes in `yav-install.json` show whether an installed file was changed later;
YAV does not compare them by itself when it starts.

## What is outside YAV's control

* What a provider does with the prompts and code it receives. With two providers, your task and source are
  sent to both. `/status` and `/login` show which account route each role uses.
* What Model A does inside its own sandbox and with the approvals you give it.
* `/shell` and `/exec`: they run as you, with your rights, outside every agent sandbox. Changes a child
  shell makes to its directory or environment do not come back to YAV.
* Telemetry: YAV sends none. The setting exists and is off; nothing in version 0.2.0 transmits data, and
  installing or removing it sends nothing either.
