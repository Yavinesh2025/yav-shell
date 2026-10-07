# Examples

Every file here is checked by the automated tests (`tests/Yav.Tests/Packaging/ExamplesTests.cs`), so
what it shows is what this version of YAV Shell does.

| File | What it is |
|---|---|
| `dotnet/yav.project.json` | required checks for a .NET solution: restore, build, tests, and a formatting check that is not required |
| `python/yav.project.json` | required checks for a Python project that uses the standard library only |
| `node/yav.project.json` | required checks for a Node.js project, one of which needs a database that is not always there |
| `task.md` | a request in a file, for `yav run --prompt-file` |
| `commands.txt` | commands and a request for the shell, read from a pipe or a file |
| `run-output.jsonl` | what `yav run --json` wrote for the request in `task.md` |

## Using a configuration

Copy the one that is closest to your project into the root of the project as `yav.project.json` and
change it. It is a proposal until you approve it. In a project that has no approved required check yet, the
first request you type shows the commands and asks; `/test trust` does the same at any time, and is
how a later change to the file is approved. The user guide describes every setting.

The files contain comments. YAV reads them; other programs that read JSON may not.

## Without a prompt

```powershell
yav run --project C:\Projects\Greeting --prompt-file task.md --json > run-output.jsonl
$result = Get-Content run-output.jsonl -Tail 1 | ConvertFrom-Json
if ($LASTEXITCODE -eq 0 -and $result.outcome -eq 'ready_to_apply') { "candidate $($result.candidate.candidateId) is ready" }
```

```powershell
Get-Content commands.txt | yav C:\Projects\Greeting
```

Lines from a pipe are read one after the other, each after the run before it has ended. Nobody can be
asked, so every question is answered with "no". When an agent asks for approval, nothing is granted
and the run ends as Approval Required.

## About `run-output.jsonl`

It is the output of a real run of `yav.exe`, in which a **scripted stand-in took the place of the
agents**: no model was asked. That is why the models are called `model-a` and `model-b`, why the run
took a second, and why the review reads like a form. The shape of the lines is what a run with real
agents writes; the test compares it with a new run every time.

The project was a directory with `src/greeting.txt` ("Helo, world!") and one approved check that fails
as long as the misspelling is there. Directories of the machine it was made on were replaced by
`C:\Projects\Greeting` and `C:\Users\you\AppData\Local\YavShell`.

Values YAV defines are written in lower case with underscores (`ready_to_apply`, `workspace_write`).
Values a provider defines are written the way the provider writes them (`workspace-write`), because
they are what was requested from it and what it reported.
