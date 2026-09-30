using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Yav.FakeAgent;

/// <summary>Small deterministic child-process behaviors used by the process tests.</summary>
internal static class ToolMode
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            return 64;
        }

        switch (args[0])
        {
            case "echo-args":
                // One JSON string per argument, so the test sees exactly what arrived.
                foreach (var argument in args[1..])
                {
                    Console.Out.WriteLine(JsonSerializer.Serialize(argument));
                }

                return 0;

            case "exit":
                return int.Parse(args[1]);

            case "keys":
                // Shows each key as the console delivers it to a program, until q is pressed.
                Console.TreatControlCAsInput = true;
                Console.Out.WriteLine("ready");
                while (true)
                {
                    var key = Console.ReadKey(intercept: true);
                    Console.Out.WriteLine($"key={key.Key} char=U+{(int)key.KeyChar:X4} modifiers={(int)key.Modifiers}");
                    if (key.KeyChar == 'q')
                    {
                        return 0;
                    }
                }

            case "sleep":
                await Task.Delay(TimeSpan.FromSeconds(double.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture)));
                return 0;

            case "env":
                Console.Out.WriteLine(JsonSerializer.Serialize(Environment.GetEnvironmentVariable(args[1])));
                return 0;

            case "cwd":
                Console.Out.WriteLine(JsonSerializer.Serialize(Environment.CurrentDirectory));
                return 0;

            case "stdin-echo":
            {
                using var input = Console.OpenStandardInput();
                using var output = Console.OpenStandardOutput();
                await input.CopyToAsync(output);
                return 0;
            }

            case "stdin-lines":
            {
                // Replies to each input line, which shows the pipe works in both directions while the process lives.
                string? line;
                while ((line = Console.In.ReadLine()) is not null)
                {
                    Console.Out.WriteLine("got:" + line);
                    Console.Out.Flush();
                }

                return 0;
            }

            case "emit-hex":
            {
                // Writes raw bytes in the given chunks with a pause between them, to split characters across reads.
                using var output = Console.OpenStandardOutput();
                foreach (var chunk in args[1..])
                {
                    var bytes = Convert.FromHexString(chunk);
                    await output.WriteAsync(bytes);
                    await output.FlushAsync();
                    await Task.Delay(40);
                }

                return 0;
            }

            case "lines":
            {
                var count = int.Parse(args[1]);
                var builder = new StringBuilder();
                for (var i = 1; i <= count; i++)
                {
                    builder.Append("line ").Append(i).Append('\n');
                }

                Console.Out.Write(builder.ToString());
                return 0;
            }

            case "stderr":
                Console.Error.WriteLine(args[1]);
                return args.Length > 2 ? int.Parse(args[2]) : 0;

            case "spawn-tree":
            {
                // Starts a child that outlives this process unless the job object ends it.
                var self = Environment.ProcessPath!;
                var start = new ProcessStartInfo(self) { UseShellExecute = false, CreateNoWindow = true };
                start.ArgumentList.Add("tool");
                start.ArgumentList.Add("sleep");
                start.ArgumentList.Add(args.Length > 1 ? args[1] : "120");
                using var child = Process.Start(start)!;
                Console.Out.WriteLine(child.Id);
                Console.Out.Flush();
                if (args.Length > 2 && args[2] == "exit-now")
                {
                    return 0;
                }

                await Task.Delay(TimeSpan.FromSeconds(120));
                return 0;
            }

            case "write-file":
                await File.WriteAllTextAsync(args[1], args[2], new UTF8Encoding(false));
                return 0;

            case "expect-text":
            {
                // Stands for a test suite: passes when the file contains the text.
                var path = Path.GetFullPath(args[1]);
                if (!File.Exists(path))
                {
                    Console.Out.WriteLine($"FAIL {args[1]} does not exist");
                    return 1;
                }

                var found = (await File.ReadAllTextAsync(path)).Contains(args[2], StringComparison.Ordinal);
                Console.Out.WriteLine(found ? $"PASS {args[1]} contains '{args[2]}'" : $"FAIL {args[1]} does not contain '{args[2]}'");
                return found ? 0 : 1;
            }

            case "expect-no-text":
            {
                // Stands for a regression suite: passes as long as the file does not contain the text.
                var path = Path.GetFullPath(args[1]);
                var found = File.Exists(path) && (await File.ReadAllTextAsync(path)).Contains(args[2], StringComparison.Ordinal);
                Console.Out.WriteLine(found ? $"FAIL {args[1]} contains '{args[2]}'" : $"PASS {args[1]} is free of '{args[2]}'");
                return found ? 1 : 0;
            }

            case "expect-then-write":
            {
                // A check that leaves something behind in the directory it runs in.
                await File.WriteAllTextAsync(args[1], args[2], new UTF8Encoding(false));
                Console.Out.WriteLine("PASS wrote " + args[1]);
                return 0;
            }

            default:
                Console.Error.WriteLine($"unknown tool '{args[0]}'");
                return 64;
        }
    }
}
