using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Yav.Coordinator;
using Yav.Core.Agents;
using Yav.Core.Profiles;
using Yav.Core.Runs;
using Yav.Core.Text;

namespace Yav.Console.Output;

/// <summary>The exit codes of 'yav run'. Every way a run can end has one of its own.</summary>
public static class ExitCodes
{
    public const int Success = 0;
    public const int DoctorFoundProblems = 1;
    public const int Blocked = 2;
    public const int ApprovalRequired = 3;
    public const int RateLimited = 4;
    public const int Failed = 5;
    public const int Interrupted = 6;
    public const int NeedsReconciliation = 7;
    public const int Invalid = 64;

    /// <param name="applied">Null when no apply was asked for; otherwise whether it succeeded.</param>
    public static int For(RunOutcome outcome, bool? applied) => outcome.Kind switch
    {
        RunOutcomeKind.ReadyToApply => applied == false ? Blocked : Success,
        RunOutcomeKind.Completed => Success,
        RunOutcomeKind.Blocked => Blocked,
        RunOutcomeKind.ApprovalRequired => ApprovalRequired,
        RunOutcomeKind.RateLimited => RateLimited,
        RunOutcomeKind.Interrupted => Interrupted,
        RunOutcomeKind.NeedsReconciliation => NeedsReconciliation,
        _ => Failed,
    };
}

/// <summary>
/// What 'yav run --json' writes to standard output: one JSON object per line and nothing else. Text that
/// comes from agents and tools is cleaned of control sequences, because whoever reads the JSON may print it.
/// What says what an agent asked for or ran is the exception: it is passed on unchanged, so that a reader gets
/// what the agent asked for and not a shortened form, and every character a terminal would not show is escaped.
/// </summary>
public static class JsonOutput
{
    private static readonly JsonWriterOptions Options = new()
    {
        Indented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string? Event(RunEvent runEvent)
    {
        return Write(writer =>
        {
            writer.WriteString("type", "event");
            writer.WriteString("runId", runEvent.RunId);
            writer.WriteString("at", runEvent.At.ToString("O", System.Globalization.CultureInfo.InvariantCulture));

            switch (runEvent)
            {
                case RunStarted e:
                    writer.WriteString("event", "run.started");
                    writer.WriteString("taskId", e.TaskId);
                    Text(writer, "request", e.RequestText);
                    writer.WriteString("profileHash", e.ProfileHash);
                    writer.WriteString("policy", e.Profile.Policy.Label);
                    writer.WriteBoolean("qualityLock", e.Profile.Policy.QualityLock);
                    writer.WriteBoolean("followUp", e.IsFollowUp);
                    Role(writer, "modelA", e.Profile.Implementer);
                    Role(writer, "modelB", e.Profile.Reviewer);
                    break;

                case StateChanged e:
                    writer.WriteString("event", "state");
                    writer.WriteString("from", Snake(e.From.ToString()));
                    writer.WriteString("to", Snake(e.To.ToString()));
                    Text(writer, "reason", e.Reason);
                    break;

                case StageNote e:
                    writer.WriteString("event", "note");
                    writer.WriteString("stage", e.Stage);
                    writer.WriteString("level", Snake(e.Level.ToString()));
                    Text(writer, "message", e.Message);
                    break;

                case SettingConfirmed e:
                    writer.WriteString("event", "setting");
                    writer.WriteString("role", Snake(e.Confirmation.Role.ToString()));
                    writer.WriteString("setting", e.Confirmation.Setting);
                    Text(writer, "requested", e.Confirmation.Requested);
                    Text(writer, "effective", e.Confirmation.Effective);
                    writer.WriteString("status", Snake(e.Confirmation.Status.ToString()));
                    Text(writer, "source", e.Confirmation.Source);
                    break;

                case CandidateFrozen e:
                    writer.WriteString("event", "candidate");
                    Candidate(writer, e.Candidate);
                    break;

                case ReviewCompleted e:
                    writer.WriteString("event", "review");
                    Review(writer, e.Review);
                    break;

                case GateStarted e:
                    writer.WriteString("event", "gate.started");
                    writer.WriteString("gate", e.Gate.Id);
                    Text(writer, "title", e.Gate.Title);
                    Text(writer, "command", e.Gate.DisplayCommand);
                    Text(writer, "workingDirectory", e.WorkingDirectory);
                    break;

                case GateOutput e:
                    writer.WriteString("event", "gate.output");
                    writer.WriteString("gate", e.GateId);
                    Text(writer, "line", e.Line);
                    break;

                case GateCompleted e:
                    writer.WriteString("event", "gate.result");
                    Gate(writer, e.Result);
                    break;

                case AcceptanceEvaluated e:
                    writer.WriteString("event", "acceptance");
                    writer.WriteString("candidateFingerprint", e.Candidate.Fingerprint);
                    Decision(writer, e.Decision);
                    break;

                case RepairStarted e:
                    writer.WriteString("event", "repair");
                    writer.WriteNumber("cycle", e.Cycle);
                    writer.WriteNumber("maxCycles", e.MaxCycles);
                    writer.WriteNumber("findings", e.Findings);
                    writer.WriteNumber("failedChecks", e.FailedGates);
                    break;

                case RunFinished e:
                    writer.WriteString("event", "run.finished");
                    writer.WriteString("state", Snake(e.State.ToString()));
                    writer.WriteString("disposition", Snake(e.Disposition.ToString()));
                    Text(writer, "reason", e.Reason);
                    break;

                case AgentActivity e:
                    Agent(writer, e.Role, e.Event);
                    break;

                default:
                    writer.WriteString("event", "unknown");
                    break;
            }
        });
    }

    public static string Result(RunOutcome outcome, DeliveryOutcome? delivery)
    {
        bool? applied = delivery?.Succeeded;
        return Write(writer =>
        {
            writer.WriteString("type", "result");
            writer.WriteString("outcome", Snake(outcome.Kind.ToString()));
            writer.WriteNumber("exitCode", ExitCodes.For(outcome, applied));
            writer.WriteString("runId", outcome.RunId);
            Text(writer, "taskId", outcome.TaskId);
            writer.WriteString("state", Snake(outcome.State.ToString()));
            Text(writer, "reason", outcome.Reason);
            Text(writer, "finalMessage", outcome.FinalMessage);

            if (outcome.Candidate is { } candidate)
            {
                writer.WriteStartObject("candidate");
                Candidate(writer, candidate);
                writer.WriteEndObject();
            }
            else
            {
                writer.WriteNull("candidate");
            }

            if (outcome.Decision is { } decision)
            {
                writer.WriteStartObject("acceptance");
                Decision(writer, decision);
                writer.WriteEndObject();
            }
            else
            {
                writer.WriteNull("acceptance");
            }

            writer.WriteStartArray("pendingApprovals");
            foreach (var approval in outcome.PendingApprovals)
            {
                writer.WriteStartObject();
                Approval(writer, approval);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            writer.WriteStartArray("problems");
            foreach (var problem in outcome.Problems)
            {
                writer.WriteStartObject();
                writer.WriteString("code", problem.Code);
                writer.WriteString("severity", Snake(problem.Severity.ToString()));
                if (problem.Role is { } role)
                {
                    writer.WriteString("role", Snake(role.ToString()));
                }
                else
                {
                    writer.WriteNull("role");
                }

                Text(writer, "message", problem.Message);
                Text(writer, "remedy", problem.Remedy);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            if (delivery is null)
            {
                writer.WriteNull("apply");
            }
            else
            {
                writer.WriteStartObject("apply");
                writer.WriteBoolean("applied", delivery.Succeeded);
                Text(writer, "message", delivery.Message);
                writer.WriteStartArray("conflicts");
                foreach (var conflict in delivery.Conflicts)
                {
                    writer.WriteStartObject();
                    Text(writer, "path", conflict.Path);
                    writer.WriteString("kind", Snake(conflict.Kind.ToString()));
                    Text(writer, "detail", conflict.Detail);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }
        })!;
    }

    public static string Failure(string message, int exitCode) => Write(writer =>
    {
        writer.WriteString("type", "result");
        writer.WriteString("outcome", exitCode == ExitCodes.Invalid ? "invalid" : "failed");
        writer.WriteNumber("exitCode", exitCode);
        Text(writer, "reason", message);
    })!;

    private static void Agent(Utf8JsonWriter writer, AgentRole role, AgentEvent agentEvent)
    {
        // Every line says what it is before it says anything else.
        void Begin(string name)
        {
            writer.WriteString("event", name);
            writer.WriteString("role", Snake(role.ToString()));
        }

        switch (agentEvent)
        {
            case SessionConfigured e:
                Begin("agent.configured");
                Text(writer, "sessionId", e.SessionId);
                Text(writer, "model", e.Effective.Model);
                Text(writer, "effort", e.Effective.Effort);
                Text(writer, "sandbox", e.Effective.Sandbox);
                Text(writer, "approvalPolicy", e.Effective.ApprovalPolicy);
                Text(writer, "serviceTier", e.Effective.ServiceTier);
                Text(writer, "credentialSource", e.Effective.CredentialSource);
                Text(writer, "account", e.Effective.Account?.Description);
                Text(writer, "agentVersion", e.Effective.AgentVersion);
                Text(writer, "source", e.Effective.Source);
                Text(writer, "effortSource", e.Effective.EffortSource);
                Text(writer, "approvalsReviewer", e.Effective.ApprovalsReviewer);
                if (e.Effective.Widening is { } widening)
                {
                    writer.WriteBoolean("networkAccess", widening.NetworkAccess);
                }
                else
                {
                    writer.WriteNull("networkAccess");
                }

                Strings(writer, "additionalWritableRoots", e.Effective.Widening?.AdditionalWritableRoots ?? []);
                Strings(writer, "instructionSources", e.Effective.InstructionSources);
                Strings(writer, "tools", e.Effective.Tools);
                break;

            case TurnStarted e:
                Begin("agent.turn.started");
                Text(writer, "turnId", e.TurnId);
                break;

            case AssistantTextDelta e:
                Begin("agent.delta");
                Text(writer, "itemId", e.ItemId);
                Text(writer, "text", e.Text);
                break;

            case AssistantMessage e:
                Begin("agent.message");
                Text(writer, "itemId", e.ItemId);
                writer.WriteString("phase", Snake(e.Phase.ToString()));
                Text(writer, "text", e.Text);
                break;

            case ReasoningSummary e:
                Begin("agent.reasoning_summary");
                Text(writer, "text", e.Text);
                break;

            case CommandStarted e:
                Begin("agent.command.started");
                Text(writer, "itemId", e.ItemId);
                Verbatim(writer, "command", e.Command);
                Verbatim(writer, "workingDirectory", e.WorkingDirectory);
                break;

            case CommandOutputDelta e:
                Begin("agent.command.output");
                Text(writer, "itemId", e.ItemId);
                Text(writer, "text", e.Text);
                break;

            case CommandCompleted e:
                Begin("agent.command.completed");
                Text(writer, "itemId", e.ItemId);
                Verbatim(writer, "command", e.Command);
                Number(writer, "exitCode", e.ExitCode);
                Number(writer, "durationMs", e.DurationMs);
                Text(writer, "status", e.Status);
                break;

            case FilesChanged e:
                Begin("agent.files");
                Text(writer, "status", e.Status);
                writer.WriteStartArray("changes");
                foreach (var change in e.Changes)
                {
                    writer.WriteStartObject();
                    Text(writer, "path", change.Path);
                    writer.WriteString("kind", Snake(change.Kind.ToString()));
                    Text(writer, "movedTo", change.MovedTo);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                break;

            case ToolActivity e:
                Begin("agent.tool");
                Text(writer, "tool", e.Tool);
                Text(writer, "summary", e.Summary);
                Text(writer, "status", e.Status);
                if (e.Path is not null)
                {
                    Text(writer, "path", e.Path);
                }

                break;

            case ApprovalRequested e:
                Begin("approval.requested");
                Approval(writer, e.Request);
                break;

            case ApprovalWithdrawn e:
                Begin("approval.withdrawn");
                Text(writer, "approvalId", e.ApprovalId);
                break;

            case UsageUpdated e:
                Begin("agent.usage");
                Text(writer, "model", e.Usage.Model);
                writer.WriteString("scope", Snake(e.Usage.Scope.ToString()));
                writer.WriteStartObject("tokens");
                Number(writer, "uncachedInput", e.Usage.Tokens.UncachedInput);
                Number(writer, "cacheRead", e.Usage.Tokens.CacheRead);
                Number(writer, "cacheWrite", e.Usage.Tokens.CacheWrite);
                Number(writer, "output", e.Usage.Tokens.Output);
                Number(writer, "reasoningWithinOutput", e.Usage.Tokens.ReasoningWithinOutput);
                Number(writer, "total", e.Usage.Tokens.Total);
                writer.WriteEndObject();
                if (e.Usage.ProviderCostUsd is { } cost)
                {
                    writer.WriteNumber("costUsd", cost);
                }
                else
                {
                    writer.WriteNull("costUsd");
                }

                writer.WriteString("costProvenance", Snake(e.Usage.CostProvenance.ToString()));
                Text(writer, "source", e.Usage.Source);
                break;

            case RateLimitUpdated e:
                Begin("agent.rate_limits");
                Text(writer, "plan", e.Snapshot.PlanType);
                Window(writer, "primary", e.Snapshot.Primary);
                Window(writer, "secondary", e.Snapshot.Secondary);
                Text(writer, "source", e.Snapshot.Source);
                break;

            case ModelRerouted e:
                Begin("agent.rerouted");
                Text(writer, "from", e.FromModel);
                Text(writer, "to", e.ToModel);
                Text(writer, "reason", e.Reason);
                break;

            case ProviderRetry e:
                Begin("agent.retry");
                writer.WriteNumber("attempt", e.Attempt);
                Number(writer, "maxAttempts", e.MaxAttempts);
                Number(writer, "delayMs", e.DelayMs);
                Text(writer, "reason", e.Reason);
                break;

            case AgentNotice e:
                Begin("agent.notice");
                writer.WriteBoolean("warning", e.IsWarning);
                Text(writer, "message", e.Message);
                break;

            case AgentError e:
                Begin("agent.error");
                Text(writer, "message", e.Message);
                Text(writer, "code", e.Code);
                writer.WriteBoolean("willRetry", e.WillRetry);
                break;

            case TurnCompleted e:
                Begin("agent.turn.completed");
                writer.WriteString("outcome", Snake(e.Outcome.ToString()));
                Text(writer, "finalMessage", e.FinalMessage);
                Text(writer, "errorCode", e.ErrorCode);
                Text(writer, "errorMessage", e.ErrorMessage);
                writer.WriteStartArray("denials");
                foreach (var denial in e.Denials)
                {
                    writer.WriteStartObject();
                    Text(writer, "tool", denial.Tool);
                    Text(writer, "summary", denial.Summary);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                break;

            case SessionEnded e:
                Begin("agent.ended");
                Number(writer, "exitCode", e.ExitCode);
                Text(writer, "reason", e.Reason);
                break;

            default:
                Begin("agent.unknown");
                break;
        }
    }

    private static void Role(Utf8JsonWriter writer, string name, RoleProfile role)
    {
        writer.WriteStartObject(name);
        writer.WriteString("adapter", role.AdapterId);
        writer.WriteString("provider", role.Provider);
        Text(writer, "model", role.ModelId);
        Text(writer, "effort", role.RequestedEffort);
        writer.WriteString("sandbox", Snake(role.Sandbox.ToString()));
        Text(writer, "serviceTier", role.ServiceTier);
        Text(writer, "accountRoute", role.AccountRouteLabel);
        writer.WriteString("billing", Snake(role.Billing.ToString()));
        writer.WriteEndObject();
    }

    private static void Candidate(Utf8JsonWriter writer, Candidate candidate)
    {
        writer.WriteString("candidateId", candidate.CandidateId);
        writer.WriteNumber("sequence", candidate.Sequence);
        writer.WriteString("fingerprint", candidate.Fingerprint);
        writer.WriteString("baselineFingerprint", candidate.BaselineFingerprint);
        writer.WriteNumber("acceptanceVersion", candidate.AcceptanceVersion);
        writer.WriteStartArray("files");
        foreach (var file in candidate.Changes.Files)
        {
            writer.WriteStartObject();
            Text(writer, "path", file.Path);
            writer.WriteString("kind", Snake(file.Kind.ToString()));
            writer.WriteBoolean("binary", file.IsBinary);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        Strings(writer, "protectedPaths", candidate.ProtectedPathsTouched);
        Strings(writer, "existingTestsChanged", candidate.ExistingTestsTouched);
    }

    private static void Review(Utf8JsonWriter writer, ReviewResult review)
    {
        writer.WriteString("verdict", Snake(review.Verdict.ToString()));
        writer.WriteBoolean("valid", review.OutputValid);
        writer.WriteBoolean("sourceUnchanged", review.SourceUnchangedDuringReview);
        writer.WriteString("candidateFingerprint", review.Binding.CandidateFingerprint);
        Text(writer, "model", review.ReviewerModel);
        Text(writer, "summary", review.Summary);
        Text(writer, "coverage", review.Coverage);
        Strings(writer, "limitations", review.Limitations);
        Strings(writer, "validationErrors", review.ValidationErrors);
        writer.WriteStartArray("findings");
        foreach (var finding in review.Findings)
        {
            writer.WriteStartObject();
            writer.WriteString("id", finding.FindingId);
            writer.WriteString("severity", Snake(finding.Severity.ToString()));
            writer.WriteString("category", Snake(finding.Category.ToString()));
            writer.WriteBoolean("optional", finding.Optional);
            Text(writer, "file", finding.File);
            Number(writer, "line", finding.Line);
            Text(writer, "title", finding.Title);
            Text(writer, "failureScenario", finding.FailureScenario);
            Text(writer, "evidence", finding.Evidence);
            Text(writer, "suggestedCorrection", finding.SuggestedCorrection);
            Text(writer, "limitation", finding.Limitation);
            writer.WriteBoolean("locationVerified", finding.LocationVerified);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void Gate(Utf8JsonWriter writer, GateResult result)
    {
        writer.WriteString("gate", result.GateId);
        Text(writer, "title", result.GateTitle);
        writer.WriteString("status", Snake(result.Status.ToString()));
        Number(writer, "exitCode", result.ExitCode);
        Text(writer, "command", result.CommandLine);
        Text(writer, "workingDirectory", result.WorkingDirectory);
        writer.WriteNumber("durationMs", result.DurationMs);
        Text(writer, "outputPath", result.OutputPath);
        Text(writer, "limitation", result.Limitation);
        writer.WriteBoolean("required", result.Required);
        writer.WriteBoolean("baselineRun", result.IsBaselineRun);
        if (result.FailsOnBaseline is { } fails)
        {
            writer.WriteBoolean("failsOnBaseline", fails);
        }
        else
        {
            writer.WriteNull("failsOnBaseline");
        }

        writer.WriteString("candidateFingerprint", result.Binding.CandidateFingerprint);
    }

    private static void Decision(Utf8JsonWriter writer, AcceptanceDecision decision)
    {
        writer.WriteBoolean("accepted", decision.Accepted);
        writer.WriteStartArray("issues");
        foreach (var issue in decision.Issues)
        {
            writer.WriteStartObject();
            writer.WriteString("kind", Snake(issue.Kind.ToString()));
            writer.WriteString("resolution", Snake(issue.Resolution.ToString()));
            Text(writer, "message", issue.Message);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    /// <summary>What an agent asked for, as it asked for it.</summary>
    private static void Approval(Utf8JsonWriter writer, ApprovalRequest request)
    {
        Verbatim(writer, "approvalId", request.ApprovalId);
        writer.WriteString("kind", Snake(request.Kind.ToString()));
        Verbatim(writer, "title", request.Title);
        Verbatim(writer, "command", request.Command);
        Verbatim(writer, "workingDirectory", request.WorkingDirectory);
        Verbatim(writer, "reason", request.Reason);
        writer.WriteStartArray("details");
        foreach (var detail in request.Details)
        {
            writer.WriteRawValue(Quoted(detail));
        }

        writer.WriteEndArray();
    }

    /// <summary>Text that says what an agent asked for or ran, unchanged. Parsed, it is exactly what the agent sent.</summary>
    private static void Verbatim(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
            return;
        }

        writer.WritePropertyName(name);
        writer.WriteRawValue(Quoted(value));
    }

    /// <summary>
    /// A string of JSON. The writer escapes controls by itself; the characters a terminal would not show that it
    /// lets through, such as those that reorder text or take no room, are escaped here, so that a line that is
    /// printed hides nothing. Escaped or not, a reader of the JSON gets the same text.
    /// </summary>
    private static string Quoted(string value)
    {
        var encoded = JsonEncodedText.Encode(Whole(value), Options.Encoder).Value;
        var builder = new StringBuilder(encoded.Length + 16).Append('"');
        foreach (var c in encoded)
        {
            if (TerminalSanitizer.IsWrittenOut(c))
            {
                builder.Append("\\u").Append(((int)c).ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.Append('"').ToString();
    }

    /// <summary>A lone half of a character cannot be written as UTF-8. It becomes the replacement character, as the writer makes it.</summary>
    private static string Whole(string value)
    {
        if (!value.Any(char.IsSurrogate))
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                builder.Append(c).Append(value[++i]);
            }
            else
            {
                builder.Append(char.IsSurrogate(c) ? (char)0xFFFD : c);
            }
        }

        return builder.ToString();
    }

    private static void Window(Utf8JsonWriter writer, string name, RateLimitWindow? window)
    {
        if (window is null)
        {
            writer.WriteNull(name);
            return;
        }

        writer.WriteStartObject(name);
        Number(writer, "usedPercent", window.UsedPercent);
        Number(writer, "windowMinutes", window.WindowMinutes);
        if (window.ResetsAt is { } resets)
        {
            writer.WriteString("resetsAt", resets.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        }
        else
        {
            writer.WriteNull("resetsAt");
        }

        writer.WriteEndObject();
    }

    private static void Text(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, TerminalSanitizer.Clean(value));
        }
    }

    private static void Number(Utf8JsonWriter writer, string name, long? value)
    {
        if (value is { } number)
        {
            writer.WriteNumber(name, number);
        }
        else
        {
            // Not reported is not zero.
            writer.WriteNull(name);
        }
    }

    private static void Number(Utf8JsonWriter writer, string name, int? value) => Number(writer, name, (long?)value);

    private static void Strings(Utf8JsonWriter writer, string name, IEnumerable<string> values)
    {
        writer.WriteStartArray(name);
        foreach (var value in values)
        {
            writer.WriteStringValue(TerminalSanitizer.Clean(value));
        }

        writer.WriteEndArray();
    }

    private static string Write(Action<Utf8JsonWriter> body)
    {
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(buffer, Options))
        {
            writer.WriteStartObject();
            body(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>"ReadyToApply" becomes "ready_to_apply".</summary>
    internal static string Snake(string name)
    {
        var builder = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0)
                {
                    builder.Append('_');
                }

                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}
