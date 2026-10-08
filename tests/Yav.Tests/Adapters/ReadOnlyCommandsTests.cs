using Yav.Adapters;

namespace Yav.Tests.Adapters;

public class ReadOnlyCommandsTests
{
    [Theory]
    [InlineData("\"C:\\Program Files\\WindowsApps\\Microsoft.PowerShell_7.6.6.0_x64__8wekyb3d8bbwe\\pwsh.exe\" -NoProfile -Command 'Get-ChildItem -Force | Select-Object Name,Mode,Length'")]
    [InlineData("Get-Content src/app.txt")]
    [InlineData("cmd /c dir /b")]
    [InlineData("powershell -NoProfile -Command \"Select-String -Path src/*.py -Pattern greet\"")]
    public void A_command_that_only_reads_is_allowed_without_asking(string command)
    {
        Assert.True(ReadOnlyCommands.IsReadOnly(command));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Remove-Item -Recurse src")]
    [InlineData("Get-ChildItem; Remove-Item x")]
    [InlineData("Get-Content a.txt > b.txt")]
    [InlineData("Get-ChildItem | ForEach-Object { Remove-Item $_ }")]
    [InlineData("git push")]
    [InlineData("git status --short")]
    [InlineData("git diff HEAD")]
    [InlineData("rg -n greeting src")]
    [InlineData("rg --pre=calc x .")]
    [InlineData("git grep -Ocalc x")]
    [InlineData("git diff --output=patch.txt")]
    [InlineData("git branch -D main")]
    [InlineData("pwsh -File script.ps1")]
    [InlineData("pwsh -EncodedCommand ZQBjAGgAbwA=")]
    [InlineData("cmd /c del x")]
    [InlineData("pwsh -Command 'pwsh -Command Remove-Item x'")]
    [InlineData("Get-Content log.txt -Wait")]
    [InlineData("python -m unittest")]
    [InlineData("npm test")]
    public void Anything_else_is_asked_about(string? command)
    {
        Assert.False(ReadOnlyCommands.IsReadOnly(command));
    }
}
