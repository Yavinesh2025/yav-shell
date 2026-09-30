using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Yav.Console.Composition;
using Yav.Console.Doctor;
using Yav.Console.Output;
using Yav.Core.Text;
using Yav.Platform.Consoles;

namespace Yav.Console.Cli;

/// <summary>'yav doctor': what YAV needs, and what it found.</summary>
public static class DoctorCommand
{
    public static async Task<int> ExecuteAsync(
        CliOptions options,
        AppServices services,
        ConsoleCapabilities console,
        TextWriter output,
        CancellationToken stop)
    {
        var project = options.ProjectPath is null ? Environment.CurrentDirectory : Path.GetFullPath(options.ProjectPath);
        var report = await DoctorChecks.CollectAsync(services, console, project, stop).ConfigureAwait(false);

        if (options.Json)
        {
            output.WriteLine(ToJson(report));
        }
        else
        {
            foreach (var line in ToText(report))
            {
                output.WriteLine(line);
            }
        }

        output.Flush();
        return report.HasProblems ? ExitCodes.DoctorFoundProblems : ExitCodes.Success;
    }

    public static IEnumerable<string> ToText(DoctorReport report)
    {
        yield return $"YAV Shell {AppServices.Version} - doctor";
        string? area = null;
        foreach (var check in report.Checks)
        {
            if (!string.Equals(area, check.Area, StringComparison.Ordinal))
            {
                area = check.Area;
                yield return string.Empty;
                yield return TerminalSanitizer.CleanSingleLine(area);
            }

            yield return $"  {Mark(check.Status),-8} {TerminalSanitizer.CleanSingleLine(check.Name)}: {TerminalSanitizer.CleanSingleLine(check.Detail)}";
            if (check.Remedy is not null)
            {
                yield return $"           -> {TerminalSanitizer.CleanSingleLine(check.Remedy)}";
            }
        }

        yield return string.Empty;
        yield return report.HasProblems
            ? $"{report.Count(CheckStatus.Problem)} problem(s), {report.Count(CheckStatus.Warning)} warning(s)."
            : $"No problems. {report.Count(CheckStatus.Warning)} warning(s).";
    }

    public static string Mark(CheckStatus status) => status switch
    {
        CheckStatus.Ok => "[ok]",
        CheckStatus.Info => "[info]",
        CheckStatus.Warning => "[warn]",
        _ => "[FAIL]",
    };

    public static string ToJson(DoctorReport report)
    {
        var buffer = new ArrayBufferWriter<byte>(2048);
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "doctor");
            writer.WriteString("version", AppServices.Version);
            writer.WriteString("at", report.At.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteBoolean("problems", report.HasProblems);
            writer.WriteStartArray("checks");
            foreach (var check in report.Checks)
            {
                writer.WriteStartObject();
                writer.WriteString("area", TerminalSanitizer.Clean(check.Area));
                writer.WriteString("name", TerminalSanitizer.Clean(check.Name));
                writer.WriteString("status", JsonOutput.Snake(check.Status.ToString()));
                writer.WriteString("detail", TerminalSanitizer.Clean(check.Detail));
                if (check.Remedy is null)
                {
                    writer.WriteNull("remedy");
                }
                else
                {
                    writer.WriteString("remedy", TerminalSanitizer.Clean(check.Remedy));
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
