using System.Diagnostics;
using System.Text;
using Yav.Core.Ports;
using Yav.Platform.Processes;

namespace Yav.Tests.Support;

/// <summary>
/// A temporary directory whose name contains a space and non-ASCII characters, so every file-based test
/// also exercises the paths that most often break on Windows.
/// </summary>
public sealed class TempDirectory : IDisposable
{
    public TempDirectory(string? label = null)
    {
        var name = $"yav tëst 日本 {label ?? "dir"} {Guid.NewGuid().ToString("N")[..8]}";
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "yav-tests", name);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string relative) => System.IO.Path.Combine(Path, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

    public string Write(string relative, string content)
    {
        var path = File(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    public string WriteBytes(string relative, byte[] content)
    {
        var path = File(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, content);
        return path;
    }

    public string Read(string relative) => System.IO.File.ReadAllText(File(relative));

    public bool Exists(string relative) => System.IO.File.Exists(File(relative));

    public string CreateDirectory(string relative)
    {
        var path = File(relative);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Makes a junction in this directory that leads to the target, as <c>mklink /J</c> does. Returns its path.</summary>
    public string Junction(string relative, string target)
    {
        var link = File(relative);
        using var made = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            ArgumentList = { "/d", "/c", "mklink", "/J", link, target },
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        })!;
        made.StandardOutput.ReadToEnd();
        made.WaitForExit();
        if (made.ExitCode != 0 || !Directory.Exists(link))
        {
            throw new IOException("A junction could not be made: " + made.StandardError.ReadToEnd());
        }

        return link;
    }

    public void Dispose()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Remove(new DirectoryInfo(Path));
                }

                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100 * (attempt + 1));
            }
        }
    }

    private static void Remove(DirectoryInfo directory)
    {
        foreach (var entry in directory.EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0 }))
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                // A link is removed as a link. What it leads to is not this directory's, and it can lead back into it.
                entry.Delete();
            }
            else if (entry is DirectoryInfo inner)
            {
                Remove(inner);
            }
            else
            {
                // Git marks object files read-only, which would make the delete fail.
                entry.Attributes &= ~FileAttributes.ReadOnly;
                entry.Delete();
            }
        }

        directory.Delete();
    }
}

public static class Fixtures
{
    /// <summary>
    /// The version the product was built with, as three numbers. It is read from a library of the product, so a
    /// test that compares it with what the program says does not have to be changed with every version.
    /// </summary>
    public static string ProductVersion
    {
        get
        {
            var version = typeof(Yav.Core.Ids).Assembly
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .Cast<System.Reflection.AssemblyInformationalVersionAttribute>()
                .Single().InformationalVersion;
            return System.Text.RegularExpressions.Regex.IsMatch(version, @"^\d+\.\d+\.\d+$")
                ? version
                : throw new InvalidOperationException($"'{version}' is not a version of three numbers.");
        }
    }

    /// <summary>The scripted stand-in for an agent CLI, built next to the tests.</summary>
    public static string FakeAgent
    {
        get
        {
            var testBin = AppContext.BaseDirectory.TrimEnd(System.IO.Path.DirectorySeparatorChar);
            var configurationFolder = System.IO.Path.GetFileName(testBin);
            var binRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(testBin, "..", ".."));
            var path = System.IO.Path.Combine(binRoot, "Yav.FakeAgent", configurationFolder, "yav-fake-agent.exe");
            if (!System.IO.File.Exists(path))
            {
                throw new FileNotFoundException("The fake agent fixture was not built.", path);
            }

            return path;
        }
    }

    public static ProcessSpec Tool(string workingDirectory, params string[] arguments) =>
        new(FakeAgent, ["tool", .. arguments], workingDirectory);

    public static async Task<ProcessResult> RunToolAsync(string workingDirectory, params string[] arguments)
    {
        var runner = new ProcessRunner();
        return await runner.RunAsync(Tool(workingDirectory, arguments), new CaptureOptions(Timeout: TimeSpan.FromSeconds(30)), CancellationToken.None);
    }

    /// <summary>Decodes the output of the fixture's "echo-args" tool back into the arguments it received.</summary>
    public static string[] DecodeArguments(string standardOutput) =>
        standardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => System.Text.Json.JsonSerializer.Deserialize<string>(line)!)
            .ToArray();
}

/// <summary>A clock the test moves by hand.</summary>
public sealed class ManualClock : TimeProvider
{
    private DateTimeOffset _now;
    private long _ticks;

    public ManualClock(DateTimeOffset? start = null)
    {
        _now = start ?? new DateTimeOffset(2026, 9, 29, 10, 0, 0, TimeSpan.Zero);
    }

    public override DateTimeOffset GetUtcNow() => _now;

    public override long GetTimestamp() => _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public void Advance(TimeSpan by)
    {
        _now += by;
        _ticks += by.Ticks;
    }
}
