# Third-party notices

YAV Shell is distributed together with the software listed here. Since version 0.2.0 all of it is inside
`yav.exe`: the program is one self-contained file that holds the .NET runtime and these libraries.
`yav install` writes this file next to the installed program, as `THIRD-PARTY-NOTICES.md` in the
installation folder, and it is part of the source repository. Versions are the ones pinned in
`Directory.Packages.props` and resolved in `src\Yav.Console\packages.lock.json`; the .NET runtime is the
runtime pack the .NET SDK 10.0.401 publishes with. Licenses are the ones the packages declare.

The license texts and notices travel with the program: they are in the folder `licenses` of the source
repository, and `yav install` installs them next to `yav.exe`, in the folder `licenses` of the
installation folder. Each file names the component it belongs to and where its text was copied from.

| Component | Version | License | License text | Project |
|---|---|---|---|---|
| .NET runtime, with the single-file host (inside `yav.exe`) | 10.0.12 | MIT | `licenses/dotnet-runtime.txt`, `licenses/dotnet-runtime-third-party-notices.txt` | https://github.com/dotnet/runtime |
| PrettyPrompt | 6.0.5 | MPL-2.0 | `licenses/PrettyPrompt.txt` | https://github.com/waf/PrettyPrompt |
| TextCopy (used by PrettyPrompt) | 6.2.1 | MIT | `licenses/TextCopy.txt` | https://github.com/CopyText/TextCopy |
| Microsoft.Extensions.DependencyInjection.Abstractions (used by TextCopy) | 7.0.0 | MIT | `licenses/Microsoft.Extensions.DependencyInjection.Abstractions.txt`, `licenses/Microsoft.Extensions.DependencyInjection.Abstractions-third-party-notices.txt` | https://github.com/dotnet/runtime |
| Spectre.Console, Spectre.Console.Ansi | 0.57.2 | MIT | `licenses/Spectre.Console.txt` | https://github.com/spectreconsole/spectre.console |
| Microsoft.Data.Sqlite, Microsoft.Data.Sqlite.Core | 10.0.12 | MIT | `licenses/Microsoft.Data.Sqlite.txt` | https://github.com/dotnet/efcore |
| SQLitePCLRaw (core, provider, bundle, lib) | 2.1.12 | Apache-2.0 | `licenses/SQLitePCLRaw.txt`, `licenses/SQLitePCLRaw-NOTICE.txt` | https://github.com/ericsink/SQLitePCL.raw |
| SQLite (native library `e_sqlite3.dll`) | as bundled by SQLitePCLRaw 2.1.12 | Public domain | `licenses/SQLite.txt` | https://www.sqlite.org |

Where a package contains a license file, that file is the text (the .NET runtime pack and
Microsoft.Extensions.DependencyInjection.Abstractions). The other packages declare their license only as an
expression; their texts are the license files of their source repositories at the commit or tag of the
release, as each file in `licenses` says. The NOTICE of SQLitePCL.raw is included as the Apache License 2.0
asks; its sections on SQLCipher and OpenSSL concern other SQLitePCLRaw bundles, which are not part of
`yav.exe`.

The native SQLite library is inside `yav.exe` as well. Windows loads a native library only from a file, so
the .NET host unpacks it into a directory of your user account the first time `yav.exe` starts.

PrettyPrompt is used unmodified, as a library. Its source code is available from the project page
above, as the Mozilla Public License 2.0 requires.

## Not distributed with YAV Shell

YAV Shell starts agent programs that you install yourself and that have their own terms:

- Codex CLI (OpenAI)
- Claude Code (Anthropic)

They are not part of any YAV Shell package. Git is used from your own installation as well.

Used only to build and test, and not distributed: xunit 2.9.3, xunit.runner.visualstudio 3.1.5,
Microsoft.NET.Test.Sdk 18.10.1.
