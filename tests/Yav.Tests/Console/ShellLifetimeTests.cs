using System.Collections.Concurrent;
using Yav.Console.Shell;
using Yav.Coordinator;
using Yav.Core.Runs;
using Yav.Tests.Support;

namespace Yav.Tests.Console;

/// <summary>What the shell keeps while a run is active and lets go of when it ends, and what it says only once.</summary>
public class ShellLifetimeTests
{
    private const string Task = "Make the app say fixed.";

    [Fact]
    public async Task Lines_entered_while_a_run_is_active_leave_nothing_behind_that_fails_when_the_run_ends()
    {
        var failures = new ConcurrentQueue<Exception>();
        void Unobserved(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            foreach (var exception in e.Exception.InnerExceptions)
            {
                if (exception is ObjectDisposedException && exception.StackTrace?.Contains(nameof(InteractiveShell), StringComparison.Ordinal) == true)
                {
                    failures.Enqueue(exception);
                }
            }
        }

        TaskScheduler.UnobservedTaskException += Unobserved;
        try
        {
            await using (var shell = new ShellHarness())
            {
                shell.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
                shell.Agents.ImplementerTurn(Step.Write("src/app.txt", "half\n"), Step.Hang());
                shell.Start();
                await shell.WaitForPromptAsync();
                shell.Enter(Task);
                await shell.WaitForAsync("update ");

                // Each line is read while the run is active, and each read is woken when the run ends.
                await shell.EnterAndWaitAsync("/status");
                await shell.EnterAndWaitAsync("/queue");
                shell.Enter("/stop");
                await shell.WaitForRunToEndAsync();
                Assert.Equal(0, await shell.ExitAsync());
            }

            for (var i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            Assert.Empty(failures);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= Unobserved;
        }
    }

    [Fact]
    public async Task Control_c_that_arrives_as_the_run_ends_stops_nothing_and_fails_nothing()
    {
        await using var shell = new ShellHarness().WithPassingRun();
        shell.Start();
        await shell.WaitForPromptAsync();
        shell.Enter(Task);
        ActiveRun? run = null;
        await shell.WaitUntilAsync(() => (run ??= shell.Shell.Session.Active) is not null, "the run to become active");
        await shell.WaitForRunToEndAsync();
        Assert.Equal(0, await shell.ExitAsync());

        // Control+C arrives on a thread of its own. It found the run just before the shell let go of it.
        shell.Shell.Session.Active = run;
        var stopped = Record.Exception(() => shell.Shell.Interrupt());

        Assert.Null(stopped);
        Assert.False(run!.StopRequested);
        shell.AssertDoesNotShow("Asking the agent to stop.");
    }

    [Fact]
    public async Task A_run_that_is_refused_because_another_run_is_active_says_why_once()
    {
        await using var shell = new ShellHarness();
        shell.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
        shell.Agents.ImplementerTurn(Step.Write("src/app.txt", "half\n"), Step.Hang());
        shell.Start();
        await shell.WaitForPromptAsync();

        // A run of the project that this shell did not start, as another window of YAV starts it.
        using var stop = new CancellationTokenSource();
        var other = System.Threading.Tasks.Task.Run(() => shell.Services.Coordinator.RunAsync(
            new RunRequest(shell.Project.Path, "Another request."),
            shell.Services.Configuration,
            new Yav.Console.Cli.DelegateObserver(_ => { }),
            new Yav.Console.Cli.NoApprovals(),
            stop.Token));
        await shell.WaitUntilAsync(() => shell.Services.Coordinator.ActiveRunFor(shell.Project.Path) is not null, "the other run");

        shell.Enter(Task);
        await shell.WaitForRunToEndAsync();

        Assert.Single(shell.Terminal.Lines, row => row.Contains("is active for this project", StringComparison.Ordinal));
        await stop.CancelAsync();
        await other.WaitAsync(TimeSpan.FromSeconds(90));
    }

    [Fact]
    public async Task Review_show_changes_nothing_and_is_not_refused_while_a_run_is_active()
    {
        await using var shell = new ShellHarness().WithPassingRun();
        shell.Agents.ImplementerTurn(Step.Write("README.md", "# App\nmore\n"), Step.Hang());
        shell.Start();
        await shell.WaitForPromptAsync();
        shell.Enter(Task);
        await shell.WaitForRunToEndAsync();
        shell.Enter("Also say more in the README.");
        await shell.WaitUntilAsync(() => shell.Terminal.Lines.Any(row => row.Contains("README.md", StringComparison.Ordinal)), "the second run at work");

        await shell.EnterAndWaitAsync("/review show");

        shell.AssertDoesNotShow("/review waits until the active run has ended");
        shell.AssertShows("by model-b (codex-app-server): Pass: 0 blocking finding(s), 0 suggestion(s)");
        Assert.NotNull(shell.Shell.Session.Active);
        shell.Enter("/stop");
        await shell.WaitForRunToEndAsync();
    }
}
