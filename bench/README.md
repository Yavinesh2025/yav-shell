# Benchmark

Twelve small tasks, acceptance checks that were written before any agent saw a task, and a tool that
runs the tasks in several ways and compares what came out.

**Status: the tool and the tasks are verified with scripted agents. No live run of the benchmark was
made.** A live run sends requests to the providers and consumes usage of your accounts, so it is yours
to start. One task, `01-small-edit`, was worked on by real models twice, outside the benchmark;
`docs/verification.md` tells what happened.
`results/` holds what was measured; every report says in its first lines whether the agents were
scripted or real.

| In `results/` | |
|---|---|
| `20260930-003039-fixture` | scripted agents, 156 runs, version 0.1.1 as it is packaged |
| `20260929-135658-fixture` | scripted agents, 156 runs, version 0.1.0 as it is packaged |
| `20260929-103458-fixture` | the same, before the time YAV needs to prepare a run was reduced; kept for comparison |

`docs/performance.md` says what these numbers mean and what they do not.

## The tasks

| Task | Kind | Language | What is asked for |
|---|---|---|---|
| 01-small-edit | small edit | Python | change the wording of a greeting |
| 02-bug-off-by-one | bug | Python | a page count that is one short |
| 03-add-tests | tests | Python | write tests that notice three given defects |
| 04-feature-option | feature | Python | add an option to a command |
| 05-refactor | refactoring | Python | split a function without changing what it does |
| 06-multi-file-rename | multi-file behavior | Node | rename a function everywhere |
| 07-bug-async | bug | Node | work that is returned before it is done |
| 08-feature-validation | feature | Node | refuse input that is not valid |
| 09-follow-up | related follow-up | Python | a setting, and then a second request that builds on the first |
| 10-code-and-documentation | small edit | Node | a default, where it is set and where it is described |
| 11-bug-text | bug | Python | initials of names as people write them |
| 12-regression-test | tests | Node | a defect, a test that shows it, and the correction |

Each task is a directory in `tasks/`:

| | |
|---|---|
| `task.json` | the request, the acceptance checks, protected paths, and the follow-up when there is one |
| `project/` | the project as it is before the task |
| `solution/` | a solution that is known to pass. It shows that the task can be done and that its checks accept a correct result. Agents never see it |
| `solution-follow-up/` | the same for the follow-up |

A task is sound when at least one of its checks fails for `project/` and all of them pass once
`solution/` is put over it. `yav-bench verify` checks exactly that, and so do the automated tests.

The tasks need Python 3 and Node.js 22 or later on PATH. They use nothing but the standard libraries.

Where a task asks for tests (03, 12), the acceptance check runs the new tests against the code as it
is, which must pass, and against code with the defect, which must fail. The files that decide are in
`acceptance/`, which is a protected path: a candidate that changes it needs your approval.

## The arms

| Arm | What runs the task |
|---|---|
| `yav` | YAV Shell as it is: `yav run --json --apply` |
| `yav-optimization-off` | the same with `/optimization off`, which isolates YAV's own additions |
| `two-models-by-hand` | the same two models at the same effort, asked the way a person asks them in two terminals: Model A works in the project itself, the checks are run, Model B is shown the diff, and what is wrong goes back to Model A |
| `model-a-alone` | Model A and the checks, without any review |

`two-models-by-hand` is a program that does what a person would do, not a person. It has no isolated
workspace, no frozen candidate and no evidence, and it uses the same agents, models, efforts,
sandboxes and checks as `yav`.

`model-a-alone` is no quality-controlled workflow. It is reported for orientation and never as an
equal of the others.

Every arm is judged the same way and by the benchmark itself: a run succeeded when every acceptance
check of its task passes in the project afterwards. What a run says about itself does not count.

## Running it

```powershell
scripts\build.ps1
$bench = 'artifacts\bin\Yav.Bench\debug_win-x64\yav-bench.exe'

& $bench verify                          # the tasks themselves, without any agent
& $bench run --repeat 3 --seed 1         # scripted agents: nothing is sent, nothing is consumed
& $bench run --mode live --i-authorize-usage --repeat 3 --seed 1
& $bench report bench\results\<run>      # writes report.md again
```

`--arms a,b` and `--only 01,02` narrow a run. The order of the runs is shuffled; the seed makes the
order repeatable. Every run gets a new copy of the project and, for the YAV arms, a data directory of
its own, so no run sees what another one left behind.

### Live runs

A live run

* uses the models and efforts you chose in YAV (`/models`, `/effort`) and only the account routes you
  acknowledged there (`/login`);
* shows how many runs it will make and through which agents, and starts only with
  `--i-authorize-usage` **and** after you typed `yes`;
* consumes usage for every run, including the runs that fail. How much is not known beforehand.

Provider speed and Adaptive mode change the billing or the way a model reasons. They are not part of
these arms and have to be measured in runs of their own, with nothing else changed.

## What a report contains

* success rate, runs with regressions, repair cycles;
* elapsed time: median and 90th percentile; active time without the time spent waiting for approval;
  time until an agent did something for the first time;
* tokens and charges for all runs and **per successful task, with what failed runs consumed included**;
* times compared between arms only for the runs in which both succeeded at the same task;
* every single run.

What a provider did not report is Unavailable. A sum is given only when every run reported the value.
Usage of a subscription is not turned into an amount of money.

## What a report does not say

Twelve tasks are twelve tasks. A report describes the runs that were made. It is no promise for other
tasks, other models or another day, and it does not show that two ways of working are equal in
quality. In fixture mode it says nothing at all about models: the script writes the solution.

What is not covered: tasks in large repositories, tasks that need the network or a database, and
cold starts of the agents as opposed to warm ones (every `yav run` starts its agents anew; only the
follow-up of task 09 continues a conversation).
