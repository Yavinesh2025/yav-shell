# Performance

What was measured for version 0.1.1 on 2026-09-30, and how to measure it again. The two runs with
real models were made on 2026-09-29 and 2026-09-30. The specification sets two targets for
development: an interactive prompt within one second, and a command acknowledged within 200
milliseconds. They are targets, not promises, and they are about what YAV does itself. **How long a
model takes is part of one section only**, [Two runs with real models](#two-runs-with-real-models).
Everywhere else the agents were the scripted stand-in, and YAV cannot make inference faster.

## The machine

| | |
|---|---|
| Processor | Intel Core i7-9700, 3.0 GHz, 8 cores |
| Memory | 16 GB |
| Disk | NVMe SSD |
| Windows | Windows 11 Pro, build 26200, long paths turned on |
| Real-time protection of Microsoft Defender | **off** while measuring. With it on, starting a program takes longer, the first time most of all |
| Git, Python, Node.js | 2.54.0, 3.14.7, 24.15.0 |

This is the only machine anything was measured on. The numbers of the new Windows further down are
from Windows Sandbox on the same machine.

## Against the targets

Measured on 2026-09-30 with the program of the package, `dist\yav-shell-0.1.1-win-x64\yav.exe`, in
the pseudo console of Windows. The times are those at which output arrived at the console, seven
starts each.

| | Target | First | Median of the others | Slowest |
|---|---:|---:|---:|---:|
| From the start of `yav` until the prompt waits for input | 1000 ms | 188 ms | 189 ms | 219 ms |
| From a key until it shows | | 23 ms | 22 ms | 50 ms |
| From Enter until the first reaction to `/status` | 200 ms | 22 ms | 11 ms | 22 ms |
| From Enter until `/status` has answered completely | | 85 ms | 71 ms | 93 ms |

The prompt does not wait for the agents: what they offer is asked for when it is needed. `/status`
is one of the larger answers; it reads the state of the task from the database.

The shell measures the same from inside and shows it with `/latency`: the time from start to
prompt, and for every command the time from Enter until its first line is written.

## Starting the program

`yav --version`, started by a program that waits for it to end.

| Where | First start | Median of five more | Slowest |
|---|---:|---:|---:|
| This machine, from the package, in an environment without .NET and Git | 98 ms | 70 ms | 76 ms |
| A new Windows (Windows Sandbox), from the package | 339 ms | 61 ms | 63 ms |

| | This machine | A new Windows |
|---|---:|---:|
| `yav doctor --json`, the first time, no agent installed | 193 ms | 434 ms |
| The shell reading `/help`, `/status`, `/exit` from a pipe | 250 ms | 393 ms |

These, and everything below that uses scripted agents, were measured with the package of this
version as it was before its last two changes: a section that tells Model A which checks YAV runs
itself, and the copyright in the properties of the program. Neither changes what is measured, and
the measurements were not repeated after them.

## A run, with agents that answer at once

`yav run --json --apply` for a change of one file in a project of two files. The agents were the
scripted stand-in, which answers within milliseconds, so what remains is what YAV does around the
agents. Seven runs; the stages are those `/latency` shows.

| | Median |
|---|---:|
| **The whole process, from start to end** | **2.19 s** |
| Preparation: the project and the agents side by side (0.29 s), then the isolated workspace (0.34 s) | 0.66 s |
| Implementation (scripted) | 0.02 s |
| Freezing the candidate | 0.13 s |
| Review (scripted) and required checks, side by side | 0.12 s |
| Evaluating the evidence, and whether the repository was left alone | 0.26 s |
| Apply: the evidence again (0.10 s), comparing and writing (0.26 s), the new starting point of the task (0.30 s) | 0.67 s |
| Not part of any stage: starting and ending the process, keeping records | about 0.3 s |

The numbers in brackets are those of the last of the seven runs.

Most of it is the time `git` takes to start, 50 to 60 ms each time on this machine, and the rest is
hashing and keeping records: every file is hashed completely whenever it matters, with no shortcut
through size or date.

All seven runs took between 2.10 and 2.24 s. They were measured on 2026-09-30 with the package of
this version, on a quiet machine. A measurement a few minutes earlier, right after the package had
been built, gave a median of 3.03 s, with every stage slower; that is how much the numbers depend on
what else a machine does.

Against the measurement of 2026-09-29, made with version 0.1.0, a run takes 0.2 s longer. Every
stage grew a little, by 0.01 to 0.07 s, most of all the preparation (0.07 s) and the evaluation of
the evidence (0.05 s); what in them takes longer was not looked into.

**What was changed after the first measurement, on 2026-09-29.** It showed 0.66 s before a run
started, and `/latency` did not show that time at all: the interval was measured and then not kept.
Now it is kept, the questions to Git are asked side by side, and so are the project and the agents.
That part took 0.27 s then, and the whole process went from 2.37 s to 1.97 s.

## The benchmark, with scripted agents

12 tasks, 4 ways of working, 3 repetitions, in an order that was shuffled: 156 runs, all of which
ended with the tasks' own checks passing. Made on 2026-09-30 with the package of this version;
`bench\results\20260930-003039-fixture\report.md` has every run. The same benchmark was made twice
before, on 2026-09-29 with version 0.1.0: `20260929-135658-fixture`, and
`20260929-103458-fixture` before the change above.

| Way of working | Median | 90th percentile | Until the first action of an agent |
|---|---:|---:|---:|
| `yav` | 2.24 s | 2.57 s | 0.42 s |
| `yav-optimization-off` | 2.21 s | 2.56 s | 0.41 s |
| `two-models-by-hand`: the same two steps without isolation, frozen candidate and evidence | 0.39 s | 0.74 s | 0.10 s |
| `model-a-alone`: no review | 0.24 s | 0.62 s | 0.10 s |

What this says:

* What YAV adds around the agents costs **about 1.9 seconds for a task** on this machine, for
  projects of this size: the median of the differences, run by run, is 1.86 s. With version 0.1.0
  it was 1.7 seconds, and before the change above 2.1 seconds.
* YAV's supplemental optimizations cost nothing that can be measured here (0.00 s). Whether they
  save time can only be seen with real models: they are about what a model is given to start with.
* It says **nothing** about models, about quality, about usage or about cost. The script writes the
  solution, so every run succeeds, and no run consumes anything.

A benchmark with real models is prepared and was not run. It sends requests through your accounts
and starts only with `--i-authorize-usage` and your `yes`; `bench\README.md` describes it. The one
run with real models that was made is not part of the benchmark: it compares nothing.

## Two runs with real models

The same task twice: `gpt-6-astra` through the Codex app server implemented, `opus` through Claude
Code reviewed, both at effort `max`. [verification.md](verification.md) tells what happened. **A run
is one measurement. It is not a forecast.**

### The second, on 2026-09-30, with this version

`yav run` went from the request to a result that was ready to apply without a person and without a
pause. The times are those `/latency` showed for the run afterwards
(`artifacts\live-run\20260930-100510\inspect-1-written.txt`).

| Stage | Intervals | Time |
|---|---:|---:|
| Review (Model B) | 1 | 1 min 42 s |
| Implementation (Model A) | 1 | 1 min 28 s |
| of the turns, in tools the agents ran | 21 | 16.8 s |
| Starting the agents and their conversations | 3 | 14.4 s |
| Preparation: reading the project and asking the agents side by side, then the isolated workspace | 3 | 14.3 s |
| Evaluating the evidence | 2 | 0.29 s |
| Freezing the candidate | 1 | 0.22 s |
| Required checks: `python -m unittest`, once | 1 | 0.14 s |
| **From the start of the run until it was ready to apply** | | **3 min 26 s** |
| The process `yav run`, from its start to its end | | 3 min 29 s |

Preparation and the starting of the agents ran side by side, 14.8 s of work in 13.8 s, and so did
review and required checks, 1 min 43 s of work in 1 min 42 s. Until the first action of an agent,
22.9 s passed.

* **The models are still where the time is.** Of 3 min 27 s, the two turns took 3 min 10 s. What
  YAV did itself between them, freezing, the check, evaluating the evidence and keeping records,
  took less than a second.
* **The questions that were added after the first run cost no time that shows.** Every conversation
  is now also asked which account it works with, and its directory is compared with the workspace.
  Starting the agents took 14.4 s in three intervals, against 16.4 s in five in the first run.
* **Asking the agents about themselves still takes about 13 seconds** before a run starts. Keeping
  what they said between two starts of `yav run` is still not done.

### The first, on 2026-09-29

The run was made in three parts, because it stopped twice and was continued. The times are computed
from the intervals the run recorded (`artifacts\live-run\20260929-141605\numbers.txt`, from the table
`spans` of its database).

| Stage | Intervals | Time |
|---|---:|---:|
| Implementation (Model A): 73.8 s until it asked for access, 39.8 s after it was continued | 2 | 1 min 54 s |
| Review (Model B): an attempt that YAV stopped after 4.9 s, and the review, 114.8 s | 2 | 2 min 0 s |
| of the turns, in tools the agents ran | 28 | 21.1 s |
| Starting the agents and their conversations | 5 | 16.4 s |
| Preparation: reading the project and asking the agents side by side, then the isolated workspace | 3 | 13.6 s |
| Freezing the candidate | 2 | 0.29 s |
| Required checks: `python -m unittest`, twice, 0.13 s each | 2 | 0.26 s |
| Evaluating the evidence | 2 | 0.21 s |
| Applying | 3 | 0.68 s |
| **From the first interval until the run was ready, without the waiting between the parts** | | **4 min 11 s** |
| Waiting between the parts, and until `/apply` | 3 | 50 min 54 s |

`/latency` showed 4 m 10 s as active and 49 m 55 s as waiting: it was asked before `/apply`, so the
last wait of 58.6 s was not over. Until the first action of an agent it showed 21.9 s.

What this shows that scripted agents cannot:

* **The models are where the time is.** Of 4 min 11 s, the turns of the two models took 3 min 53 s.
  What YAV did itself, the isolated workspace (0.40 s), freezing twice (0.29 s), evaluating the
  evidence (0.21 s) and keeping records between the stages (0.43 s), took 1.3 s, and applying took
  0.7 s more. That is what the measurements above say as well.
* **Asking the agents about themselves takes 13 seconds, once.** Before a run starts, YAV reads the
  version, the account and the models of both agents; the run recorded 13.2 s for it. With the
  stand-in it is 0.27 s. The run did not record how the 13 seconds are made up. Measured by hand
  afterwards (`artifacts\live-run\20260929-141605\probes\README.txt`): Claude Code 2.1.284 takes
  2.1 s to say its version, 2.2 s to say its account and 6.4 to 7.1 s to list its models. It is
  started three times, and each time a program of 246 MB has to start and read the configuration of
  its user; on this machine that configuration has 85 plugins. In the shell, `/models` took 12.5 s
  when it asked Claude Code and 2.3 s when it asked Codex. The shell keeps what it read for ten
  minutes, except where a command asks anew on purpose, as `/login` does; `yav run` is a new process
  every time and asks every time.
* **`/models` and `/login` were silent for 12 seconds** when they had to ask Claude Code. The target
  is a first reaction within 200 ms. They now say at once that they are asking, and whom; so does
  `/status`. That was changed after the run, and in the set-up of the second run it was seen with
  the real agents: "Asking Claude Code (CLI) for its version, account and models. No inference is
  requested."
* **Review and required checks ran side by side**, as designed. The check itself took 0.13 s. It
  began 2.1 s after the review had been started, which is the time the copy for checking took, and
  that time is not recorded as an interval of its own.
* **Asking Claude Code what is in effect costs no time that a run would notice.** Its answer comes
  when the process has started, 3.0 to 3.3 s after that, and the message with which a turn begins
  follows 0.03 s later, whether it was asked or not. Measured by hand, as above. The question which
  account a conversation works with (`initialize`) was added after the run; measured by hand on
  2026-09-30, its answer came 0.01 to 0.02 s after the answer to `get_settings`.

Not done, and candidates for a later version: keeping what the agents said about themselves
between two starts of `yav run`, which would save the 13 seconds and needs a decision on how old
such knowledge may be; and recording the time the copy for checking takes.

## The layout of the package

The two layouts were compared on 2026-09-29 with version 0.1.0; the folder of 0.1.1 has 227 files
and 86 MB.

| | Files | Size | `yav --version`, first start | Later starts |
|---|---:|---:|---:|---:|
| Folder: the program next to its libraries (what the package is) | 226 | 86 MB | 66 ms | 58 ms |
| Single file: everything in `yav.exe` | 1 | 94 MB | 125 ms | 61 ms |

The single file unpacks a native library (SQLite, 1.9 MB) into a directory of the user when it is
started for the first time. The folder writes nothing anywhere and every one of its files can be
compared with the manifest, so the folder is what is packaged. `scripts\package.ps1 -Layout
single-file` builds the other.

## Not measured

* Real models beyond the two runs above: other models, other efforts, larger tasks, a repair. The
  time until the first token of an answer was not measured at all.
* Projects of a realistic size. Preparing the workspace, freezing and applying grow with the number
  and the size of the files; the projects here have a handful of small files.
* A machine with real-time protection turned on, a slower disk, or a network drive.
* The first start after a restart of Windows, except on the new Windows of the sandbox.

## Measuring again

```powershell
scripts\package.ps1 -SkipTests
$env:YAV_MEASURE_EXE = (Resolve-Path dist\yav-shell-0.1.1-win-x64\yav.exe)
scripts\dev-check.ps1 -Filter "FullyQualifiedName~ConsoleMeasurements"   # writes artifacts\measurements\console.json and run.json
scripts\verify-package.ps1                                                 # starts, in an environment without .NET
scripts\verify-package.ps1 -Sandbox                                        # the same on a new Windows
artifacts\bin\Yav.Bench\debug_win-x64\yav-bench.exe run --repeat 3 --seed 1 --yav $env:YAV_MEASURE_EXE
```

Measure while nothing else is running. The tests that measure run with all other tests as well, but
what they assert there is only that the times are far from unusable.
