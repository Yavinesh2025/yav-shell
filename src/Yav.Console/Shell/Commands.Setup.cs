using System.Globalization;
using Yav.Adapters;
using Yav.Console.Rendering;
using Yav.Core.Agents;
using Yav.Core.Ports;
using Yav.Core.Profiles;
using Yav.Core.Runs;

namespace Yav.Console.Shell;

/// <summary>A request as it was sent, so that it can be sent again unchanged once what kept it from starting is settled.</summary>
internal sealed record SentRequest(string Text, string? TaskId, IReadOnlyList<string> Attachments, MechanicalEditRequest? Edit)
{
    /// <summary>How often the guided first run has sent the request again.</summary>
    public int Round { get; init; }

    /// <summary>What was settled for this request already, by <see cref="SetupProblems.Key"/>. It is not asked about a second time.</summary>
    public IReadOnlySet<string> Settled { get; init; } = new HashSet<string>(StringComparer.Ordinal);
}

/// <summary>
/// Which problems of a refused request whoever typed it can settle with an answer, and how often the request is
/// sent again. Everything else is said by the refusal itself, together with what to do about it.
/// </summary>
internal static class SetupProblems
{
    /// <summary>A request is sent again at most this often, and only after something was settled for it.</summary>
    public const int MaxRounds = 4;

    /// <summary>
    /// A model has to be chosen for a role, or chosen again because the agent does not list it or the adapter is
    /// unknown. An agent that is not found is not among them: choosing again does not install it, and the refusal
    /// says what to do.
    /// </summary>
    public static readonly IReadOnlyList<string> ModelCodes = ["model-a-missing", "model-b-missing", "single-model", "model-unavailable", "adapter-unknown"];

    private static readonly HashSet<string> Settleable = new(ModelCodes, StringComparer.Ordinal)
    {
        // The exact effort of a role.
        "effort-unranked", "effort-unsupported",

        // The account an agent works through.
        "auth-missing", "route-unacknowledged",

        // The project.
        "gates-missing", "workspace-gaps",
    };

    public static bool CanSettle(string code) => Settleable.Contains(code);

    /// <summary>
    /// What the guided first run does with a refused request: nothing, when no answer can settle what refused
    /// it; stop, when something settled for it before is there again, or when it was sent again too often; or
    /// ask for what is open.
    /// </summary>
    public static (SetupStep Step, IReadOnlyList<ProfileProblem> Problems) Next(IReadOnlyList<ProfileProblem> problems, IReadOnlySet<string> settled, int round)
    {
        var recurring = Recurring(problems, settled);
        if (recurring.Count > 0)
        {
            return (SetupStep.Recurring, recurring);
        }

        var open = Open(problems, settled);
        if (open.Count == 0)
        {
            return (SetupStep.Nothing, open);
        }

        return round >= MaxRounds ? (SetupStep.TooManyRounds, open) : (SetupStep.Ask, open);
    }

    /// <summary>What a settled problem is remembered by: its code, and the role it was about.</summary>
    public static string Key(ProfileProblem problem) => problem.Role is { } role ? $"{problem.Code}:{role}" : problem.Code;

    /// <summary>The blocking problems an answer can settle and that were not settled for this request before.</summary>
    public static IReadOnlyList<ProfileProblem> Open(IReadOnlyList<ProfileProblem> problems, IReadOnlySet<string> settled) =>
        problems.Where(p => p.Severity == ProblemSeverity.Blocking && CanSettle(p.Code) && !settled.Contains(Key(p))).ToList();

    /// <summary>The blocking problems that were settled for this request before and are there again.</summary>
    public static IReadOnlyList<ProfileProblem> Recurring(IReadOnlyList<ProfileProblem> problems, IReadOnlySet<string> settled) =>
        problems.Where(p => p.Severity == ProblemSeverity.Blocking && settled.Contains(Key(p))).ToList();
}

/// <summary>What <see cref="SetupProblems.Next"/> decides for a refused request.</summary>
internal enum SetupStep
{
    /// <summary>No answer can settle what refused it: the refusal says what to do.</summary>
    Nothing,

    /// <summary>Something settled for it a moment ago refuses it again: asking the same again would not help.</summary>
    Recurring,

    /// <summary>It was sent again as often as it is sent again by itself.</summary>
    TooManyRounds,

    /// <summary>What is open is asked for, and the request is sent again once it is settled.</summary>
    Ask,
}

/// <summary>
/// The guided first run. A request that was refused before anything began, for something only the user can
/// decide, is not simply refused: what is missing is asked right there, one question at a time, and recorded
/// exactly as the command for it records it. Then the same request is sent again. Nothing is decided for the
/// user: a model is chosen by its number, and everything that grants something needs a typed yes.
/// </summary>
public sealed partial class InteractiveShell
{
    private const string SetupStage = "SETUP";

    /// <summary>A question whose answer cannot be used is asked again at most so often.</summary>
    private const int Attempts = 3;

    /// <summary>The interface of each provider the guided first run offers models of, the one preferred first.</summary>
    private static readonly string[] AdapterPreference = [CodexAppServerAdapter.AdapterId, CodexExecAdapter.AdapterId, ClaudeCliAdapter.AdapterId];

    private enum Settling
    {
        NotNeeded,
        Settled,
        Declined,
    }

    /// <summary>A model an agent lists for the user's account, as the guided first run offers it.</summary>
    /// <param name="Experimental">The interface of the agent is labelled experimental, as /models labels it.</param>
    private sealed record OfferedModel(IAgentAdapter Adapter, ModelInfo Model, bool Experimental);

    /// <summary>
    /// When the request was refused before it began for something an answer can settle, asks for it and sends
    /// the request again. Not sent again when nobody can be asked, nothing can be settled here, or the user
    /// declined; <c>Settled</c> then holds what was settled before the guide stopped, whose remedies need not be
    /// said again.
    /// </summary>
    private async Task<(bool SentAgain, IReadOnlySet<string> Settled)> SettleAndSendAgainAsync(SentRequest sent, RunOutcome outcome, CancellationToken cancellationToken)
    {
        var settled = new HashSet<string>(StringComparer.Ordinal);
        if (!_input.CanAsk || _session.ProjectPath is not { } project || !RefusedBeforeItBegan(outcome))
        {
            return (false, settled);
        }

        var (step, problems) = SetupProblems.Next(outcome.Problems, sent.Settled, sent.Round);
        switch (step)
        {
            case SetupStep.Nothing:
                if (sent.Round > 0)
                {
                    KeepAttachments(sent);
                }

                return (false, settled);

            case SetupStep.Recurring:
                // Settled a moment ago and still in the way: asking the same again would not help.
                _ui.Warn("This was settled a moment ago, and the request is refused for it nonetheless: "
                    + string.Join(" ", problems.Select(p => p.Message)) + " It is not asked again; /doctor may say more.");
                KeepAttachments(sent);
                return (false, settled);

            case SetupStep.TooManyRounds:
                _ui.Warn($"The request was sent again {sent.Round} times and is still refused. It is not sent again by itself.");
                KeepAttachments(sent);
                return (false, settled);
        }

        _ui.Note(SetupStage, sent.Round == 0
            ? "Nothing was sent. The request needs a decision of yours first; it is sent as soon as that is made."
            : "Nothing was sent yet. One more thing needs a decision of yours.");
        bool settledAll;
        using (var question = BeginGuideQuestion(cancellationToken))
        {
            try
            {
                settledAll = await SettleAsync(problems, project, settled, question.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (question.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                // Control+C at a question, where it does not arrive as a key: nothing more is asked.
                settledAll = false;
            }
            catch (Exception ex) when (IsCommandFailure(ex))
            {
                // Recording an answer can fail as the command for it can, for example when a file cannot be written.
                // That must not take the shell with it, as a command that fails does not.
                _ui.Error($"{ex.GetType().Name}: {ex.Message}");
                settledAll = false;
            }
            finally
            {
                EndGuideQuestion(question);
            }
        }

        if (!settledAll)
        {
            _ui.Muted("The request was not sent. Enter it again to be asked again.");
            KeepAttachments(sent);
            return (false, settled);
        }

        if (settled.Count == 0)
        {
            // Nothing was asked, or no answer changed anything: sent again, it would be refused the same way.
            _ui.Muted("Nothing here could be settled by an answer, so the request was not sent again.");
            KeepAttachments(sent);
            return (false, settled);
        }

        _ui.Note(SetupStage, "Sending the request again.", NoteLevel.Success);
        var all = new HashSet<string>(sent.Settled, StringComparer.Ordinal);
        all.UnionWith(settled);
        await StartRunAsync(sent with { Round = sent.Round + 1, Settled = all }, cancellationToken).ConfigureAwait(false);
        return (true, settled);
    }

    /// <summary>
    /// The files attached to a request the guide did not send again: they stay attached to the next request, as
    /// they would have been had the request not been typed yet.
    /// </summary>
    private void KeepAttachments(SentRequest sent)
    {
        var back = sent.Attachments.Where(a => !_session.Attachments.Contains(a, StringComparer.OrdinalIgnoreCase)).ToList();
        if (back.Count == 0)
        {
            return;
        }

        _session.Attachments.AddRange(back);
        _ui.Muted(back.Count == 1
            ? "The file that was attached stays attached to the next request."
            : $"The {back.Count} files that were attached stay attached to the next request.");
    }

    /// <summary>
    /// A question of the guide is open: Control+C that arrives as a signal (plain input) cancels it, as the key
    /// does in the rich console, instead of ending YAV in the middle of it.
    /// </summary>
    private CancellationTokenSource BeginGuideQuestion(CancellationToken cancellationToken)
    {
        var question = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Volatile.Write(ref _guideQuestion, question);
        return question;
    }

    private void EndGuideQuestion(CancellationTokenSource question) => Interlocked.CompareExchange(ref _guideQuestion, null, question);

    /// <summary>True when the run was refused before anything began: nothing was stored, no candidate exists, and the refusal named its problems.</summary>
    private bool RefusedBeforeItBegan(RunOutcome outcome) =>
        outcome.Kind == RunOutcomeKind.Blocked
        && outcome.Candidate is null
        && outcome.Problems.Any(p => p.Severity == ProblemSeverity.Blocking)
        && (outcome.RunId.Length == 0 || _services.Database.FindRun(outcome.RunId) is null);

    /// <summary>
    /// Asks for what is open, in the order in which one answer depends on the other: models first, because the
    /// account and the effort belong to a model. Adds to <paramref name="settled"/> the keys of the problems a step
    /// settled, and only those: what no step settled is not counted as settled. False when the user declined.
    /// </summary>
    private async Task<bool> SettleAsync(IReadOnlyList<ProfileProblem> open, string project, HashSet<string> settled, CancellationToken cancellationToken)
    {
        // The effort and the route steps add the keys of what they settled themselves, role by role.
        var steps = new (Func<Task<Settling>> Step, IReadOnlyList<string> Codes)[]
        {
            (() => SettleModelsAsync(open, settled, cancellationToken), SetupProblems.ModelCodes),
            (() => SettleSignInAsync(open, cancellationToken), ["auth-missing"]),
            (() => SettleEffortsAsync(settled, cancellationToken), []),
            (() => SettleRoutesAsync(settled, cancellationToken), []),
            (() => SettleChecksAsync(open, project, cancellationToken), ["gates-missing"]),
            (() => SettleGapsAsync(open, project, cancellationToken), ["workspace-gaps"]),
        };

        foreach (var (step, codes) in steps)
        {
            var result = await step().ConfigureAwait(false);
            if (result == Settling.Declined)
            {
                return false;
            }

            if (result == Settling.Settled)
            {
                // Should it come back, it is not asked again.
                settled.UnionWith(open.Where(p => codes.Contains(p.Code, StringComparer.Ordinal)).Select(SetupProblems.Key));
            }
        }

        return true;
    }

    private async Task<Settling> SettleModelsAsync(IReadOnlyList<ProfileProblem> open, HashSet<string> settled, CancellationToken cancellationToken)
    {
        var settings = _services.Settings;
        bool Again(AgentRole role) => open.Any(p => p.Role == role && p.Code is "model-unavailable" or "adapter-unknown");
        var needA = settings.ModelA is null || Again(AgentRole.Implementer);
        var needB = settings.ModelB is null || Again(AgentRole.Reviewer) || open.Any(p => p.Code == "single-model");
        if (!needA && !needB)
        {
            return Settling.NotNeeded;
        }

        var offered = await OfferedModelsAsync(cancellationToken).ConfigureAwait(false);
        if (offered is null)
        {
            return Settling.Declined;
        }

        _ui.Note(SetupStage, needA && needB
            ? "Two models work on a request: Model A implements it, and Model B reviews the result. These are the models your agents list for your accounts:"
            : "These are the models your agents list for your accounts:");
        // Agent by agent, as /models shows them, and numbered through: the number is what the user types.
        var number = 0;
        foreach (var agent in offered.GroupBy(o => o.Adapter))
        {
            _ui.Say($"{agent.Key.DisplayName} ({agent.Key.Id}){(agent.First().Experimental ? " - experimental interface" : string.Empty)}", Tone.Accent);
            _ui.Table(
                ["#", "Model", "Effort values", ""],
                agent.Select(o => (IReadOnlyList<string>)
                [
                    (++number).ToString(CultureInfo.InvariantCulture),
                    o.Model.Id + (string.Equals(o.Model.DisplayName, o.Model.Id, StringComparison.OrdinalIgnoreCase) ? string.Empty : $" ({o.Model.DisplayName})"),
                    o.Model.SupportedEfforts.Count == 0 ? "none" : string.Join(", ", o.Model.SupportedEfforts),
                    o.Model.IsDefault ? "provider default" : string.Empty,
                ]).ToList());
        }

        _ui.Muted("YAV lists what the agents report. It does not rank the models and does not choose for you.");

        if (needA)
        {
            // Model B stays as it is: then Model A cannot be its model either, for the same reason as below.
            var kept = settings.QualityLock && settings.Strict && !needB ? settings.ModelB : null;
            _ui.Say("Model A implements the request, in an isolated copy of the project.");
            if (await AskForModelAsync("Model A", offered, kept, cancellationToken).ConfigureAwait(false) is not { } chosen)
            {
                return Settling.Declined;
            }

            RecordModel(AgentRole.Implementer, chosen.Adapter.Id, chosen.Model, chosen.Model.Id, guided: true);

            // Settled even when Model B is declined next: what was said about Model A is not what to do any more.
            settled.UnionWith(open.Where(p => p.Role == AgentRole.Implementer && SetupProblems.ModelCodes.Contains(p.Code)).Select(SetupProblems.Key));
        }

        if (needB)
        {
            // One model reviewing its own work is not the dual-model workflow, and with Quality Lock it does not run.
            var current = _services.Settings;
            var notThis = current.QualityLock && current.Strict ? current.ModelA : null;
            _ui.Say("Model B reviews what Model A made, in a conversation of its own, and cannot change the project.");
            if (await AskForModelAsync("Model B", offered, notThis, cancellationToken).ConfigureAwait(false) is not { } chosen)
            {
                return Settling.Declined;
            }

            RecordModel(AgentRole.Reviewer, chosen.Adapter.Id, chosen.Model, chosen.Model.Id, guided: true);
        }

        return Settling.Settled;
    }

    /// <summary>
    /// The models of every provider whose agent is installed, signed in and lists models, through the interface
    /// preferred for the shell. When no agent is signed in, the provider's own sign-in is offered first. Null when
    /// nothing can be offered; what to do then was said.
    /// </summary>
    private async Task<List<OfferedModel>?> OfferedModelsAsync(CancellationToken cancellationToken)
    {
        if (_services.Adapters.Count == 0)
        {
            _ui.Warn("Every adapter is turned off in the settings, so no model can be offered. /settings adapter <id> on turns one on.");
            return null;
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var offered = new List<OfferedModel>();
            var notSignedIn = new List<IAgentAdapter>();
            var signedInWithoutList = new List<IAgentAdapter>();
            foreach (var provider in _services.Adapters.Values.GroupBy(a => a.Provider).OrderBy(g => g.Min(Preference)))
            {
                IAgentAdapter? unsigned = null;
                IAgentAdapter? unlisted = null;
                var found = false;
                foreach (var adapter in provider.OrderBy(Preference).ThenBy(a => a.Id, StringComparer.Ordinal))
                {
                    var snapshot = await SnapshotAsync(adapter.Id, false, cancellationToken).ConfigureAwait(false);
                    if (snapshot is not { Detection.Found: true })
                    {
                        continue;
                    }

                    if (snapshot.Auth is not { Authenticated: true })
                    {
                        unsigned ??= adapter;
                        continue;
                    }

                    var models = snapshot.Models.Where(m => !m.Hidden).ToList();
                    if (models.Count == 0)
                    {
                        unlisted ??= adapter;
                        continue;
                    }

                    var experimental = snapshot.Detection.Maturity == AdapterMaturity.Experimental;
                    offered.AddRange(models.Select(m => new OfferedModel(adapter, m, experimental)));
                    found = true;
                    break;
                }

                if (!found && unsigned is not null)
                {
                    notSignedIn.Add(unsigned);
                }
                else if (!found && unlisted is not null)
                {
                    signedInWithoutList.Add(unlisted);
                }
            }

            if (offered.Count > 0)
            {
                foreach (var adapter in notSignedIn)
                {
                    _ui.Muted($"{adapter.DisplayName} is installed but not signed in, so its models are not listed. " + HowToSignIn(adapter));
                }

                return offered;
            }

            if (notSignedIn.Count == 0)
            {
                if (signedInWithoutList.Count > 0)
                {
                    _ui.Warn($"{string.Join(" and ", signedInWithoutList.Select(a => a.DisplayName))} listed no models, so none can be offered by number. "
                        + "/models a <adapter> <model> and /models b <adapter> <model> choose them by name.");
                    return null;
                }

                SayHowToInstallAnAgent();
                return null;
            }

            // Installed, but not signed in. Where the provider permits it, its own sign-in is offered, one agent
            // after the other; for the others it is said how to sign in with the agent itself.
            var started = false;
            foreach (var adapter in notSignedIn)
            {
                _ui.Note(SetupStage, $"{adapter.DisplayName} is installed but not signed in, so it lists no models.");
                if (!MayStartSignIn(adapter))
                {
                    _ui.Muted(HowToSignIn(adapter));
                    continue;
                }

                if (await ProviderLoginAsync(adapter, cancellationToken).ConfigureAwait(false))
                {
                    started = true;
                    break;
                }
            }

            if (!started)
            {
                return null;
            }
        }

        _ui.Warn("No agent is signed in yet, so no model can be offered. /login shows each agent's account.");
        return null;
    }

    private static int Preference(IAgentAdapter adapter)
    {
        var index = Array.IndexOf(AdapterPreference, adapter.Id);
        return index < 0 ? AdapterPreference.Length : index;
    }

    private static string LoginName(IAgentAdapter adapter) => adapter.Provider == "anthropic" ? "claude" : "codex";

    /// <summary>
    /// Whether the guided first run may offer to start the provider's own sign-in. Not for Claude Code: Anthropic
    /// does not permit third-party developers to offer Claude.ai login (docs\adapters.md, "Authentication"), so YAV
    /// only uses the unmodified program the user signed in to themselves. /login claude, which the user asks for
    /// by name, is another matter.
    /// </summary>
    private static bool MayStartSignIn(IAgentAdapter adapter) => adapter.Provider != "anthropic";

    /// <summary>How to sign in with the agent itself, or with the provider's sign-in that YAV may start.</summary>
    private static string HowToSignIn(IAgentAdapter adapter) => MayStartSignIn(adapter)
        ? $"/login {LoginName(adapter)} starts its own sign-in."
        : "Sign in with Claude Code itself: open a console, run claude and follow its sign-in (or run claude auth login), then type the request again.";

    /// <summary>What to do when no agent program is installed. The lines are the makers' own; YAV runs none of them.</summary>
    private void SayHowToInstallAnAgent()
    {
        _ui.Warn("No agent program was found. YAV works through Codex CLI or Claude Code, and you install them yourself, as their makers describe:");
        _ui.Pairs(
            ("Codex CLI", "powershell -ExecutionPolicy ByPass -c \"irm https://chatgpt.com/codex/install.ps1 | iex\"   then: codex login"),
            ("Claude Code", "irm https://claude.ai/install.ps1 | iex   then: claude, and sign in"));
        _ui.Muted("Also possible: winget install Anthropic.ClaudeCode, or npm install -g @openai/codex.");
        _ui.Muted("Then open a new console, so that it finds the agent, and start yav again.");
    }

    /// <summary>Asks for the number of a model from the list. Null when no model was chosen.</summary>
    private async Task<OfferedModel?> AskForModelAsync(string name, List<OfferedModel> offered, RoleSelection? notThis, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var answer = (await _input.AskAsync($"{name} - type its number, then Enter: ", cancellationToken).ConfigureAwait(false))?.Trim();
            if (string.IsNullOrEmpty(answer))
            {
                _ui.Muted($"{name} was not chosen.");
                return null;
            }

            if (!int.TryParse(answer, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 1 || number > offered.Count)
            {
                _ui.Warn($"'{answer}' is not a number from the list (1 to {offered.Count}).");
                continue;
            }

            var chosen = offered[number - 1];
            if (notThis is not null
                && string.Equals(notThis.AdapterId, chosen.Adapter.Id, StringComparison.OrdinalIgnoreCase)
                && string.Equals(notThis.ModelId, chosen.Model.Id, StringComparison.OrdinalIgnoreCase))
            {
                var other = name == "Model A" ? "Model B" : "Model A";
                _ui.Warn($"{name} has to be another model than {other}: a model that reviews its own work is not the dual-model workflow, and with Quality Lock it does not run.");
                continue;
            }

            return chosen;
        }

        _ui.Muted($"{name} was not chosen.");
        return null;
    }

    /// <summary>A role whose agent is not signed in: the provider's own sign-in is offered.</summary>
    private async Task<Settling> SettleSignInAsync(IReadOnlyList<ProfileProblem> open, CancellationToken cancellationToken)
    {
        var result = Settling.NotNeeded;
        var asked = new HashSet<string>(StringComparer.Ordinal);
        foreach (var role in open.Where(p => p.Code == "auth-missing").Select(p => p.Role).OfType<AgentRole>().Distinct())
        {
            if (SelectionOf(role) is not { } selection || _services.Adapters.GetValueOrDefault(selection.AdapterId) is not { } adapter
                || !asked.Add(adapter.Provider))
            {
                continue;
            }

            // The model may have been chosen again a moment ago, from an agent that is signed in.
            if ((await SnapshotAsync(adapter.Id, false, cancellationToken).ConfigureAwait(false))?.Auth is { Authenticated: true })
            {
                continue;
            }

            _ui.Note(SetupStage, $"{adapter.DisplayName} is not signed in, and {RoleName(role)} works through it.");
            if (!MayStartSignIn(adapter))
            {
                // Nothing is asked: the sign-in is the agent's own, started by the user.
                _ui.Muted(HowToSignIn(adapter));
                return Settling.Declined;
            }

            if (!await ProviderLoginAsync(adapter, cancellationToken).ConfigureAwait(false)
                || (await SnapshotAsync(adapter.Id, refresh: true, cancellationToken).ConfigureAwait(false))?.Auth is not { Authenticated: true })
            {
                _ui.Muted($"{adapter.DisplayName} is not signed in. /login {LoginName(adapter)} starts its sign-in.");
                return Settling.Declined;
            }

            result = Settling.Settled;
        }

        return result;
    }

    /// <summary>
    /// A role whose effort cannot be resolved from what the model lists: a value YAV cannot rank, or a value that
    /// is not listed. The exact value is asked for; nothing is chosen or lowered for the user.
    /// </summary>
    private async Task<Settling> SettleEffortsAsync(HashSet<string> settled, CancellationToken cancellationToken)
    {
        var result = Settling.NotNeeded;
        foreach (var role in new[] { AgentRole.Implementer, AgentRole.Reviewer })
        {
            if (SelectionOf(role) is not { } selection)
            {
                continue;
            }

            var snapshot = await SnapshotAsync(selection.AdapterId, false, cancellationToken).ConfigureAwait(false);
            var model = snapshot?.Models.FirstOrDefault(m =>
                string.Equals(m.Id, selection.ModelId, StringComparison.OrdinalIgnoreCase) || string.Equals(m.ResolvedModelId, selection.ModelId, StringComparison.OrdinalIgnoreCase));
            if (model is null || model.SupportedEfforts.Count == 0)
            {
                continue;
            }

            var unranked = selection.WantsMaximum && ProfileResolver.HighestEffort(model.SupportedEfforts) is null;
            var unlisted = !selection.WantsMaximum && !model.SupportedEfforts.Any(e => e.Equals(selection.EffortPreference, StringComparison.OrdinalIgnoreCase));
            if (!unranked && !unlisted)
            {
                continue;
            }

            var name = RoleName(role);
            _ui.Note(SetupStage, unranked
                ? $"{name}, {model.Id}, lists effort values YAV cannot rank, so the maximum is not chosen for you. {ProfileResolver.DescribeEfforts(model, model.SupportedEfforts)}"
                : $"{name}, {model.Id}, does not list the effort '{selection.EffortPreference}' that was chosen for it, and nothing is lowered for you. {ProfileResolver.DescribeEfforts(model, model.SupportedEfforts)}");
            var effort = await AskForEffortAsync(name, model, cancellationToken).ConfigureAwait(false);
            if (effort is null || !await SetEffortAsync(role == AgentRole.Implementer, effort, cancellationToken).ConfigureAwait(false))
            {
                _ui.Muted($"/effort {(role == AgentRole.Implementer ? "a" : "b")} <value> sets it.");
                return Settling.Declined;
            }

            settled.Add($"{(unranked ? "effort-unranked" : "effort-unsupported")}:{role}");
            result = Settling.Settled;
        }

        return result;
    }

    /// <summary>Asks for one of the effort values the model lists, exactly. Null when none was given.</summary>
    private async Task<string?> AskForEffortAsync(string name, ModelInfo model, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var answer = (await _input.AskAsync($"Effort for {name} - type one of the listed values, then Enter: ", cancellationToken).ConfigureAwait(false))?.Trim();
            if (string.IsNullOrEmpty(answer))
            {
                return null;
            }

            if (model.SupportedEfforts.FirstOrDefault(e => e.Equals(answer, StringComparison.OrdinalIgnoreCase)) is { } listed)
            {
                return listed;
            }

            _ui.Warn($"'{answer}' is not a value {model.Id} lists. Listed: {string.Join(", ", model.SupportedEfforts)}.");
        }

        return null;
    }

    /// <summary>
    /// The account route of each role's agent that needs the user's acknowledgement: shown as /login shows it,
    /// and acknowledged only with a typed yes. Asked once for each provider.
    /// </summary>
    private async Task<Settling> SettleRoutesAsync(HashSet<string> settled, CancellationToken cancellationToken)
    {
        var result = Settling.NotNeeded;
        foreach (var role in new[] { AgentRole.Implementer, AgentRole.Reviewer })
        {
            if (SelectionOf(role) is not { } selection || _services.Adapters.GetValueOrDefault(selection.AdapterId) is not { } adapter)
            {
                continue;
            }

            var snapshot = await SnapshotAsync(adapter.Id, false, cancellationToken).ConfigureAwait(false);
            if (snapshot?.Auth is not { Authenticated: true, Policy: RoutePolicy.RequiresAcknowledgement } auth
                || _services.Database.IsRouteAcknowledged(auth.RouteKey(adapter.Id)))
            {
                continue;
            }

            _ui.Note(SetupStage, $"{RoleName(role)} works through {adapter.DisplayName}, with this account. Whether it may be used for runs of YAV is your decision:");
            ShowRoute(adapter, auth);
            if (!await AcknowledgeRouteAsync(adapter, auth, cancellationToken).ConfigureAwait(false))
            {
                _ui.Muted($"/login {LoginName(adapter)} shows the route and asks again.");
                return Settling.Declined;
            }

            settled.Add($"route-unacknowledged:{role}");
            result = Settling.Settled;
        }

        return result;
    }

    /// <summary>
    /// A project for which no approved check is required, which is what a run goes by: its own configuration is
    /// shown for approval, or what its files suggest. When there is nothing to approve, the user approves nothing, or
    /// what was approved requires no check either, acceptance on the review alone is offered, for this project only.
    /// It is not offered while the checks of the project cannot be read, because a run does not apply it then.
    /// </summary>
    private async Task<Settling> SettleChecksAsync(IReadOnlyList<ProfileProblem> open, string project, CancellationToken cancellationToken)
    {
        if (!open.Any(p => p.Code == "gates-missing"))
        {
            return Settling.NotNeeded;
        }

        var file = _services.Validation.ConfigurationFileName;
        var state = _services.Validation.LoadConfiguration(project);
        if (state.Effective.RequiredGates.Any())
        {
            // Approved since the request was refused: nothing to ask.
            return Settling.Settled;
        }

        // What was approved for the project, as far as it can be read: an approval YAV can no longer read says so itself.
        var approved = state.Errors.Count > 0 && state.Trust != ConfigurationTrust.Invalid
            ? string.Join(" ", state.Errors)
            : NoRequiredCheck(state.Effective);
        if (state.Trust == ConfigurationTrust.Invalid)
        {
            _ui.Note(SetupStage, $"{approved} Its {file} cannot be used: {string.Join(" ", state.Errors)}", NoteLevel.Warning);
        }
        else if (state.Pending is { } pending)
        {
            _ui.Note(SetupStage, $"{approved} Its {file} is not approved as it stands, and nothing of it runs before you approve it:");
            _confirmUnanswered = false;
            if (!await TrustGatesAsync(project, state, cancellationToken).ConfigureAwait(false))
            {
                if (_confirmUnanswered)
                {
                    // No answer at all (Control+C, or the input ended) is no decision against the checks: the
                    // review alone is not offered in their place.
                    return Settling.Declined;
                }
            }
            else
            {
                if (pending.RequiredGates.Any())
                {
                    return Settling.Settled;
                }

                // Approved as the file has it, and still no check is required. What is offered next goes by what
                // is approved now.
                _ui.Note(SetupStage, NoRequiredCheck(pending), NoteLevel.Warning);
                state = _services.Validation.LoadConfiguration(project);
            }
        }
        else if (_services.Validation.Detect(project).Count > 0)
        {
            _ui.Note(SetupStage, $"{approved} Its files suggest these:");
            _confirmUnanswered = false;
            if (await DetectGatesAsync(project, state, cancellationToken).ConfigureAwait(false))
            {
                return Settling.Settled;
            }

            if (_confirmUnanswered)
            {
                return Settling.Declined;
            }
        }
        else
        {
            _ui.Note(SetupStage, $"{approved} Its files suggest none.");
        }

        if (state.Errors.Count > 0)
        {
            // A run does not accept a candidate on the review alone while it cannot read the checks of the project:
            // one of them may be required. So the acceptance is not offered here either.
            _ui.Warn("Acceptance on the review alone is not offered while the checks of this project cannot be read: one of them may be required.");
            _ui.Muted(state.Trust == ConfigurationTrust.Invalid
                ? $"Correct {file}, then type the request again; /test trust shows it and asks."
                : "/test trust or /test detect approves checks again; then type the request again.");
            return Settling.Declined;
        }

        _ui.Warn("Without an approved check that is required, nothing is run to test a candidate of this project: it can only be accepted on Model B's review. "
            + "Once a check that is required is approved for it (/test detect, /test trust), checks are required again.");
        if (!await ConfirmAsync("Accept candidates of this project on the review alone?", cancellationToken).ConfigureAwait(false))
        {
            return Settling.Declined;
        }

        _services.Database.AcceptReviewOnly(project, "Candidates are accepted on the review alone while no approved check is required for the project.");
        _ui.Success("Accepted for this project. /quality gates required withdraws it.");
        return Settling.Settled;
    }

    /// <summary>
    /// Says that no check approved for the project is required, and tells apart a project whose approved checks are
    /// all optional: a run does not run a check that is optional, so they do not count.
    /// </summary>
    private static string NoRequiredCheck(ProjectConfiguration approved) => approved.Gates.Count switch
    {
        0 => "The project has no approved check.",
        1 => "The one check approved for the project is optional, and a run does not run an optional check.",
        var count => $"The {count} checks approved for the project are all optional, and a run does not run an optional check.",
    };

    /// <summary>
    /// Files the isolated workspace would not have: shown, and accepted for the project only with a typed yes, as
    /// /open --accept-gaps does.
    /// </summary>
    private async Task<Settling> SettleGapsAsync(IReadOnlyList<ProfileProblem> open, string project, CancellationToken cancellationToken)
    {
        if (!open.Any(p => p.Code == "workspace-gaps"))
        {
            return Settling.NotNeeded;
        }

        WorkspaceInspection inspection;
        try
        {
            inspection = await _services.Workspaces.InspectAsync(project, _services.Validation.LoadConfiguration(project).Effective, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _ui.Warn("The project could not be inspected: " + ex.Message);
            return Settling.Declined;
        }

        if (inspection.EquivalenceGaps.Count == 0
            || _services.Database.AreGapsAcknowledged(project, Yav.Coordinator.RunCoordinator.GapsFingerprint(inspection.EquivalenceGaps)))
        {
            // Gone since the request was refused, or accepted meanwhile: nothing to ask.
            return Settling.Settled;
        }

        _ui.Note(SetupStage, "The isolated workspace would differ from the project: " + string.Join(" ", inspection.EquivalenceGaps), NoteLevel.Warning);
        _ui.Muted($"A file a run needs can be copied instead: name it under replicateIgnored in {_services.Validation.ConfigurationFileName}.");
        if (!await ConfirmAsync("Accept this difference for the project?", cancellationToken).ConfigureAwait(false))
        {
            return Settling.Declined;
        }

        AcceptGaps(project, inspection.EquivalenceGaps);
        return Settling.Settled;
    }

    /// <summary>
    /// A request typed while no project is selected: the folder it is for is asked for and opened as /open does.
    /// A drive, the profile, the system or the folder of downloads is not taken, because the request would be
    /// sent there at once; /open selects such a folder deliberately. What was attached stays attached, and the
    /// request begins a task of its own: it was typed for no task. False when no project was selected; the
    /// request is then not sent.
    /// </summary>
    private async Task<bool> AskForProjectAsync(CancellationToken cancellationToken)
    {
        _ui.Note(SetupStage, "No project is selected, so the request has nowhere to go yet. Name the folder of the project it is for.");
        var attached = _session.Attachments.ToList();
        var opened = false;
        using (var question = BeginGuideQuestion(cancellationToken))
        {
            try
            {
                opened = await AskForProjectFolderAsync(question.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (question.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                opened = false;
            }
            finally
            {
                EndGuideQuestion(question);
            }
        }

        if (!opened)
        {
            _ui.Muted("The request was not sent. /open <path> selects a project.");
            return false;
        }

        // Opening the folder started with no attachments and with the task of its last run. Neither is what the
        // request was typed with.
        _session.Attachments.AddRange(attached.Where(a => !_session.Attachments.Contains(a, StringComparer.OrdinalIgnoreCase)));
        if (_session.TaskId is not null)
        {
            _session.TaskId = null;
            _session.TaskEffort = null;
            _ui.Muted("This request begins a task of its own; it does not continue the last run here.");
        }

        return true;
    }

    private async Task<bool> AskForProjectFolderAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            // A folder dragged into the console arrives in quotes.
            var answer = (await _input.AskAsync("Project folder - type or paste its path, then Enter: ", cancellationToken).ConfigureAwait(false))?.Trim().Trim('"').Trim();
            if (string.IsNullOrEmpty(answer))
            {
                return false;
            }

            string full;
            try
            {
                full = Path.GetFullPath(answer, Environment.CurrentDirectory);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                _ui.Error($"'{answer}' is not a path: {ex.Message}");
                continue;
            }

            if (Directory.Exists(full) && !IsSensibleProject(full))
            {
                _ui.Warn($"'{full}' is a drive, the profile, the Desktop, Documents, Downloads or a system folder, not a project: a run would copy all of it, and the request would go there at once. /open <path> selects it deliberately.");
                continue;
            }

            if (await OpenProjectAsync(full, Environment.CurrentDirectory, acceptGaps: false, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
    }

    private RoleSelection? SelectionOf(AgentRole role) => role == AgentRole.Implementer ? _services.Settings.ModelA : _services.Settings.ModelB;

    private static string RoleName(AgentRole role) => role == AgentRole.Implementer ? "Model A" : "Model B";
}
