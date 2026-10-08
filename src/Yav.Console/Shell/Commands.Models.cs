using Yav.Console.Composition;
using Yav.Console.Doctor;
using Yav.Console.Rendering;
using Yav.Core.Agents;
using Yav.Core.Ports;
using Yav.Core.Profiles;

namespace Yav.Console.Shell;

public sealed partial class InteractiveShell
{
    private async Task<AdapterSnapshot?> SnapshotAsync(string adapterId, bool refresh, CancellationToken cancellationToken)
    {
        if (Coordinator.Catalog.Find(adapterId) is { } asked && (refresh || !Coordinator.Catalog.IsFresh(adapterId)))
        {
            // Said before the answer is waited for: an agent takes seconds to start, and it is started more than once.
            _ui.Muted($"Asking {asked.DisplayName} for its version, account and models. No inference is requested.");
        }

        try
        {
            return await Coordinator.Catalog.GetAsync(adapterId, refresh, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is AgentException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _ui.Warn($"{adapterId} could not be examined: {ex.Message}");
            return null;
        }
    }

    private async Task ModelsAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var verb = args.Count > 0 ? args[0].ToLowerInvariant() : string.Empty;
        switch (verb)
        {
            case "a" or "b":
                await ChooseModelAsync(verb == "a" ? AgentRole.Implementer : AgentRole.Reviewer, args.Skip(1).ToList(), cancellationToken).ConfigureAwait(false);
                return;

            case "swap":
                if (_services.Settings.ModelA is null || _services.Settings.ModelB is null)
                {
                    _ui.Warn("Both models have to be chosen before they can change places.");
                    return;
                }

                Save(settings => settings with { ModelA = settings.ModelB, ModelB = settings.ModelA });
                _ui.Say($"Model A (implements) is now {Describe(_services.Settings.ModelA)}; Model B (reviews) is now {Describe(_services.Settings.ModelB)}.");
                _ui.Muted("This applies to the next task. A run that is active keeps the models it started with.");
                return;

            case "refresh":
                Coordinator.Catalog.Invalidate();
                await ListModelsAsync(refresh: true, cancellationToken).ConfigureAwait(false);
                return;

            case "":
                await ListModelsAsync(refresh: false, cancellationToken).ConfigureAwait(false);
                return;

            default:
                _ui.Warn($"'{args[0]}' is not something /models does. Usage: /models [a|b <adapter> <model> | swap | refresh]");
                return;
        }
    }

    private async Task ListModelsAsync(bool refresh, CancellationToken cancellationToken)
    {
        var settings = _services.Settings;
        _ui.Heading("Models the providers list for your accounts");
        foreach (var adapter in _services.Adapters.Values.OrderBy(a => a.Id, StringComparer.Ordinal))
        {
            var snapshot = await SnapshotAsync(adapter.Id, refresh, cancellationToken).ConfigureAwait(false);
            if (snapshot is null)
            {
                continue;
            }

            var maturity = snapshot.Detection.Maturity == AdapterMaturity.Experimental ? " - experimental interface" : string.Empty;
            _ui.Say($"{adapter.DisplayName} ({adapter.Id}){maturity}", Tone.Accent);
            if (!snapshot.Detection.Found)
            {
                _ui.Muted("  not installed");
                continue;
            }

            if (snapshot.Auth is not { Authenticated: true })
            {
                _ui.Muted($"  not signed in: /login {(adapter.Provider == "anthropic" ? "claude" : "codex")}");
                continue;
            }

            if (snapshot.Models.Count == 0)
            {
                _ui.Muted("  This adapter cannot list models. A model chosen for it stays Requested / Unverified.");
                continue;
            }

            _ui.Table(
                ["", "Model", "Name", "Effort values", "Faster tier"],
                snapshot.Models.Where(m => !m.Hidden).Select(m => (IReadOnlyList<string>)
                [
                    Mark(settings, adapter.Id, m),
                    m.Id + (m.ResolvedModelId is not null && m.ResolvedModelId != m.Id ? $" -> {m.ResolvedModelId}" : string.Empty),
                    m.DisplayName,
                    m.SupportedEfforts.Count == 0 ? "none" : string.Join(", ", m.SupportedEfforts),
                    m.ServiceTiers.FirstOrDefault(t => t.Faster) is { } tier ? tier.Name : "-",
                ]));
        }

        _ui.Muted("A = implements, B = reviews. Choose with /models a <adapter> <model> and /models b <adapter> <model>.");
        _ui.Muted("YAV lists what the providers report. It does not rank models and does not choose for you.");
    }

    private static string Mark(Yav.Core.Settings.AppSettings settings, string adapterId, ModelInfo model)
    {
        bool Is(RoleSelection? selection) => selection is not null
            && string.Equals(selection.AdapterId, adapterId, StringComparison.OrdinalIgnoreCase)
            && (string.Equals(selection.ModelId, model.Id, StringComparison.OrdinalIgnoreCase)
                || string.Equals(selection.ModelId, model.ResolvedModelId, StringComparison.OrdinalIgnoreCase));

        return (Is(settings.ModelA), Is(settings.ModelB)) switch
        {
            (true, true) => "A B",
            (true, false) => "A",
            (false, true) => "B",
            _ => string.Empty,
        };
    }

    private async Task ChooseModelAsync(AgentRole role, List<string> args, CancellationToken cancellationToken)
    {
        if (args.Count is 0 or > 2)
        {
            _ui.Warn($"Usage: /models {(role == AgentRole.Implementer ? "a" : "b")} <adapter> <model>");
            return;
        }

        string? adapterId;
        string modelId;
        if (args.Count == 2)
        {
            adapterId = _services.Adapters.Keys.FirstOrDefault(k => string.Equals(k, args[0], StringComparison.OrdinalIgnoreCase));
            modelId = args[1];
            if (adapterId is null)
            {
                _ui.Warn($"'{args[0]}' is not an adapter. Available: {string.Join(", ", _services.Adapters.Keys.Order(StringComparer.Ordinal))}.");
                return;
            }
        }
        else
        {
            // Only the model was named. It is taken when exactly one adapter lists it.
            modelId = args[0];
            var offering = new List<string>();
            foreach (var id in _services.Adapters.Keys)
            {
                var listed = await SnapshotAsync(id, false, cancellationToken).ConfigureAwait(false);
                if (listed?.Models.Any(m => string.Equals(m.Id, modelId, StringComparison.OrdinalIgnoreCase)) == true)
                {
                    offering.Add(id);
                }
            }

            if (offering.Count != 1)
            {
                _ui.Warn(offering.Count == 0
                    ? $"No adapter lists a model '{modelId}'. /models shows what is listed."
                    : $"'{modelId}' is listed by {string.Join(" and ", offering)}. Name the adapter: /models {(role == AgentRole.Implementer ? "a" : "b")} <adapter> {modelId}");
                return;
            }

            adapterId = offering[0];
        }

        var snapshot = await SnapshotAsync(adapterId, false, cancellationToken).ConfigureAwait(false);
        var model = snapshot?.Models.FirstOrDefault(m =>
            string.Equals(m.Id, modelId, StringComparison.OrdinalIgnoreCase) || string.Equals(m.ResolvedModelId, modelId, StringComparison.OrdinalIgnoreCase));
        if (snapshot is { Models.Count: > 0 } && model is null)
        {
            _ui.Warn($"{adapterId} does not list a model '{modelId}' for your account, so it was not chosen. /models shows what is listed.");
            return;
        }

        RecordModel(role, adapterId, model, modelId, guided: false);
        _ui.Muted("This applies to the next task. A run that is active keeps the models it started with.");
    }

    /// <summary>
    /// Keeps the model the user chose for a role, at the maximum effort the provider lists, and says what follows
    /// from the choice. /models and the guided first run both record a choice here.
    /// </summary>
    /// <param name="guided">True when the guided first run asks for what is missing next, so the command for it is not named.</param>
    private void RecordModel(AgentRole role, string adapterId, ModelInfo? model, string modelId, bool guided)
    {
        var name = role == AgentRole.Implementer ? "Model A" : "Model B";
        var selection = new RoleSelection(adapterId, model?.Id ?? modelId);
        Save(settings => role == AgentRole.Implementer ? settings with { ModelA = selection } : settings with { ModelB = selection });
        _ui.Say($"{name} is {selection.ModelId} through {adapterId}, at the maximum effort the provider lists.");

        if (model is null)
        {
            _ui.Warn("The model could not be verified, because this adapter lists no models. It stays Requested / Unverified, which strict policy does not run.");
        }
        else if (model.SupportedEfforts.Count > 0 && ProfileResolver.HighestEffort(model.SupportedEfforts) is null)
        {
            _ui.Warn("This model lists an effort value YAV cannot rank, so the maximum is not chosen for you. " + ProfileResolver.DescribeEfforts(model, model.SupportedEfforts));
            if (!guided)
            {
                _ui.Muted($"Choose the exact value: /effort {(role == AgentRole.Implementer ? "a" : "b")} <value>");
            }
        }

        var other = role == AgentRole.Implementer ? _services.Settings.ModelB : _services.Settings.ModelA;
        if (other is not null && string.Equals(other.AdapterId, selection.AdapterId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(other.ModelId, selection.ModelId, StringComparison.OrdinalIgnoreCase))
        {
            _ui.Warn("Model A and Model B are now the same model. That is not the dual-model workflow; with Quality Lock it does not run.");
        }
    }

    private async Task EffortAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        if (args.Count == 0)
        {
            foreach (var (letter, selection) in new[] { ("A", _services.Settings.ModelA), ("B", _services.Settings.ModelB) })
            {
                if (selection is null)
                {
                    _ui.Say($"Model {letter}: not chosen");
                    continue;
                }

                var snapshot = await SnapshotAsync(selection.AdapterId, false, cancellationToken).ConfigureAwait(false);
                var model = snapshot?.Models.FirstOrDefault(m => string.Equals(m.Id, selection.ModelId, StringComparison.OrdinalIgnoreCase));
                _ui.Say($"Model {letter}: {selection.ModelId} ({selection.AdapterId})", Tone.Accent);
                if (model is null)
                {
                    _ui.Pairs(("Requested", selection.EffortPreference), ("Listed by the provider", "Unavailable: the model list could not be read"));
                    continue;
                }

                var highest = ProfileResolver.HighestEffort(model.SupportedEfforts);
                _ui.Pairs(
                    ("Requested", selection.WantsMaximum
                        ? model.SupportedEfforts.Count == 0 ? "maximum (the model has no effort setting)"
                            : highest is null ? "maximum - not resolved: choose the exact value" : $"maximum = {highest}"
                        : selection.EffortPreference),
                    ("Provider's default", model.DefaultEffort ?? "not reported"));
                foreach (var effort in model.SupportedEfforts)
                {
                    var description = model.EffortDescriptions is not null && model.EffortDescriptions.TryGetValue(effort, out var text) ? text : string.Empty;
                    _ui.Say($"    {effort,-10} {description}", Tone.Muted);
                }
            }

            _ui.Muted("Set with /effort a|b <value>, or /effort a|b maximum. What is in effect is shown by /status once a run has started.");
            return;
        }

        if (args.Count != 2 || args[0].ToLowerInvariant() is not ("a" or "b"))
        {
            _ui.Warn("Usage: /effort a|b <value>|maximum");
            return;
        }

        if (await SetEffortAsync(args[0].Equals("a", StringComparison.OrdinalIgnoreCase), args[1], cancellationToken).ConfigureAwait(false))
        {
            _ui.Muted("This applies to the next run. A run that is active keeps the effort it started with.");
        }
    }

    /// <summary>
    /// Sets the effort of a role to a value the model lists, or to "maximum", as /effort does. Nothing is changed,
    /// and nothing lowered, when the model does not list the value. False when nothing was set.
    /// </summary>
    private async Task<bool> SetEffortAsync(bool implementer, string value, CancellationToken cancellationToken)
    {
        var letter = implementer ? "A" : "B";
        var current = implementer ? _services.Settings.ModelA : _services.Settings.ModelB;
        if (current is null)
        {
            _ui.Warn($"Model {letter} is not chosen yet. Choose it with /models first.");
            return false;
        }

        if (value.Equals("max", StringComparison.OrdinalIgnoreCase) || value.Equals("maximum", StringComparison.OrdinalIgnoreCase))
        {
            // "max" is also a value some providers list. It is that value when the model lists it, and the preference otherwise.
            var listed = (await SnapshotAsync(current.AdapterId, false, cancellationToken).ConfigureAwait(false))?.Models
                .FirstOrDefault(m => string.Equals(m.Id, current.ModelId, StringComparison.OrdinalIgnoreCase));
            var exact = value.Equals("max", StringComparison.OrdinalIgnoreCase)
                ? listed?.SupportedEfforts.FirstOrDefault(e => e.Equals("max", StringComparison.OrdinalIgnoreCase))
                : null;
            value = exact ?? RoleSelection.MaximumEffort;
        }

        if (!value.Equals(RoleSelection.MaximumEffort, StringComparison.Ordinal))
        {
            var snapshot = await SnapshotAsync(current.AdapterId, false, cancellationToken).ConfigureAwait(false);
            var model = snapshot?.Models.FirstOrDefault(m => string.Equals(m.Id, current.ModelId, StringComparison.OrdinalIgnoreCase));
            if (model is not null)
            {
                var match = model.SupportedEfforts.FirstOrDefault(e => e.Equals(value, StringComparison.OrdinalIgnoreCase));
                if (match is null)
                {
                    _ui.Warn($"{current.ModelId} does not list the effort '{value}'. Listed: {(model.SupportedEfforts.Count == 0 ? "none" : string.Join(", ", model.SupportedEfforts))}. Nothing was changed, and nothing is lowered for you.");
                    return false;
                }

                value = match;
                var highest = ProfileResolver.HighestEffort(model.SupportedEfforts);
                if (highest is not null && !highest.Equals(value, StringComparison.OrdinalIgnoreCase))
                {
                    _ui.Warn($"'{value}' is below the maximum this model lists ('{highest}'). That is your choice; /effort {letter.ToLowerInvariant()} maximum returns to it.");
                }
            }
        }

        var changed = current with { EffortPreference = value };
        Save(settings => implementer ? settings with { ModelA = changed } : settings with { ModelB = changed });
        _ui.Say($"Model {letter} effort: {(changed.WantsMaximum ? "maximum supported" : changed.EffortPreference)}");
        return true;
    }

    private void Quality(IReadOnlyList<string> args)
    {
        var settings = _services.Settings;
        if (args.Count == 0)
        {
            _ui.Heading("Quality Lock and what a candidate has to pass");
            var pairs = new List<(string, string, Tone)>
            {
                ("Quality Lock", settings.QualityLock ? "ON: models, effort, review and billing route are held to what you chose" : "OFF", settings.QualityLock ? Tone.Success : Tone.Warning),
                ("Policy", settings.Strict ? "strict: a setting the provider did not confirm blocks the run" : "relaxed: an unconfirmed setting is shown as Requested / Unverified and the run goes on", settings.Strict ? Tone.Normal : Tone.Warning),
                ("Review by Model B", "required, in its own conversation, read-only", Tone.Normal),
                ("Required checks", settings.RequireGates ? "required: a project with no approved check that is required does not run" : "optional (the default): a project with no approved check that is required runs on the review alone; approved required checks still run and must pass", settings.RequireGates ? Tone.Normal : Tone.Warning),
            };
            if (settings.RequireGates && _session.ProjectPath is { } project && _services.Database.IsReviewOnlyAccepted(project))
            {
                var (text, tone) = DescribeReviewOnly(project);
                pairs.Add(("This project", text + " (/quality gates required withdraws it)", tone));
            }

            pairs.AddRange(
            [
                ("Repair cycles", $"{settings.Limits.MaxRepairCycles} after the first candidate (/limits repairs <n>)", Tone.Normal),
                ("Failures that were already there", settings.RepairPreExistingFailures ? "sent to Model A for repair" : "your decision: waive them, or fix them first", Tone.Normal),
                ("Adaptive", settings.Adaptive ? "ON: runs are marked Adaptive, not Strict Max" : "OFF", settings.Adaptive ? Tone.Warning : Tone.Normal),
            ]);
            _ui.Pairs(pairs);
            _ui.Muted("Change with /quality lock|strict on|off, /quality gates required|optional, /quality preexisting ask|repair.");
            _ui.Muted("Quality Lock keeps the configuration and the acceptance requirements. It cannot make a model's answer correct or the same twice.");
            return;
        }

        if (args.Count != 2)
        {
            _ui.Warn("Usage: /quality [lock|strict on|off] [gates required|optional] [preexisting ask|repair]");
            return;
        }

        var value = args[1].ToLowerInvariant();
        switch (args[0].ToLowerInvariant())
        {
            case "lock" when TryOnOff(value, out var locked):
                Save(s => s with { QualityLock = locked });
                _ui.Say(locked ? "Quality Lock: ON" : "Quality Lock: OFF. Runs are not held to the models and effort you chose; they are not Strict Max runs.", locked ? Tone.Success : Tone.Warning);
                break;

            case "strict" when TryOnOff(value, out var strict):
                Save(s => s with { Strict = strict });
                _ui.Say(strict
                    ? "Strict policy: a configuration that is unsupported or that the provider did not confirm blocks the run."
                    : "Relaxed policy: a setting the provider did not confirm is shown as Requested / Unverified and the run goes on.", strict ? Tone.Normal : Tone.Warning);
                break;

            case "gates" when value is "required" or "optional":
                Save(s => s with { RequireGates = value == "required" });
                _ui.Say(value == "required"
                    ? "Required checks: a project with no approved check that is required does not run."
                    : "Required checks are optional, the default: a project with no approved check that is required runs on the review alone, without asking. Approved checks that are required still run and still have to pass.", value == "required" ? Tone.Normal : Tone.Warning);

                // Required means required here as well: what was accepted for the project that is open is withdrawn.
                if (value == "required" && _session.ProjectPath is { } open && _services.Database.WithdrawReviewOnly(open))
                {
                    _ui.Say("Withdrawn for this project as well: it does not run again until an approved check is required for it, or you accept the review alone once more.");
                }

                break;

            case "preexisting" when value is "ask" or "repair":
                Save(s => s with { RepairPreExistingFailures = value == "repair" });
                _ui.Say(value == "repair"
                    ? "A required check that already failed before the task is sent to Model A for repair, within the repair limit."
                    : "A required check that already failed before the task is your decision: waive it with /test waive, or fix it first.");
                break;

            default:
                _ui.Warn("Usage: /quality [lock|strict on|off] [gates required|optional] [preexisting ask|repair]");
                return;
        }

        _ui.Muted("This applies to the next run. A run that is active keeps the policy it started with.");
    }

    /// <summary>
    /// What the acceptance of the review alone means for a project now, as a run decides it: it applies only while no
    /// approved check is required for the project, and not while the checks of the project cannot be read. An approved
    /// check that is optional does not count, because a run does not run it.
    /// </summary>
    private (string Text, Tone Tone) DescribeReviewOnly(string project)
    {
        var state = _services.Validation.LoadConfiguration(project);
        if (state.Errors.Count > 0)
        {
            return ($"accepted for review only, but that does not apply while its checks cannot be read: {state.Errors[0]}", Tone.Warning);
        }

        if (state.Effective.RequiredGates.Any())
        {
            return ("accepted for review only, but that does not apply while an approved check is required for it: its required checks run", Tone.Normal);
        }

        return (state.Effective.Gates.Count == 0
            ? "review only, as you accepted: no check is approved for it, so a candidate is accepted on the review alone"
            : "review only, as you accepted: none of its approved checks is required, and a run does not run an optional one, so a candidate is accepted on the review alone", Tone.Warning);
    }

    private async Task SpeedAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var settings = _services.Settings;
        var verb = args.Count == 0 ? string.Empty : args[0].ToLowerInvariant();
        if (verb is not ("" or "standard" or "provider"))
        {
            _ui.Warn("Usage: /speed [standard|provider]");
            return;
        }

        if (verb == "standard")
        {
            Save(s => s with { Speed = ProviderSpeedMode.Standard });
            _ui.Say("Provider speed: STANDARD. Models and effort are unchanged.");
            return;
        }

        var offers = new List<(string Role, RoleSelection Selection, AdapterSnapshot Snapshot, ServiceTierInfo? Tier)>();
        foreach (var (role, selection) in new[] { ("Model A", settings.ModelA), ("Model B", settings.ModelB) })
        {
            if (selection is null)
            {
                continue;
            }

            var snapshot = await SnapshotAsync(selection.AdapterId, false, cancellationToken).ConfigureAwait(false);
            if (snapshot is null)
            {
                continue;
            }

            var model = snapshot.Models.FirstOrDefault(m => string.Equals(m.Id, selection.ModelId, StringComparison.OrdinalIgnoreCase));
            offers.Add((role, selection, snapshot, model?.ServiceTiers.FirstOrDefault(t => t.Faster)));
        }

        _ui.Heading("Provider speed");
        _ui.Say($"Mode: {(settings.Speed == ProviderSpeedMode.Provider ? "PROVIDER - requested for the next run" : "STANDARD")}");
        _ui.Muted("Provider speed is the provider's own faster serving of the same model at the same effort. It never changes the model.");
        foreach (var offer in offers)
        {
            var key = offer.Snapshot.Auth?.RouteKey(offer.Snapshot.AdapterId);
            _ui.Say($"{offer.Role}: {offer.Selection.ModelId} ({offer.Selection.AdapterId})", Tone.Accent);
            if (offer.Tier is null)
            {
                _ui.Pairs(("Faster tier", "Unavailable: the provider lists none for this model and account"));
                continue;
            }

            _ui.Pairs(
                ("Faster tier", $"{offer.Tier.Name} ({offer.Tier.Id})"),
                ("Provider says", offer.Tier.Description ?? "no description given"),
                ("Billed through", offer.Snapshot.Auth is { } auth ? $"{auth.RouteLabel}; {DoctorChecks.Describe(auth.Billing)}" : "Unavailable"),
                ("Authorized by you", key is not null && _services.Database.IsPaidSpeedAuthorized(key) ? "yes" : "no"));
        }

        if (verb != "provider")
        {
            _ui.Muted("Ask for it with /speed provider. What is in effect is shown by /status once a run has started.");
            return;
        }

        if (offers.Count == 0 || offers.Any(o => o.Tier is null))
        {
            _ui.Warn("Provider speed was not turned on: it is not available for every role. Standard speed stays in use, and no model is exchanged for a faster one.");
            return;
        }

        _ui.Warn("The faster tier is billed differently from standard serving, as the provider describes above. YAV does not know the amount and shows no estimate of it.");
        if (!await ConfirmAsync("Use the faster tier for the roles above, with the billing the provider describes?", cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        foreach (var offer in offers)
        {
            if (offer.Snapshot.Auth?.RouteKey(offer.Snapshot.AdapterId) is { } key)
            {
                _services.Database.AuthorizePaidSpeed(key, $"{offer.Tier!.Name} ({offer.Tier.Id}): {offer.Tier.Description}");
            }
        }

        Save(s => s with { Speed = ProviderSpeedMode.Provider });
        _ui.Say("Provider speed: requested for the next run. A run reports whether the provider put it into effect; if it did not, the run is blocked rather than served at another tier in silence.");
    }

    private async Task LoginAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var provider = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal))?.ToLowerInvariant();
        var flags = args.Where(a => a.StartsWith("--", StringComparison.Ordinal)).Select(a => a.ToLowerInvariant()).ToList();
        if (provider is not (null or "codex" or "claude" or "openai" or "anthropic"))
        {
            _ui.Warn($"'{provider}' is not a provider YAV has an adapter for. Usage: /login [codex|claude] [--api-key | --forget-key | --acknowledge]");
            return;
        }

        var wanted = provider switch
        {
            "codex" or "openai" => "openai",
            "claude" or "anthropic" => "anthropic",
            _ => null,
        };

        if (flags.Contains("--api-key") || flags.Contains("--forget-key"))
        {
            await ApiKeyAsync(wanted, flags.Contains("--forget-key"), cancellationToken).ConfigureAwait(false);
            return;
        }

        var adapters = _services.Adapters.Values
            .Where(a => wanted is null || a.Provider == wanted)
            .GroupBy(a => a.Provider).Select(g => g.OrderBy(a => a.Id, StringComparer.Ordinal).First())
            .ToList();
        if (adapters.Count == 0)
        {
            _ui.Warn("No adapter for this provider is turned on. See /settings.");
            return;
        }

        foreach (var adapter in adapters)
        {
            var snapshot = await SnapshotAsync(adapter.Id, refresh: true, cancellationToken).ConfigureAwait(false);
            if (snapshot is null)
            {
                continue;
            }

            _ui.Say(adapter.DisplayName, Tone.Accent);
            if (!snapshot.Detection.Found)
            {
                _ui.Warn("  " + (snapshot.Detection.Problems.FirstOrDefault() ?? "The agent is not installed."));
                continue;
            }

            var auth = snapshot.Auth;
            if (auth is null || !auth.Authenticated)
            {
                _ui.Say("  Not signed in.");
                if (wanted is not null)
                {
                    await ProviderLoginAsync(adapter, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    _ui.Muted($"  /login {(adapter.Provider == "anthropic" ? "claude" : "codex")} starts the provider's own sign-in.");
                }

                continue;
            }

            var acknowledged = ShowRoute(adapter, auth);
            if (auth.Policy == RoutePolicy.RequiresAcknowledgement && !acknowledged && (wanted is not null || flags.Contains("--acknowledge")))
            {
                await AcknowledgeRouteAsync(adapter, auth, cancellationToken).ConfigureAwait(false);
            }
        }

        _ui.Muted("YAV never sees a password or a token. It shows what the agents themselves report about the account they use.");
    }

    /// <summary>
    /// Shows through which account an agent works, how that is billed and whether the route may be used for runs
    /// of YAV. True when the user acknowledged the route before.
    /// </summary>
    private bool ShowRoute(IAgentAdapter adapter, AuthStatus auth)
    {
        var acknowledged = _services.Database.IsRouteAcknowledged(auth.RouteKey(adapter.Id));
        _ui.Pairs(
        [
            ("Account route", auth.RouteLabel, Tone.Normal),
            ("Billing", DoctorChecks.Describe(auth.Billing), Tone.Normal),
            ("Read from", auth.Source, Tone.Muted),
            ("For this integration", auth.Policy switch
            {
                RoutePolicy.Allowed => "documented by the provider",
                RoutePolicy.NotPermitted => "not permitted by the provider",
                _ => acknowledged ? "acknowledged by you" : "needs your acknowledgement",
            }, auth.Policy == RoutePolicy.NotPermitted ? Tone.Error : acknowledged || auth.Policy == RoutePolicy.Allowed ? Tone.Success : Tone.Warning),
        ]);
        if (!string.IsNullOrWhiteSpace(auth.PolicyNote))
        {
            _ui.Quote(auth.PolicyNote, Tone.Muted);
        }

        return acknowledged;
    }

    /// <summary>
    /// Asks whether the route shown may be used for runs of YAV, and records the answer for every adapter of the
    /// provider. Only a typed yes acknowledges it. True when it was acknowledged.
    /// </summary>
    private async Task<bool> AcknowledgeRouteAsync(IAgentAdapter adapter, AuthStatus auth, CancellationToken cancellationToken)
    {
        if (!await ConfirmAsync($"Use '{auth.RouteLabel}' for runs of YAV, billed as stated above?", cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        // The same account serves every adapter of the provider.
        foreach (var sibling in _services.Adapters.Values.Where(a => a.Provider == adapter.Provider))
        {
            _services.Database.AcknowledgeRoute(auth.RouteKey(sibling.Id), $"{auth.RouteLabel}; {auth.PolicyNote}");
        }

        _ui.Success("  Acknowledged. It is asked again when the account route changes.");
        return true;
    }

    /// <summary>Starts the provider's own sign-in after the user confirmed it. True when it was started; the account is read again afterwards.</summary>
    private async Task<bool> ProviderLoginAsync(IAgentAdapter adapter, CancellationToken cancellationToken)
    {
        var flow = adapter.GetLoginFlow();
        if (flow is null)
        {
            _ui.Warn("  This agent has no sign-in that YAV could start. Sign in with the agent's own program.");
            return false;
        }

        foreach (var note in flow.Notes)
        {
            _ui.Muted("  " + note);
        }

        if (!await ConfirmAsync($"Start {flow.Description} now? It takes over the console until it is done.", cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        var exitCode = await ForegroundAsync(new ProcessSpec(flow.Executable, flow.Arguments, Environment.CurrentDirectory, Io: ProcessIo.InheritConsole), cancellationToken).ConfigureAwait(false);
        Coordinator.Catalog.Invalidate(adapter.Id);
        _ui.Say(exitCode == 0 ? "  The sign-in ended. /login shows the account that is used now." : $"  The sign-in ended with exit code {exitCode}.");
        return true;
    }

    private async Task ApiKeyAsync(string? provider, bool forget, CancellationToken cancellationToken)
    {
        if (provider != "anthropic")
        {
            _ui.Warn("YAV keeps an API key for Claude Code only: /login claude --api-key. Codex keeps its own credentials; use /login codex.");
            return;
        }

        if (!_services.Credentials.IsAvailable)
        {
            _ui.Error("The Windows Credential Manager is not available, so no key can be kept.");
            return;
        }

        if (forget)
        {
            _ui.Say(_services.Credentials.Delete(AppServices.AnthropicKeyName)
                ? "The API key was removed from the Windows Credential Manager."
                : "No API key was stored.");
            Coordinator.Catalog.Invalidate();
            return;
        }

        _ui.Say("An Anthropic API key is a route of its own: usage is billed per token to the API account the key belongs to.", Tone.Warning);
        _ui.Muted("The key is kept in the Windows Credential Manager, never in settings, logs or history. It is given to Claude Code through the environment of its process.");
        _ui.Muted("Commands that Claude Code runs inherit it. For that reason Claude Code does not implement with a key in a project that is not trusted.");
        var key = await _input.AskSecretAsync("API key (not shown; Enter to store, Esc to cancel): ", cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(key))
        {
            _ui.Muted(_input.CanAsk ? "No key was entered. Nothing was changed." : "A key cannot be entered in this mode, because it would be shown.");
            return;
        }

        _services.Credentials.Write(AppServices.AnthropicKeyName, key.Trim(), "Anthropic API key used by YAV Shell for Claude Code");
        Coordinator.Catalog.Invalidate();
        _ui.Success("The key is stored. /login claude shows the route and asks for your acknowledgement of it.");
    }
}
