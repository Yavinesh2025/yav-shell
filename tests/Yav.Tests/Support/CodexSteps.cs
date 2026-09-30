using System.Text.Json.Nodes;

namespace Yav.Tests.Support;

/// <summary>Steps of a scripted turn that only the stand-in for the Codex app server plays.</summary>
public static partial class Step
{
    /// <summary>
    /// The agent announces a change of files and then asks for approval of it by the item, as Codex does: the
    /// request itself names no file. With <paramref name="announced"/>, the item is first announced with those
    /// changes and then updated to <paramref name="changes"/> before the question.
    /// </summary>
    public static JsonObject FileChangeApproval(
        JsonObject[] changes,
        string? reason = null,
        string? grantRoot = null,
        JsonObject[]? announced = null,
        bool announce = true)
    {
        var step = new JsonObject
        {
            ["type"] = "approval",
            ["kind"] = "file",
            ["reason"] = reason,
            ["grantRoot"] = grantRoot,
            ["changes"] = new JsonArray(changes.Select(c => (JsonNode?)c).ToArray()),
            ["announce"] = announce,
        };
        if (announced is not null)
        {
            step["announced"] = new JsonArray(announced.Select(c => (JsonNode?)c).ToArray());
        }

        return step;
    }

    /// <summary>
    /// One file of a change, relative to the agent's working directory. The kind is "add", "update" or "delete", or
    /// any other word, for a kind of change this version of YAV does not know.
    /// </summary>
    public static JsonObject Changed(string path, string kind = "update", string? movedTo = null) =>
        new() { ["path"] = path, ["kind"] = kind, ["movedTo"] = movedTo };

    /// <summary>
    /// A request to approve a command, of the kind the protocol names ("command" or "writeStdin"). The command
    /// may be missing, and <paramref name="networkHost"/> makes it a request for network access.
    /// </summary>
    public static JsonObject CommandApproval(string? command, string approvalKind = "command", string? networkHost = null) => new()
    {
        ["type"] = "approval",
        ["kind"] = "command",
        ["command"] = command,
        ["approvalKind"] = approvalKind,
        ["networkHost"] = networkHost,
    };

    /// <summary>
    /// The agent asks for more than its sandbox gives, as a request for permissions. <paramref name="onAccept"/> is
    /// played when the answer grants anything.
    /// </summary>
    public static JsonObject PermissionsApproval(JsonObject permissions, JsonObject[]? onAccept = null, string? reason = null)
    {
        var step = Approval(string.Empty, onAccept: onAccept, reason: reason, kind: "permissions");
        step["permissions"] = permissions;
        return step;
    }

    /// <summary>A rate-limit update as Codex sends it: only the values it has, for one limit.</summary>
    public static JsonObject RateLimitUpdate(JsonObject rateLimits) => new() { ["type"] = "rateLimits", ["rateLimits"] = rateLimits };

    public static JsonObject LimitWindow(int usedPercent, int windowMinutes = 300, long resetsAt = 1_790_000_000) =>
        new() { ["usedPercent"] = usedPercent, ["windowDurationMins"] = windowMinutes, ["resetsAt"] = resetsAt };

    /// <summary>
    /// The settings of the thread change while it is open, as another client of the same Codex could change them.
    /// With <paramref name="withoutApprovalsReviewer"/> the notification does not say who decides about access.
    /// </summary>
    public static JsonObject SettingsUpdated(
        string? approvalsReviewer = null, string? model = null, string? effort = null, JsonObject? sandboxPolicy = null, bool withoutApprovalsReviewer = false) => new()
    {
        ["type"] = "settingsUpdated",
        ["approvalsReviewer"] = approvalsReviewer,
        ["model"] = model,
        ["effort"] = effort,
        ["sandboxPolicy"] = sandboxPolicy,
        ["withoutApprovalsReviewer"] = withoutApprovalsReviewer,
    };

    /// <summary>A warning that names no conversation.</summary>
    public static JsonObject WarningForAll(string message) => new() { ["type"] = "warningForAll", ["message"] = message };

    /// <summary>
    /// A notification of any method, sent as it is. With <paramref name="forThread"/> the thread (and, in a turn,
    /// the turn) it belongs to is filled in.
    /// </summary>
    public static JsonObject Notification(string method, JsonObject? parameters = null, bool forThread = false) => new()
    {
        ["type"] = "notification",
        ["method"] = method,
        ["params"] = parameters ?? new JsonObject(),
        ["forThread"] = forThread,
    };

    /// <summary>Codex says that the account it works with changed: the kind of sign-in, and the plan for a ChatGPT sign-in.</summary>
    public static JsonObject AccountUpdated(string? authMode, string? planType = null) =>
        Notification("account/updated", new JsonObject { ["authMode"] = authMode, ["planType"] = planType });

    /// <summary>Codex warns that its Windows sandbox cannot protect folders that everybody may write to.</summary>
    public static JsonObject WorldWritableWarning(string[] samplePaths, int extraCount = 0, bool failedScan = false) => Notification(
        "windows/worldWritableWarning",
        new JsonObject
        {
            ["samplePaths"] = new JsonArray(samplePaths.Select(p => (JsonNode?)p).ToArray()),
            ["extraCount"] = extraCount,
            ["failedScan"] = failedScan,
        });

    public static JsonObject ConfigWarning(string summary, string? details = null, string? path = null) =>
        new() { ["type"] = "configWarning", ["summary"] = summary, ["details"] = details, ["path"] = path };

    public static JsonObject DeprecationNotice(string summary, string? details = null) =>
        new() { ["type"] = "deprecationNotice", ["summary"] = summary, ["details"] = details };

    /// <summary>
    /// The model starts a sub-agent, which works in a thread of its own and says <paramref name="text"/> there.
    /// With <paramref name="resumed"/> the same sub-agent is announced once more, as when it is resumed.
    /// </summary>
    /// <param name="asks">How many approvals the sub-agent asks for in its own thread, each after the answer to the one before.</param>
    /// <param name="autoReview">A notification of Codex's own reviewer about a decision in the sub-agent's thread, as in <see cref="AutoReview"/>.</param>
    public static JsonObject SubAgent(string text, bool resumed = false, int asks = 0, string? autoReview = null) =>
        new() { ["type"] = "subAgent", ["text"] = text, ["resumed"] = resumed, ["asks"] = asks, ["autoReview"] = autoReview };

    /// <summary>
    /// The stand-in closes its standard input, so that what YAV writes to it afterwards fails as it does when the
    /// agent is gone. The scenario must say "deafAfterTurnStart": an input that is being read cannot be closed.
    /// </summary>
    public static JsonObject CloseInput() => new() { ["type"] = "closeInput" };

    /// <summary>
    /// A request of the agent of any method. The thread and the turn are filled in unless <paramref name="forThread"/>
    /// is false; the answer is waited for only with <paramref name="wait"/>.
    /// </summary>
    public static JsonObject ServerRequest(string method, JsonObject? parameters = null, bool wait = false, bool forThread = true) => new()
    {
        ["type"] = "serverRequest",
        ["method"] = method,
        ["params"] = parameters ?? new JsonObject(),
        ["wait"] = wait,
        ["forThread"] = forThread,
    };

    /// <summary>
    /// Codex's own reviewer takes part in a decision about access: <paramref name="method"/> is one of
    /// "item/autoApprovalReview/started", "item/autoApprovalReview/completed",
    /// "autoApprovalReview/strictReviewRequired" and "guardianWarning".
    /// </summary>
    public static JsonObject AutoReview(string method, string command = "rm -rf build") =>
        new() { ["type"] = "autoReview", ["method"] = method, ["command"] = command };

    /// <summary>Codex closes the thread once the turn has ended: it unloads it, and nothing runs it any more.</summary>
    public static JsonObject ThreadClosed() => new() { ["type"] = "threadClosed" };

    /// <summary>The agent goes on only when the test creates a file of this name in the directory of the scenario.</summary>
    public static JsonObject WaitForSignal(string name) => new() { ["type"] = "waitForSignal", ["name"] = name };

    /// <summary>A request to approve a command whose answer the agent does not wait for.</summary>
    public static JsonObject UnansweredCommandApproval(string command) =>
        ServerRequest("item/commandExecution/requestApproval", new JsonObject { ["itemId"] = "item-unanswered", ["command"] = command, ["startedAtMs"] = 1_790_000_000_000 });

    /// <summary>An MCP server asks the user for input, in the form Codex passes it on.</summary>
    public static JsonObject Elicitation(string serverName, string message, bool wait = true) => ServerRequest(
        "mcpServer/elicitation/request",
        new JsonObject
        {
            ["serverName"] = serverName,
            ["mode"] = "form",
            ["message"] = message,
            ["requestedSchema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
        },
        wait);
}
