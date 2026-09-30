# Benchmark results

Run on 2026-09-29 10:34 UTC, mode **fixture**, YAV Shell 0.1.0, Microsoft Windows 10.0.26200, 8 logical processors.
12 tasks, 3 repetition(s), order shuffled with seed 1. Model A: model-a (codex-app-server, effort maximum). Model B: model-b (codex-app-server, effort maximum).

> **The agents were scripted.** In fixture mode a stand-in plays Model A and Model B: it writes the
> solution that is known to pass and reviews nothing. Nothing was sent to a provider. These numbers
> measure what YAV Shell does around the agents on this machine: preparing the workspace, freezing the
> candidate, running the checks, applying. **They say nothing about what a model can do, about
> provider time, about usage or about cost**, and a success rate of 100% is what the script produces.

## By arm

| Arm | Runs | Succeeded | Success rate | With regressions | Repair cycles | Median | 90th percentile | Median active | Median until first action |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| yav-optimization-off | 39 | 39 | 100 % | 0 | 0 | 2.48 s | 2.85 s | 2.48 s | 0.39 s |
| yav | 39 | 39 | 100 % | 0 | 0 | 2.47 s | 2.78 s | 2.47 s | 0.39 s |
| two-models-by-hand | 39 | 39 | 100 % | 0 | 0 | 0.36 s | 0.67 s | 0.36 s | 0.09 s |
| model-a-alone | 39 | 39 | 100 % | 0 | 0 | 0.24 s | 0.58 s | 0.24 s | 0.09 s |

A run succeeded when every acceptance check of its task passes in the project afterwards. The benchmark decides that
itself, the same way for every arm. Elapsed time is everything from the start of a run to its end.

## Usage and cost

| Arm | Tokens, all runs | Tokens per successful task | Charge, all runs | Charge per successful task | Runs without usage | Runs without charge |
|---|---:|---:|---:|---:|---:|---:|
| yav-optimization-off | Unavailable | Unavailable | Unavailable | Unavailable | 39 | 39 |
| yav | Unavailable | Unavailable | Unavailable | Unavailable | 39 | 39 |
| two-models-by-hand | Unavailable | Unavailable | Unavailable | Unavailable | 39 | 39 |
| model-a-alone | Unavailable | Unavailable | Unavailable | Unavailable | 39 | 39 |

What failed runs consumed is part of the cost of a successful task. A sum is given only when every run reported
the value; otherwise it is Unavailable, because a sum of what happened to be reported would look like the whole.
A charge is what an agent itself reported. Usage of a subscription is not turned into an amount of money.

## Times where both succeeded

Compared with `yav`, for the runs in which both arms succeeded at the same task, step and repetition.

| Compared with | Pairs | Median of yav | Median of the other | Median difference |
|---|---:|---:|---:|---:|
| yav-optimization-off | 39 | 2.47 s | 2.48 s | -0.02 s |
| two-models-by-hand | 39 | 2.47 s | 0.36 s | +2.10 s |
| model-a-alone | 39 | 2.47 s | 0.24 s | +2.22 s |

`model-a-alone` has no review. It is no quality-controlled workflow and is listed for orientation, not as an equal.

## By task

| Task | Step | Arm | Repetition | Succeeded | Outcome | Elapsed | Repairs | Failed checks | Regressions | Note |
|---|---:|---|---:|---|---|---:|---:|---|---|---|
| 01-small-edit | 1 | model-a-alone | 1 | yes | completed | 0.20 s | 0 |  |  |  |
| 01-small-edit | 1 | model-a-alone | 2 | yes | completed | 0.19 s | 0 |  |  |  |
| 01-small-edit | 1 | model-a-alone | 3 | yes | completed | 0.20 s | 0 |  |  |  |
| 01-small-edit | 1 | two-models-by-hand | 1 | yes | completed | 0.35 s | 0 |  |  |  |
| 01-small-edit | 1 | two-models-by-hand | 2 | yes | completed | 0.36 s | 0 |  |  |  |
| 01-small-edit | 1 | two-models-by-hand | 3 | yes | completed | 0.34 s | 0 |  |  |  |
| 01-small-edit | 1 | yav | 1 | yes | ready_to_apply | 2.43 s | 0 |  |  |  |
| 01-small-edit | 1 | yav | 2 | yes | ready_to_apply | 2.44 s | 0 |  |  |  |
| 01-small-edit | 1 | yav | 3 | yes | ready_to_apply | 2.47 s | 0 |  |  |  |
| 01-small-edit | 1 | yav-optimization-off | 1 | yes | ready_to_apply | 2.41 s | 0 |  |  |  |
| 01-small-edit | 1 | yav-optimization-off | 2 | yes | ready_to_apply | 2.35 s | 0 |  |  |  |
| 01-small-edit | 1 | yav-optimization-off | 3 | yes | ready_to_apply | 2.50 s | 0 |  |  |  |
| 02-bug-off-by-one | 1 | model-a-alone | 1 | yes | completed | 0.20 s | 0 |  |  |  |
| 02-bug-off-by-one | 1 | model-a-alone | 2 | yes | completed | 0.21 s | 0 |  |  |  |
| 02-bug-off-by-one | 1 | model-a-alone | 3 | yes | completed | 0.20 s | 0 |  |  |  |
| 02-bug-off-by-one | 1 | two-models-by-hand | 1 | yes | completed | 0.36 s | 0 |  |  |  |
| 02-bug-off-by-one | 1 | two-models-by-hand | 2 | yes | completed | 0.30 s | 0 |  |  |  |
| 02-bug-off-by-one | 1 | two-models-by-hand | 3 | yes | completed | 0.30 s | 0 |  |  |  |
| 02-bug-off-by-one | 1 | yav | 1 | yes | ready_to_apply | 2.42 s | 0 |  |  |  |
| 02-bug-off-by-one | 1 | yav | 2 | yes | ready_to_apply | 2.37 s | 0 |  |  |  |
| 02-bug-off-by-one | 1 | yav | 3 | yes | ready_to_apply | 2.41 s | 0 |  |  |  |
| 02-bug-off-by-one | 1 | yav-optimization-off | 1 | yes | ready_to_apply | 2.38 s | 0 |  |  |  |
| 02-bug-off-by-one | 1 | yav-optimization-off | 2 | yes | ready_to_apply | 2.44 s | 0 |  |  |  |
| 02-bug-off-by-one | 1 | yav-optimization-off | 3 | yes | ready_to_apply | 2.42 s | 0 |  |  |  |
| 03-add-tests | 1 | model-a-alone | 1 | yes | completed | 0.54 s | 0 |  |  |  |
| 03-add-tests | 1 | model-a-alone | 2 | yes | completed | 0.53 s | 0 |  |  |  |
| 03-add-tests | 1 | model-a-alone | 3 | yes | completed | 0.58 s | 0 |  |  |  |
| 03-add-tests | 1 | two-models-by-hand | 1 | yes | completed | 0.67 s | 0 |  |  |  |
| 03-add-tests | 1 | two-models-by-hand | 2 | yes | completed | 0.65 s | 0 |  |  |  |
| 03-add-tests | 1 | two-models-by-hand | 3 | yes | completed | 0.65 s | 0 |  |  |  |
| 03-add-tests | 1 | yav | 1 | yes | ready_to_apply | 2.76 s | 0 |  |  |  |
| 03-add-tests | 1 | yav | 2 | yes | ready_to_apply | 2.78 s | 0 |  |  |  |
| 03-add-tests | 1 | yav | 3 | yes | ready_to_apply | 2.69 s | 0 |  |  |  |
| 03-add-tests | 1 | yav-optimization-off | 1 | yes | ready_to_apply | 2.85 s | 0 |  |  |  |
| 03-add-tests | 1 | yav-optimization-off | 2 | yes | ready_to_apply | 2.80 s | 0 |  |  |  |
| 03-add-tests | 1 | yav-optimization-off | 3 | yes | ready_to_apply | 2.75 s | 0 |  |  |  |
| 04-feature-option | 1 | model-a-alone | 1 | yes | completed | 0.26 s | 0 |  |  |  |
| 04-feature-option | 1 | model-a-alone | 2 | yes | completed | 0.24 s | 0 |  |  |  |
| 04-feature-option | 1 | model-a-alone | 3 | yes | completed | 0.23 s | 0 |  |  |  |
| 04-feature-option | 1 | two-models-by-hand | 1 | yes | completed | 0.33 s | 0 |  |  |  |
| 04-feature-option | 1 | two-models-by-hand | 2 | yes | completed | 0.33 s | 0 |  |  |  |
| 04-feature-option | 1 | two-models-by-hand | 3 | yes | completed | 0.33 s | 0 |  |  |  |
| 04-feature-option | 1 | yav | 1 | yes | ready_to_apply | 2.47 s | 0 |  |  |  |
| 04-feature-option | 1 | yav | 2 | yes | ready_to_apply | 2.43 s | 0 |  |  |  |
| 04-feature-option | 1 | yav | 3 | yes | ready_to_apply | 2.45 s | 0 |  |  |  |
| 04-feature-option | 1 | yav-optimization-off | 1 | yes | ready_to_apply | 2.43 s | 0 |  |  |  |
| 04-feature-option | 1 | yav-optimization-off | 2 | yes | ready_to_apply | 2.36 s | 0 |  |  |  |
| 04-feature-option | 1 | yav-optimization-off | 3 | yes | ready_to_apply | 2.52 s | 0 |  |  |  |
| 05-refactor | 1 | model-a-alone | 1 | yes | completed | 0.20 s | 0 |  |  |  |
| 05-refactor | 1 | model-a-alone | 2 | yes | completed | 0.20 s | 0 |  |  |  |
| 05-refactor | 1 | model-a-alone | 3 | yes | completed | 0.20 s | 0 |  |  |  |
| 05-refactor | 1 | two-models-by-hand | 1 | yes | completed | 0.31 s | 0 |  |  |  |
| 05-refactor | 1 | two-models-by-hand | 2 | yes | completed | 0.31 s | 0 |  |  |  |
| 05-refactor | 1 | two-models-by-hand | 3 | yes | completed | 0.30 s | 0 |  |  |  |
| 05-refactor | 1 | yav | 1 | yes | ready_to_apply | 2.34 s | 0 |  |  |  |
| 05-refactor | 1 | yav | 2 | yes | ready_to_apply | 2.47 s | 0 |  |  |  |
| 05-refactor | 1 | yav | 3 | yes | ready_to_apply | 2.38 s | 0 |  |  |  |
| 05-refactor | 1 | yav-optimization-off | 1 | yes | ready_to_apply | 2.36 s | 0 |  |  |  |
| 05-refactor | 1 | yav-optimization-off | 2 | yes | ready_to_apply | 2.37 s | 0 |  |  |  |
| 05-refactor | 1 | yav-optimization-off | 3 | yes | ready_to_apply | 2.44 s | 0 |  |  |  |
| 06-multi-file-rename | 1 | model-a-alone | 1 | yes | completed | 0.25 s | 0 |  |  |  |
| 06-multi-file-rename | 1 | model-a-alone | 2 | yes | completed | 0.26 s | 0 |  |  |  |
| 06-multi-file-rename | 1 | model-a-alone | 3 | yes | completed | 0.25 s | 0 |  |  |  |
| 06-multi-file-rename | 1 | two-models-by-hand | 1 | yes | completed | 0.36 s | 0 |  |  |  |
| 06-multi-file-rename | 1 | two-models-by-hand | 2 | yes | completed | 0.37 s | 0 |  |  |  |
| 06-multi-file-rename | 1 | two-models-by-hand | 3 | yes | completed | 0.36 s | 0 |  |  |  |
| 06-multi-file-rename | 1 | yav | 1 | yes | ready_to_apply | 2.68 s | 0 |  |  |  |
| 06-multi-file-rename | 1 | yav | 2 | yes | ready_to_apply | 2.48 s | 0 |  |  |  |
| 06-multi-file-rename | 1 | yav | 3 | yes | ready_to_apply | 2.59 s | 0 |  |  |  |
| 06-multi-file-rename | 1 | yav-optimization-off | 1 | yes | ready_to_apply | 2.51 s | 0 |  |  |  |
| 06-multi-file-rename | 1 | yav-optimization-off | 2 | yes | ready_to_apply | 2.59 s | 0 |  |  |  |
| 06-multi-file-rename | 1 | yav-optimization-off | 3 | yes | ready_to_apply | 2.96 s | 0 |  |  |  |
| 07-bug-async | 1 | model-a-alone | 1 | yes | completed | 0.33 s | 0 |  |  |  |
| 07-bug-async | 1 | model-a-alone | 2 | yes | completed | 0.34 s | 0 |  |  |  |
| 07-bug-async | 1 | model-a-alone | 3 | yes | completed | 0.33 s | 0 |  |  |  |
| 07-bug-async | 1 | two-models-by-hand | 1 | yes | completed | 0.47 s | 0 |  |  |  |
| 07-bug-async | 1 | two-models-by-hand | 2 | yes | completed | 0.44 s | 0 |  |  |  |
| 07-bug-async | 1 | two-models-by-hand | 3 | yes | completed | 0.44 s | 0 |  |  |  |
| 07-bug-async | 1 | yav | 1 | yes | ready_to_apply | 2.56 s | 0 |  |  |  |
| 07-bug-async | 1 | yav | 2 | yes | ready_to_apply | 2.55 s | 0 |  |  |  |
| 07-bug-async | 1 | yav | 3 | yes | ready_to_apply | 2.47 s | 0 |  |  |  |
| 07-bug-async | 1 | yav-optimization-off | 1 | yes | ready_to_apply | 2.55 s | 0 |  |  |  |
| 07-bug-async | 1 | yav-optimization-off | 2 | yes | ready_to_apply | 2.49 s | 0 |  |  |  |
| 07-bug-async | 1 | yav-optimization-off | 3 | yes | ready_to_apply | 2.55 s | 0 |  |  |  |
| 08-feature-validation | 1 | model-a-alone | 1 | yes | completed | 0.27 s | 0 |  |  |  |
| 08-feature-validation | 1 | model-a-alone | 2 | yes | completed | 0.28 s | 0 |  |  |  |
| 08-feature-validation | 1 | model-a-alone | 3 | yes | completed | 0.27 s | 0 |  |  |  |
| 08-feature-validation | 1 | two-models-by-hand | 1 | yes | completed | 0.36 s | 0 |  |  |  |
| 08-feature-validation | 1 | two-models-by-hand | 2 | yes | completed | 0.36 s | 0 |  |  |  |
| 08-feature-validation | 1 | two-models-by-hand | 3 | yes | completed | 0.40 s | 0 |  |  |  |
| 08-feature-validation | 1 | yav | 1 | yes | ready_to_apply | 2.47 s | 0 |  |  |  |
| 08-feature-validation | 1 | yav | 2 | yes | ready_to_apply | 2.52 s | 0 |  |  |  |
| 08-feature-validation | 1 | yav | 3 | yes | ready_to_apply | 2.47 s | 0 |  |  |  |
| 08-feature-validation | 1 | yav-optimization-off | 1 | yes | ready_to_apply | 2.50 s | 0 |  |  |  |
| 08-feature-validation | 1 | yav-optimization-off | 2 | yes | ready_to_apply | 2.48 s | 0 |  |  |  |
| 08-feature-validation | 1 | yav-optimization-off | 3 | yes | ready_to_apply | 2.46 s | 0 |  |  |  |
| 09-follow-up | 1 | model-a-alone | 1 | yes | completed | 0.20 s | 0 |  |  |  |
| 09-follow-up | 1 | model-a-alone | 2 | yes | completed | 0.20 s | 0 |  |  |  |
| 09-follow-up | 1 | model-a-alone | 3 | yes | completed | 0.20 s | 0 |  |  |  |
| 09-follow-up | 1 | two-models-by-hand | 1 | yes | completed | 0.30 s | 0 |  |  |  |
| 09-follow-up | 1 | two-models-by-hand | 2 | yes | completed | 0.31 s | 0 |  |  |  |
| 09-follow-up | 1 | two-models-by-hand | 3 | yes | completed | 0.30 s | 0 |  |  |  |
| 09-follow-up | 1 | yav | 1 | yes | ready_to_apply | 2.41 s | 0 |  |  |  |
| 09-follow-up | 1 | yav | 2 | yes | ready_to_apply | 2.49 s | 0 |  |  |  |
| 09-follow-up | 1 | yav | 3 | yes | ready_to_apply | 2.51 s | 0 |  |  |  |
| 09-follow-up | 1 | yav-optimization-off | 1 | yes | ready_to_apply | 2.59 s | 0 |  |  |  |
| 09-follow-up | 1 | yav-optimization-off | 2 | yes | ready_to_apply | 2.33 s | 0 |  |  |  |
| 09-follow-up | 1 | yav-optimization-off | 3 | yes | ready_to_apply | 2.43 s | 0 |  |  |  |
| 09-follow-up | 2 | model-a-alone | 1 | yes | completed | 0.20 s | 0 |  |  |  |
| 09-follow-up | 2 | model-a-alone | 2 | yes | completed | 0.20 s | 0 |  |  |  |
| 09-follow-up | 2 | model-a-alone | 3 | yes | completed | 0.20 s | 0 |  |  |  |
| 09-follow-up | 2 | two-models-by-hand | 1 | yes | completed | 0.32 s | 0 |  |  |  |
| 09-follow-up | 2 | two-models-by-hand | 2 | yes | completed | 0.32 s | 0 |  |  |  |
| 09-follow-up | 2 | two-models-by-hand | 3 | yes | completed | 0.30 s | 0 |  |  |  |
| 09-follow-up | 2 | yav | 1 | yes | ready_to_apply | 2.26 s | 0 |  |  |  |
| 09-follow-up | 2 | yav | 2 | yes | ready_to_apply | 2.23 s | 0 |  |  |  |
| 09-follow-up | 2 | yav | 3 | yes | ready_to_apply | 2.16 s | 0 |  |  |  |
| 09-follow-up | 2 | yav-optimization-off | 1 | yes | ready_to_apply | 2.25 s | 0 |  |  |  |
| 09-follow-up | 2 | yav-optimization-off | 2 | yes | ready_to_apply | 2.24 s | 0 |  |  |  |
| 09-follow-up | 2 | yav-optimization-off | 3 | yes | ready_to_apply | 2.20 s | 0 |  |  |  |
| 10-code-and-documentation | 1 | model-a-alone | 1 | yes | completed | 0.26 s | 0 |  |  |  |
| 10-code-and-documentation | 1 | model-a-alone | 2 | yes | completed | 0.25 s | 0 |  |  |  |
| 10-code-and-documentation | 1 | model-a-alone | 3 | yes | completed | 0.26 s | 0 |  |  |  |
| 10-code-and-documentation | 1 | two-models-by-hand | 1 | yes | completed | 0.36 s | 0 |  |  |  |
| 10-code-and-documentation | 1 | two-models-by-hand | 2 | yes | completed | 0.36 s | 0 |  |  |  |
| 10-code-and-documentation | 1 | two-models-by-hand | 3 | yes | completed | 0.36 s | 0 |  |  |  |
| 10-code-and-documentation | 1 | yav | 1 | yes | ready_to_apply | 2.48 s | 0 |  |  |  |
| 10-code-and-documentation | 1 | yav | 2 | yes | ready_to_apply | 2.66 s | 0 |  |  |  |
| 10-code-and-documentation | 1 | yav | 3 | yes | ready_to_apply | 2.64 s | 0 |  |  |  |
| 10-code-and-documentation | 1 | yav-optimization-off | 1 | yes | ready_to_apply | 2.56 s | 0 |  |  |  |
| 10-code-and-documentation | 1 | yav-optimization-off | 2 | yes | ready_to_apply | 2.48 s | 0 |  |  |  |
| 10-code-and-documentation | 1 | yav-optimization-off | 3 | yes | ready_to_apply | 2.47 s | 0 |  |  |  |
| 11-bug-text | 1 | model-a-alone | 1 | yes | completed | 0.21 s | 0 |  |  |  |
| 11-bug-text | 1 | model-a-alone | 2 | yes | completed | 0.20 s | 0 |  |  |  |
| 11-bug-text | 1 | model-a-alone | 3 | yes | completed | 0.20 s | 0 |  |  |  |
| 11-bug-text | 1 | two-models-by-hand | 1 | yes | completed | 0.31 s | 0 |  |  |  |
| 11-bug-text | 1 | two-models-by-hand | 2 | yes | completed | 0.30 s | 0 |  |  |  |
| 11-bug-text | 1 | two-models-by-hand | 3 | yes | completed | 0.34 s | 0 |  |  |  |
| 11-bug-text | 1 | yav | 1 | yes | ready_to_apply | 2.38 s | 0 |  |  |  |
| 11-bug-text | 1 | yav | 2 | yes | ready_to_apply | 2.38 s | 0 |  |  |  |
| 11-bug-text | 1 | yav | 3 | yes | ready_to_apply | 2.39 s | 0 |  |  |  |
| 11-bug-text | 1 | yav-optimization-off | 1 | yes | ready_to_apply | 2.43 s | 0 |  |  |  |
| 11-bug-text | 1 | yav-optimization-off | 2 | yes | ready_to_apply | 2.63 s | 0 |  |  |  |
| 11-bug-text | 1 | yav-optimization-off | 3 | yes | ready_to_apply | 2.43 s | 0 |  |  |  |
| 12-regression-test | 1 | model-a-alone | 1 | yes | completed | 0.60 s | 0 |  |  |  |
| 12-regression-test | 1 | model-a-alone | 2 | yes | completed | 0.61 s | 0 |  |  |  |
| 12-regression-test | 1 | model-a-alone | 3 | yes | completed | 0.63 s | 0 |  |  |  |
| 12-regression-test | 1 | two-models-by-hand | 1 | yes | completed | 0.70 s | 0 |  |  |  |
| 12-regression-test | 1 | two-models-by-hand | 2 | yes | completed | 0.77 s | 0 |  |  |  |
| 12-regression-test | 1 | two-models-by-hand | 3 | yes | completed | 0.75 s | 0 |  |  |  |
| 12-regression-test | 1 | yav | 1 | yes | ready_to_apply | 2.89 s | 0 |  |  |  |
| 12-regression-test | 1 | yav | 2 | yes | ready_to_apply | 2.87 s | 0 |  |  |  |
| 12-regression-test | 1 | yav | 3 | yes | ready_to_apply | 2.84 s | 0 |  |  |  |
| 12-regression-test | 1 | yav-optimization-off | 1 | yes | ready_to_apply | 3.03 s | 0 |  |  |  |
| 12-regression-test | 1 | yav-optimization-off | 2 | yes | ready_to_apply | 2.99 s | 0 |  |  |  |
| 12-regression-test | 1 | yav-optimization-off | 3 | yes | ready_to_apply | 2.82 s | 0 |  |  |  |
