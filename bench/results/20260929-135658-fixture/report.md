# Benchmark results

Run on 2026-09-29 13:56 UTC, mode **fixture**, YAV Shell 0.1.0, Microsoft Windows 10.0.26200, 8 logical processors.
12 tasks, 3 repetition(s), order shuffled with seed 1. Model A: model-a (codex-app-server, effort maximum). Model B: model-b (codex-app-server, effort maximum).

> **The agents were scripted.** In fixture mode a stand-in plays Model A and Model B: it writes the
> solution that is known to pass and reviews nothing. Nothing was sent to a provider. These numbers
> measure what YAV Shell does around the agents on this machine: preparing the workspace, freezing the
> candidate, running the checks, applying. **They say nothing about what a model can do, about
> provider time, about usage or about cost**, and a success rate of 100% is what the script produces.

## By arm

| Arm | Runs | Succeeded | Success rate | With regressions | Repair cycles | Median | 90th percentile | Median active | Median until first action |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| yav-optimization-off | 39 | 39 | 100 % | 0 | 0 | 2.01 s | 2.32 s | 2.00 s | 0.38 s |
| yav | 39 | 39 | 100 % | 0 | 0 | 2.01 s | 2.32 s | 2.00 s | 0.38 s |
| two-models-by-hand | 39 | 39 | 100 % | 0 | 0 | 0.33 s | 0.68 s | 0.33 s | 0.09 s |
| model-a-alone | 39 | 39 | 100 % | 0 | 0 | 0.23 s | 0.53 s | 0.23 s | 0.09 s |

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
| yav-optimization-off | 39 | 2.01 s | 2.01 s | +0.01 s |
| two-models-by-hand | 39 | 2.01 s | 0.33 s | +1.67 s |
| model-a-alone | 39 | 2.01 s | 0.23 s | +1.77 s |

`model-a-alone` has no review. It is no quality-controlled workflow and is listed for orientation, not as an equal.

## By task

| Task | Step | Arm | Repetition | Succeeded | Outcome | Elapsed | Repairs | Failed checks | Regressions | Note |
|---|---:|---|---:|---|---|---:|---:|---|---|---|
| 01-small-edit | 1 | model-a-alone | 1 | yes | completed | 0.20 s | 0 |  |  |  |
| 01-small-edit | 1 | model-a-alone | 2 | yes | completed | 0.20 s | 0 |  |  |  |
| 01-small-edit | 1 | model-a-alone | 3 | yes | completed | 0.20 s | 0 |  |  |  |
| 01-small-edit | 1 | two-models-by-hand | 1 | yes | completed | 0.30 s | 0 |  |  |  |
| 01-small-edit | 1 | two-models-by-hand | 2 | yes | completed | 0.30 s | 0 |  |  |  |
| 01-small-edit | 1 | two-models-by-hand | 3 | yes | completed | 0.33 s | 0 |  |  |  |
| 01-small-edit | 1 | yav | 1 | yes | ready_to_apply | 2.02 s | 0 |  |  |  |
| 01-small-edit | 1 | yav | 2 | yes | ready_to_apply | 1.97 s | 0 |  |  |  |
| 01-small-edit | 1 | yav | 3 | yes | ready_to_apply | 1.97 s | 0 |  |  |  |
| 01-small-edit | 1 | yav-optimization-off | 1 | yes | ready_to_apply | 1.95 s | 0 |  |  |  |
| 01-small-edit | 1 | yav-optimization-off | 2 | yes | ready_to_apply | 1.96 s | 0 |  |  |  |
| 01-small-edit | 1 | yav-optimization-off | 3 | yes | ready_to_apply | 1.95 s | 0 |  |  |  |
| 02-bug-off-by-one | 1 | model-a-alone | 1 | yes | completed | 0.20 s | 0 |  |  |  |
| 02-bug-off-by-one | 1 | model-a-alone | 2 | yes | completed | 0.20 s | 0 |  |  |  |
| 02-bug-off-by-one | 1 | model-a-alone | 3 | yes | completed | 0.20 s | 0 |  |  |  |
| 02-bug-off-by-one | 1 | two-models-by-hand | 1 | yes | completed | 0.30 s | 0 |  |  |  |
| 02-bug-off-by-one | 1 | two-models-by-hand | 2 | yes | completed | 0.30 s | 0 |  |  |  |
| 02-bug-off-by-one | 1 | two-models-by-hand | 3 | yes | completed | 0.31 s | 0 |  |  |  |
| 02-bug-off-by-one | 1 | yav | 1 | yes | ready_to_apply | 1.97 s | 0 |  |  |  |
| 02-bug-off-by-one | 1 | yav | 2 | yes | ready_to_apply | 1.96 s | 0 |  |  |  |
| 02-bug-off-by-one | 1 | yav | 3 | yes | ready_to_apply | 1.99 s | 0 |  |  |  |
| 02-bug-off-by-one | 1 | yav-optimization-off | 1 | yes | ready_to_apply | 1.98 s | 0 |  |  |  |
| 02-bug-off-by-one | 1 | yav-optimization-off | 2 | yes | ready_to_apply | 2.02 s | 0 |  |  |  |
| 02-bug-off-by-one | 1 | yav-optimization-off | 3 | yes | ready_to_apply | 1.99 s | 0 |  |  |  |
| 03-add-tests | 1 | model-a-alone | 1 | yes | completed | 0.53 s | 0 |  |  |  |
| 03-add-tests | 1 | model-a-alone | 2 | yes | completed | 0.53 s | 0 |  |  |  |
| 03-add-tests | 1 | model-a-alone | 3 | yes | completed | 0.53 s | 0 |  |  |  |
| 03-add-tests | 1 | two-models-by-hand | 1 | yes | completed | 0.64 s | 0 |  |  |  |
| 03-add-tests | 1 | two-models-by-hand | 2 | yes | completed | 0.68 s | 0 |  |  |  |
| 03-add-tests | 1 | two-models-by-hand | 3 | yes | completed | 0.64 s | 0 |  |  |  |
| 03-add-tests | 1 | yav | 1 | yes | ready_to_apply | 2.32 s | 0 |  |  |  |
| 03-add-tests | 1 | yav | 2 | yes | ready_to_apply | 2.29 s | 0 |  |  |  |
| 03-add-tests | 1 | yav | 3 | yes | ready_to_apply | 2.31 s | 0 |  |  |  |
| 03-add-tests | 1 | yav-optimization-off | 1 | yes | ready_to_apply | 2.31 s | 0 |  |  |  |
| 03-add-tests | 1 | yav-optimization-off | 2 | yes | ready_to_apply | 2.28 s | 0 |  |  |  |
| 03-add-tests | 1 | yav-optimization-off | 3 | yes | ready_to_apply | 2.32 s | 0 |  |  |  |
| 04-feature-option | 1 | model-a-alone | 1 | yes | completed | 0.26 s | 0 |  |  |  |
| 04-feature-option | 1 | model-a-alone | 2 | yes | completed | 0.23 s | 0 |  |  |  |
| 04-feature-option | 1 | model-a-alone | 3 | yes | completed | 0.23 s | 0 |  |  |  |
| 04-feature-option | 1 | two-models-by-hand | 1 | yes | completed | 0.33 s | 0 |  |  |  |
| 04-feature-option | 1 | two-models-by-hand | 2 | yes | completed | 0.33 s | 0 |  |  |  |
| 04-feature-option | 1 | two-models-by-hand | 3 | yes | completed | 0.33 s | 0 |  |  |  |
| 04-feature-option | 1 | yav | 1 | yes | ready_to_apply | 2.01 s | 0 |  |  |  |
| 04-feature-option | 1 | yav | 2 | yes | ready_to_apply | 1.99 s | 0 |  |  |  |
| 04-feature-option | 1 | yav | 3 | yes | ready_to_apply | 2.00 s | 0 |  |  |  |
| 04-feature-option | 1 | yav-optimization-off | 1 | yes | ready_to_apply | 2.01 s | 0 |  |  |  |
| 04-feature-option | 1 | yav-optimization-off | 2 | yes | ready_to_apply | 1.98 s | 0 |  |  |  |
| 04-feature-option | 1 | yav-optimization-off | 3 | yes | ready_to_apply | 2.01 s | 0 |  |  |  |
| 05-refactor | 1 | model-a-alone | 1 | yes | completed | 0.20 s | 0 |  |  |  |
| 05-refactor | 1 | model-a-alone | 2 | yes | completed | 0.20 s | 0 |  |  |  |
| 05-refactor | 1 | model-a-alone | 3 | yes | completed | 0.20 s | 0 |  |  |  |
| 05-refactor | 1 | two-models-by-hand | 1 | yes | completed | 0.30 s | 0 |  |  |  |
| 05-refactor | 1 | two-models-by-hand | 2 | yes | completed | 0.30 s | 0 |  |  |  |
| 05-refactor | 1 | two-models-by-hand | 3 | yes | completed | 0.30 s | 0 |  |  |  |
| 05-refactor | 1 | yav | 1 | yes | ready_to_apply | 1.96 s | 0 |  |  |  |
| 05-refactor | 1 | yav | 2 | yes | ready_to_apply | 1.95 s | 0 |  |  |  |
| 05-refactor | 1 | yav | 3 | yes | ready_to_apply | 1.97 s | 0 |  |  |  |
| 05-refactor | 1 | yav-optimization-off | 1 | yes | ready_to_apply | 1.98 s | 0 |  |  |  |
| 05-refactor | 1 | yav-optimization-off | 2 | yes | ready_to_apply | 1.95 s | 0 |  |  |  |
| 05-refactor | 1 | yav-optimization-off | 3 | yes | ready_to_apply | 1.95 s | 0 |  |  |  |
| 06-multi-file-rename | 1 | model-a-alone | 1 | yes | completed | 0.26 s | 0 |  |  |  |
| 06-multi-file-rename | 1 | model-a-alone | 2 | yes | completed | 0.26 s | 0 |  |  |  |
| 06-multi-file-rename | 1 | model-a-alone | 3 | yes | completed | 0.25 s | 0 |  |  |  |
| 06-multi-file-rename | 1 | two-models-by-hand | 1 | yes | completed | 0.36 s | 0 |  |  |  |
| 06-multi-file-rename | 1 | two-models-by-hand | 2 | yes | completed | 0.40 s | 0 |  |  |  |
| 06-multi-file-rename | 1 | two-models-by-hand | 3 | yes | completed | 0.36 s | 0 |  |  |  |
| 06-multi-file-rename | 1 | yav | 1 | yes | ready_to_apply | 2.05 s | 0 |  |  |  |
| 06-multi-file-rename | 1 | yav | 2 | yes | ready_to_apply | 2.04 s | 0 |  |  |  |
| 06-multi-file-rename | 1 | yav | 3 | yes | ready_to_apply | 2.04 s | 0 |  |  |  |
| 06-multi-file-rename | 1 | yav-optimization-off | 1 | yes | ready_to_apply | 2.02 s | 0 |  |  |  |
| 06-multi-file-rename | 1 | yav-optimization-off | 2 | yes | ready_to_apply | 2.04 s | 0 |  |  |  |
| 06-multi-file-rename | 1 | yav-optimization-off | 3 | yes | ready_to_apply | 2.05 s | 0 |  |  |  |
| 07-bug-async | 1 | model-a-alone | 1 | yes | completed | 0.33 s | 0 |  |  |  |
| 07-bug-async | 1 | model-a-alone | 2 | yes | completed | 0.36 s | 0 |  |  |  |
| 07-bug-async | 1 | model-a-alone | 3 | yes | completed | 0.34 s | 0 |  |  |  |
| 07-bug-async | 1 | two-models-by-hand | 1 | yes | completed | 0.43 s | 0 |  |  |  |
| 07-bug-async | 1 | two-models-by-hand | 2 | yes | completed | 0.44 s | 0 |  |  |  |
| 07-bug-async | 1 | two-models-by-hand | 3 | yes | completed | 0.47 s | 0 |  |  |  |
| 07-bug-async | 1 | yav | 1 | yes | ready_to_apply | 2.10 s | 0 |  |  |  |
| 07-bug-async | 1 | yav | 2 | yes | ready_to_apply | 2.06 s | 0 |  |  |  |
| 07-bug-async | 1 | yav | 3 | yes | ready_to_apply | 2.12 s | 0 |  |  |  |
| 07-bug-async | 1 | yav-optimization-off | 1 | yes | ready_to_apply | 2.08 s | 0 |  |  |  |
| 07-bug-async | 1 | yav-optimization-off | 2 | yes | ready_to_apply | 2.11 s | 0 |  |  |  |
| 07-bug-async | 1 | yav-optimization-off | 3 | yes | ready_to_apply | 2.12 s | 0 |  |  |  |
| 08-feature-validation | 1 | model-a-alone | 1 | yes | completed | 0.27 s | 0 |  |  |  |
| 08-feature-validation | 1 | model-a-alone | 2 | yes | completed | 0.25 s | 0 |  |  |  |
| 08-feature-validation | 1 | model-a-alone | 3 | yes | completed | 0.26 s | 0 |  |  |  |
| 08-feature-validation | 1 | two-models-by-hand | 1 | yes | completed | 0.40 s | 0 |  |  |  |
| 08-feature-validation | 1 | two-models-by-hand | 2 | yes | completed | 0.36 s | 0 |  |  |  |
| 08-feature-validation | 1 | two-models-by-hand | 3 | yes | completed | 0.39 s | 0 |  |  |  |
| 08-feature-validation | 1 | yav | 1 | yes | ready_to_apply | 2.05 s | 0 |  |  |  |
| 08-feature-validation | 1 | yav | 2 | yes | ready_to_apply | 2.04 s | 0 |  |  |  |
| 08-feature-validation | 1 | yav | 3 | yes | ready_to_apply | 2.00 s | 0 |  |  |  |
| 08-feature-validation | 1 | yav-optimization-off | 1 | yes | ready_to_apply | 2.04 s | 0 |  |  |  |
| 08-feature-validation | 1 | yav-optimization-off | 2 | yes | ready_to_apply | 2.00 s | 0 |  |  |  |
| 08-feature-validation | 1 | yav-optimization-off | 3 | yes | ready_to_apply | 2.06 s | 0 |  |  |  |
| 09-follow-up | 1 | model-a-alone | 1 | yes | completed | 0.20 s | 0 |  |  |  |
| 09-follow-up | 1 | model-a-alone | 2 | yes | completed | 0.20 s | 0 |  |  |  |
| 09-follow-up | 1 | model-a-alone | 3 | yes | completed | 0.20 s | 0 |  |  |  |
| 09-follow-up | 1 | two-models-by-hand | 1 | yes | completed | 0.30 s | 0 |  |  |  |
| 09-follow-up | 1 | two-models-by-hand | 2 | yes | completed | 0.30 s | 0 |  |  |  |
| 09-follow-up | 1 | two-models-by-hand | 3 | yes | completed | 0.33 s | 0 |  |  |  |
| 09-follow-up | 1 | yav | 1 | yes | ready_to_apply | 2.12 s | 0 |  |  |  |
| 09-follow-up | 1 | yav | 2 | yes | ready_to_apply | 1.98 s | 0 |  |  |  |
| 09-follow-up | 1 | yav | 3 | yes | ready_to_apply | 2.00 s | 0 |  |  |  |
| 09-follow-up | 1 | yav-optimization-off | 1 | yes | ready_to_apply | 1.98 s | 0 |  |  |  |
| 09-follow-up | 1 | yav-optimization-off | 2 | yes | ready_to_apply | 2.00 s | 0 |  |  |  |
| 09-follow-up | 1 | yav-optimization-off | 3 | yes | ready_to_apply | 1.95 s | 0 |  |  |  |
| 09-follow-up | 2 | model-a-alone | 1 | yes | completed | 0.20 s | 0 |  |  |  |
| 09-follow-up | 2 | model-a-alone | 2 | yes | completed | 0.20 s | 0 |  |  |  |
| 09-follow-up | 2 | model-a-alone | 3 | yes | completed | 0.20 s | 0 |  |  |  |
| 09-follow-up | 2 | two-models-by-hand | 1 | yes | completed | 0.31 s | 0 |  |  |  |
| 09-follow-up | 2 | two-models-by-hand | 2 | yes | completed | 0.30 s | 0 |  |  |  |
| 09-follow-up | 2 | two-models-by-hand | 3 | yes | completed | 0.31 s | 0 |  |  |  |
| 09-follow-up | 2 | yav | 1 | yes | ready_to_apply | 1.79 s | 0 |  |  |  |
| 09-follow-up | 2 | yav | 2 | yes | ready_to_apply | 1.83 s | 0 |  |  |  |
| 09-follow-up | 2 | yav | 3 | yes | ready_to_apply | 1.78 s | 0 |  |  |  |
| 09-follow-up | 2 | yav-optimization-off | 1 | yes | ready_to_apply | 1.85 s | 0 |  |  |  |
| 09-follow-up | 2 | yav-optimization-off | 2 | yes | ready_to_apply | 1.78 s | 0 |  |  |  |
| 09-follow-up | 2 | yav-optimization-off | 3 | yes | ready_to_apply | 1.81 s | 0 |  |  |  |
| 10-code-and-documentation | 1 | model-a-alone | 1 | yes | completed | 0.25 s | 0 |  |  |  |
| 10-code-and-documentation | 1 | model-a-alone | 2 | yes | completed | 0.25 s | 0 |  |  |  |
| 10-code-and-documentation | 1 | model-a-alone | 3 | yes | completed | 0.25 s | 0 |  |  |  |
| 10-code-and-documentation | 1 | two-models-by-hand | 1 | yes | completed | 0.36 s | 0 |  |  |  |
| 10-code-and-documentation | 1 | two-models-by-hand | 2 | yes | completed | 0.35 s | 0 |  |  |  |
| 10-code-and-documentation | 1 | two-models-by-hand | 3 | yes | completed | 0.36 s | 0 |  |  |  |
| 10-code-and-documentation | 1 | yav | 1 | yes | ready_to_apply | 2.06 s | 0 |  |  |  |
| 10-code-and-documentation | 1 | yav | 2 | yes | ready_to_apply | 2.09 s | 0 |  |  |  |
| 10-code-and-documentation | 1 | yav | 3 | yes | ready_to_apply | 2.06 s | 0 |  |  |  |
| 10-code-and-documentation | 1 | yav-optimization-off | 1 | yes | ready_to_apply | 2.06 s | 0 |  |  |  |
| 10-code-and-documentation | 1 | yav-optimization-off | 2 | yes | ready_to_apply | 2.03 s | 0 |  |  |  |
| 10-code-and-documentation | 1 | yav-optimization-off | 3 | yes | ready_to_apply | 2.02 s | 0 |  |  |  |
| 11-bug-text | 1 | model-a-alone | 1 | yes | completed | 0.20 s | 0 |  |  |  |
| 11-bug-text | 1 | model-a-alone | 2 | yes | completed | 0.20 s | 0 |  |  |  |
| 11-bug-text | 1 | model-a-alone | 3 | yes | completed | 0.20 s | 0 |  |  |  |
| 11-bug-text | 1 | two-models-by-hand | 1 | yes | completed | 0.30 s | 0 |  |  |  |
| 11-bug-text | 1 | two-models-by-hand | 2 | yes | completed | 0.30 s | 0 |  |  |  |
| 11-bug-text | 1 | two-models-by-hand | 3 | yes | completed | 0.30 s | 0 |  |  |  |
| 11-bug-text | 1 | yav | 1 | yes | ready_to_apply | 1.99 s | 0 |  |  |  |
| 11-bug-text | 1 | yav | 2 | yes | ready_to_apply | 1.98 s | 0 |  |  |  |
| 11-bug-text | 1 | yav | 3 | yes | ready_to_apply | 1.94 s | 0 |  |  |  |
| 11-bug-text | 1 | yav-optimization-off | 1 | yes | ready_to_apply | 1.95 s | 0 |  |  |  |
| 11-bug-text | 1 | yav-optimization-off | 2 | yes | ready_to_apply | 1.94 s | 0 |  |  |  |
| 11-bug-text | 1 | yav-optimization-off | 3 | yes | ready_to_apply | 1.94 s | 0 |  |  |  |
| 12-regression-test | 1 | model-a-alone | 1 | yes | completed | 0.60 s | 0 |  |  |  |
| 12-regression-test | 1 | model-a-alone | 2 | yes | completed | 0.61 s | 0 |  |  |  |
| 12-regression-test | 1 | model-a-alone | 3 | yes | completed | 0.60 s | 0 |  |  |  |
| 12-regression-test | 1 | two-models-by-hand | 1 | yes | completed | 0.72 s | 0 |  |  |  |
| 12-regression-test | 1 | two-models-by-hand | 2 | yes | completed | 0.72 s | 0 |  |  |  |
| 12-regression-test | 1 | two-models-by-hand | 3 | yes | completed | 0.70 s | 0 |  |  |  |
| 12-regression-test | 1 | yav | 1 | yes | ready_to_apply | 2.39 s | 0 |  |  |  |
| 12-regression-test | 1 | yav | 2 | yes | ready_to_apply | 2.36 s | 0 |  |  |  |
| 12-regression-test | 1 | yav | 3 | yes | ready_to_apply | 2.39 s | 0 |  |  |  |
| 12-regression-test | 1 | yav-optimization-off | 1 | yes | ready_to_apply | 2.38 s | 0 |  |  |  |
| 12-regression-test | 1 | yav-optimization-off | 2 | yes | ready_to_apply | 2.37 s | 0 |  |  |  |
| 12-regression-test | 1 | yav-optimization-off | 3 | yes | ready_to_apply | 2.40 s | 0 |  |  |  |
