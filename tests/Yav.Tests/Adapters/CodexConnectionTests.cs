using System.Threading.Channels;
using Yav.Adapters;
using Yav.Adapters.Protocol;

namespace Yav.Tests.Adapters;

/// <summary>The JSON-RPC connection to the Codex app server, with a process whose every line the test decides.</summary>
public class CodexConnectionTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task A_message_that_cannot_be_handled_is_reported_and_the_next_one_is_still_handled()
    {
        var process = new CodexScriptedProcess();
        var problems = new List<string>();
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var connection = new JsonRpcConnection(process, TimeSpan.FromMinutes(5), new DiagnosticTail())
        {
            OnNotification = (method, _) =>
            {
                if (method == "first")
                {
                    // What a session does when it publishes to a channel that was completed in the meantime.
                    throw new ChannelClosedException();
                }

                second.TrySetResult();
                return ValueTask.CompletedTask;
            },
            OnProtocolProblem = problem =>
            {
                lock (problems)
                {
                    problems.Add(problem);
                }

                return ValueTask.CompletedTask;
            },
        };

        await process.SayAsync("""{"method":"first","params":{}}""");
        await process.SayAsync("""{"method":"second","params":{}}""");

        await second.Task.WaitAsync(Patience);
        lock (problems)
        {
            var problem = Assert.Single(problems);
            Assert.Contains("'first'", problem, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task When_reading_fails_in_an_unexpected_way_open_requests_fail_and_the_end_is_reported()
    {
        var output = new CodexBrokenOutput();
        var process = new CodexScriptedProcess(output);
        var closed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var connection = new JsonRpcConnection(process, TimeSpan.FromMinutes(5), new DiagnosticTail())
        {
            OnClosed = (_, description) =>
            {
                closed.TrySetResult(description);
                return ValueTask.CompletedTask;
            },
        };

        var request = connection.RequestAsync("account/read", null, CancellationToken.None);
        Assert.Equal("account/read", (await process.HeardAsync())["method"]!.GetValue<string>());
        output.Break();

        var error = await Assert.ThrowsAsync<AgentProtocolException>(() => request.WaitAsync(Patience));
        Assert.Contains("unexpected way", error.Message, StringComparison.Ordinal);
        Assert.Contains("unexpected way", await closed.Task.WaitAsync(Patience), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_request_made_after_the_agent_ended_fails_at_once()
    {
        var process = new CodexScriptedProcess();
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var connection = new JsonRpcConnection(process, TimeSpan.FromMinutes(5), new DiagnosticTail())
        {
            OnClosed = (_, _) =>
            {
                closed.TrySetResult();
                return ValueTask.CompletedTask;
            },
        };

        process.Exit(3);
        await closed.Task.WaitAsync(Patience);

        await Assert.ThrowsAsync<AgentProtocolException>(() => connection.RequestAsync("account/read", null, CancellationToken.None).WaitAsync(Patience));
    }

    [Fact]
    public async Task What_is_done_with_an_answer_is_done_before_the_message_that_follows_it_is_handled()
    {
        var process = new CodexScriptedProcess();
        var order = new List<string>();
        var followed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var connection = new JsonRpcConnection(process, TimeSpan.FromMinutes(5), new DiagnosticTail())
        {
            OnNotification = (method, _) =>
            {
                lock (order)
                {
                    order.Add(method);
                }

                followed.TrySetResult();
                return ValueTask.CompletedTask;
            },
        };

        var request = connection.RequestAsync(
            "account/rateLimits/read",
            null,
            CancellationToken.None,
            onAnswer: _ =>
            {
                lock (order)
                {
                    order.Add("answer");
                }
            });
        var id = (await process.HeardAsync())["id"]!.ToJsonString();

        // Both arrive in one piece, as Codex's buffered output can deliver them.
        await process.SayAsync("""{"id":""" + id + ""","result":{}}""" + "\n" + """{"method":"account/rateLimits/updated","params":{}}""");
        await followed.Task.WaitAsync(Patience);
        await request.WaitAsync(Patience);

        lock (order)
        {
            Assert.Equal(["answer", "account/rateLimits/updated"], order);
        }
    }

    [Fact]
    public async Task Closing_the_connection_does_not_throw_what_a_handler_threw()
    {
        var process = new CodexScriptedProcess();
        var handled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = new JsonRpcConnection(process, TimeSpan.FromMinutes(5), new DiagnosticTail())
        {
            OnNotification = (_, _) =>
            {
                handled.TrySetResult();
                throw new InvalidOperationException("The handler broke.");
            },
        };

        await process.SayAsync("""{"method":"any","params":{}}""");
        await handled.Task.WaitAsync(Patience);

        await connection.DisposeAsync();
    }

    [Fact]
    public async Task What_the_closing_handler_throws_does_not_escape_the_reader()
    {
        var process = new CodexScriptedProcess();
        var connection = new JsonRpcConnection(process, TimeSpan.FromMinutes(5), new DiagnosticTail())
        {
            OnClosed = (_, _) => throw new ChannelClosedException(),
        };

        process.Exit(0);

        // The reader ends by itself; disposing waits for it and must not see what the handler threw.
        await connection.DisposeAsync();
    }
}
