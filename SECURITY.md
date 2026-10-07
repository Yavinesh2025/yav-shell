# Security policy

## Supported versions

| Version | Supported |
|---|---|
| 0.2.0 | yes |
| 0.1.x | no: it was never published |

## Reporting a vulnerability

Please do not report a vulnerability in a public issue. Report it privately through GitHub: on the
**Security** tab of [Yavinesh2025/yav-shell](https://github.com/Yavinesh2025/yav-shell), choose
**Report a vulnerability** ([direct link](https://github.com/Yavinesh2025/yav-shell/security/advisories/new)).
GitHub keeps the report visible only to you and the owner of the repository until an advisory is
published.

Say which version you used (`yav --version`), which Windows build (`winver`), what you did, what
happened and what you expected. A command, a project or a file that shows the problem helps most.
Leave out secrets: an API key, a token or a password is never needed to show a problem of YAV.

There is no fixed time for an answer. A report is read, answered, and, when it is confirmed, fixed in a
new version; the advisory names the versions it affects.

## What is in scope

- `yav.exe`: the shell, `yav run`, `yav doctor`, and what YAV enforces itself - the isolated workspace,
  the acceptance of a candidate, `/apply` and `/undo`, the questions it asks and what an answer
  grants, the handling of what agents and tools write to the terminal, and the secrets it keeps.
- The installation: `yav install`, `yav uninstall`, and the offer `yav.exe` makes to install itself -
  what they write into the installation directory, the PATH of your account and "Installed apps", and
  what they remove.
- The scripts in `scripts\` that build, test, package and verify YAV, and the workflows in
  `.github\workflows` that run them.

[Security boundaries](docs/security-boundaries.md) says what is enforced, by whom, and what is not.
What it lists as outside YAV's control is not a vulnerability of YAV.

## What is not in scope

- Codex CLI and Claude Code themselves, the models behind them, and the accounts they use: report those
  to OpenAI and to Anthropic.
- What any program that runs as you can do anyway, such as reading the Windows Credential Manager of
  your account.

## Checking a download

`yav.exe` is not code-signed: Windows cannot tell you who built it. Before you start a downloaded copy,
check it in two ways.

1. **Its SHA-256 is the one published with it.** Every release carries `yav.exe.sha256` next to
   `yav.exe`. In PowerShell:

   ```powershell
   (Get-FileHash .\yav.exe -Algorithm SHA256).Hash.ToLowerInvariant()
   Get-Content .\yav.exe.sha256
   ```

   The first line has to be the hash at the start of `yav.exe.sha256`, which is written the way
   `sha256sum` writes it: the hash, two blanks, `yav.exe`.

2. **It was built from this repository by its release workflow.** With the
   [GitHub CLI](https://cli.github.com/), signed in (`gh auth login`), and the version of the release you
   downloaded in place of `<version>`:

   ```powershell
   gh attestation verify yav.exe --repo Yavinesh2025/yav-shell --signer-workflow Yavinesh2025/yav-shell/.github/workflows/release.yml --source-ref refs/tags/v<version> --deny-self-hosted-runners
   ```

   This checks the record GitHub made when the release workflow of this repository built the file from
   the tag `v<version>` on a GitHub-hosted runner. It fails for a file that was changed afterwards, or that
   was signed by another workflow, from another ref or on a self-hosted runner.

Windows SmartScreen may warn when a downloaded `yav.exe` is started for the first time. On a PC where
Smart App Control is on, Windows does not start an unsigned program at all, whichever way it is started.
