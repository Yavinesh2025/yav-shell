# Third-party notices

YAV Shell is distributed together with the software listed here. Versions are the ones pinned in
`Directory.Packages.props` and the `packages.lock.json` files; licenses are the ones the packages declare
on nuget.org.

| Component | Version | License | Project |
|---|---|---|---|
| .NET runtime (part of the self-contained package) | 10.0.x | MIT | https://github.com/dotnet/runtime |
| PrettyPrompt | 6.0.5 | MPL-2.0 | https://github.com/waf/PrettyPrompt |
| TextCopy (used by PrettyPrompt) | 6.2.1 | MIT | https://github.com/CopyText/TextCopy |
| Microsoft.Extensions.DependencyInjection.Abstractions (used by TextCopy) | 7.0.0 | MIT | https://github.com/dotnet/runtime |
| Spectre.Console, Spectre.Console.Ansi | 0.57.2 | MIT | https://github.com/spectreconsole/spectre.console |
| Microsoft.Data.Sqlite, Microsoft.Data.Sqlite.Core | 10.0.12 | MIT | https://github.com/dotnet/efcore |
| SQLitePCLRaw (core, provider, bundle, lib) | 2.1.12 | Apache-2.0 | https://github.com/ericsink/SQLitePCL.raw |
| SQLite (native library `e_sqlite3.dll`) | as bundled by SQLitePCLRaw 2.1.12 | Public domain | https://www.sqlite.org |

PrettyPrompt is used unmodified, as a library. Its source code is available from the project page
above, as the Mozilla Public License 2.0 requires.

The full license texts are part of each package on nuget.org and of the projects named above.

## Not distributed with YAV Shell

YAV Shell starts agent programs that you install yourself and that have their own terms:

- Codex CLI (OpenAI)
- Claude Code (Anthropic)

They are not part of any YAV Shell package. Git is used from your own installation as well.

Used only to build and test, and not distributed: xunit 2.9.3, xunit.runner.visualstudio 3.1.5,
Microsoft.NET.Test.Sdk 18.10.1.
