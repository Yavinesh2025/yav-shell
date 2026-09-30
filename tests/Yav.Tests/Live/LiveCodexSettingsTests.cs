using System.Text.Json;
using Xunit.Abstractions;
using Yav.Adapters;
using Yav.Adapters.Protocol;
using Yav.Core.Agents;
using Yav.Core.Ports;
using Yav.Platform.Processes;
using Yav.Tests.Support;

namespace Yav.Tests.Live;

/// <summary>
/// Checks against the installed Codex that it reports who decides approvals, the directory a conversation works in
/// and what widens its sandbox in the way the adapter reads them. Only threads that are not kept are started and no
/// turn is sent, so no model is asked. Runs only with "scripts\test.ps1 -Live".
/// </summary>
[Trait("Category", "Live")]
public class LiveCodexSettingsTests(ITestOutputHelper output)
{
    [LiveFact]
    public async Task The_installed_codex_confirms_that_you_decide_approvals_and_says_what_widens_its_sandbox()
    {
        var runner = new ProcessRunner();
        var executable = runner.Resolve("codex");
        Assert.NotNull(executable);
        using var directory = new TempDirectory("live");

        // Marks the directory, so that the one Codex names can be recognized however it spells the path.
        var marker = "yav-live-" + Guid.NewGuid().ToString("N")[..8];
        directory.Write(marker, "marker");

        var process = runner.Start(new ProcessSpec(executable, ["app-server"], directory.Path));
        await using var connection = new JsonRpcConnection(process, TimeSpan.FromSeconds(60), new DiagnosticTail());
        await connection.RequestAsync(
            "initialize",
            writer =>
            {
                writer.WritePropertyName("clientInfo");
                writer.WriteStartObject();
                writer.WriteString("name", CodexAppServerAdapter.ClientName);
                writer.WriteString("title", "YAV Shell");
                writer.WriteString("version", "0.1.0-live-test");
                writer.WriteEndObject();
            },
            CancellationToken.None);
        await connection.NotifyAsync("initialized", CancellationToken.None);

        foreach (var (sandbox, approval) in new[] { ("read-only", "never"), ("workspace-write", "on-request") })
        {
            var result = await connection.RequestAsync(
                "thread/start",
                writer =>
                {
                    writer.WriteString("cwd", directory.Path);
                    writer.WriteString("sandbox", sandbox);
                    writer.WriteString("approvalPolicy", approval);
                    writer.WriteString("approvalsReviewer", "user");
                    writer.WriteString("developerInstructions", "Live settings test. No turn is sent.");

                    // Not written to disk, so nothing is left in the user's session list.
                    writer.WriteBoolean("ephemeral", true);
                },
                CancellationToken.None);

            var effective = CodexAppServerAdapter.ParseEffective(result, "thread/start", null);
            output.WriteLine(
                $"requested sandbox={sandbox} approval={approval} -> sandbox={effective.Sandbox} reviewer={effective.ApprovalsReviewer} "
                + $"network={effective.Widening?.NetworkAccess} roots=[{string.Join(";", effective.Widening?.AdditionalWritableRoots ?? Array.Empty<string>())}]");
            output.WriteLine("  raw reviewer: " + (result.TryGetProperty("approvalsReviewer", out var reviewer) ? reviewer.GetRawText() : "(none)"));
            output.WriteLine("  raw sandbox: " + (result.TryGetProperty("sandbox", out var raw) ? raw.GetRawText() : "(none)"));
            output.WriteLine($"  requested cwd: {directory.Path}");
            output.WriteLine("  raw cwd: " + (result.TryGetProperty("cwd", out var cwd) ? cwd.GetRawText() : "(none)"));

            // Codex names who decides; a response that does not is refused by the adapter.
            Assert.Equal(JsonValueKind.String, reviewer.ValueKind);
            Assert.Equal(CodexAppServerAdapter.UserReviewer, effective.ApprovalsReviewer);
            Assert.Null(CodexAppServerAdapter.RefusalFor(effective.ApprovalsReviewer));
            Assert.Null(CodexAppServerAdapter.SandboxRefusal(raw.ValueKind == JsonValueKind.Object ? raw : null));

            // Codex names the directory the conversation works in, as a full path, and it is the one asked for.
            // The spelling may differ (a short name, say); what it is printed as above shows how.
            Assert.Equal(JsonValueKind.String, cwd.ValueKind);
            Assert.NotNull(effective.WorkingDirectory);
            Assert.True(Path.IsPathFullyQualified(effective.WorkingDirectory));
            Assert.True(File.Exists(Path.Combine(effective.WorkingDirectory, marker)), $"Codex names '{effective.WorkingDirectory}', not '{directory.Path}'.");

            // What widens the sandbox is said, with full paths; a read-only sandbox names no folder to write.
            var widening = Assert.IsType<SandboxWidening>(effective.Widening);
            Assert.All(widening.AdditionalWritableRoots, root => Assert.True(Path.IsPathFullyQualified(root), $"'{root}' is not a full path."));
            if (sandbox == "read-only")
            {
                Assert.Empty(widening.AdditionalWritableRoots);
            }
        }
    }
}
