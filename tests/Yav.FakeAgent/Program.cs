using System.Text;
using Yav.FakeAgent;

// Test fixture. See the project file for what this is and is not.
Console.InputEncoding = new UTF8Encoding(false);
Console.OutputEncoding = new UTF8Encoding(false);

if (args.Length == 0)
{
    Console.Error.WriteLine("yav-fake-agent: no mode given");
    return 64;
}

try
{
    return args[0] switch
    {
        "tool" => await ToolMode.RunAsync(args[1..]),
        "--version" or "-V" or "-v" => Identity.PrintVersion(),
        "login" => Identity.PrintCodexLogin(args[1..]),
        "auth" => Identity.PrintClaudeAuth(args[1..]),
        "app-server" => await CodexAppServer.RunAsync(args[1..]),
        "exec" => await CodexExec.RunAsync(args[1..]),
        _ when args.Contains("-p") || args.Contains("--print") => await ClaudeStream.RunAsync(args),
        _ => Unknown(args[0]),
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine("yav-fake-agent: " + ex);
    return 70;
}

static int Unknown(string mode)
{
    Console.Error.WriteLine($"yav-fake-agent: unknown mode '{mode}'");
    return 64;
}
