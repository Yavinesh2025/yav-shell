using System.Collections.Concurrent;
using Yav.Core.Agents;
using Yav.Core.Ports;
using Yav.Core.Profiles;

namespace Yav.Coordinator;

/// <summary>
/// What is known about each agent: installation, account route and models. Readings are kept while the
/// shell is open so that starting a task does not wait for discovery again. Reading never sends an
/// inference request.
/// </summary>
public sealed class AdapterCatalog
{
    private readonly IReadOnlyDictionary<string, IAgentAdapter> _adapters;
    private readonly IProjectTrustStore _trust;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _maxAge;
    private readonly ConcurrentDictionary<string, Reading> _readings = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);

    private sealed record Reading(AdapterDetection Detection, AuthStatus? Auth, IReadOnlyList<ModelInfo> Models, IReadOnlyList<string> Errors, long Timestamp);

    public AdapterCatalog(IReadOnlyDictionary<string, IAgentAdapter> adapters, IProjectTrustStore trust, TimeProvider clock, TimeSpan maxAge)
    {
        _adapters = adapters;
        _trust = trust;
        _clock = clock;
        _maxAge = maxAge;
    }

    public IReadOnlyCollection<string> AdapterIds => _adapters.Keys.ToArray();

    public IAgentAdapter? Find(string adapterId) => _adapters.GetValueOrDefault(adapterId);

    /// <summary>Forgets what was read, for example after a login.</summary>
    public void Invalidate(string? adapterId = null)
    {
        if (adapterId is null)
        {
            _readings.Clear();
        }
        else
        {
            _readings.TryRemove(adapterId, out _);
        }
    }

    /// <summary>
    /// True when what is known about the agent is recent enough to be used without asking it again. Asking
    /// takes as long as the agent takes to start, several times over.
    /// </summary>
    public bool IsFresh(string adapterId) =>
        _readings.TryGetValue(adapterId, out var reading) && _clock.GetElapsedTime(reading.Timestamp) <= _maxAge;

    /// <summary>Problems met while reading an adapter, for /doctor. Empty when everything could be read.</summary>
    public IReadOnlyList<string> ErrorsFor(string adapterId) =>
        _readings.TryGetValue(adapterId, out var reading) ? reading.Errors : [];

    public async Task<AdapterSnapshot?> GetAsync(string adapterId, bool refresh, CancellationToken cancellationToken)
    {
        if (!_adapters.TryGetValue(adapterId, out var adapter))
        {
            return null;
        }

        var gate = _gates.GetOrAdd(adapterId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        Reading reading;
        try
        {
            if (refresh || !_readings.TryGetValue(adapterId, out reading!) || _clock.GetElapsedTime(reading.Timestamp) > _maxAge)
            {
                reading = await ReadAsync(adapter, cancellationToken).ConfigureAwait(false);
                _readings[adapterId] = reading;
            }
        }
        finally
        {
            gate.Release();
        }

        // Acknowledgements are read every time: they change when the user answers a prompt.
        var routeKey = reading.Auth?.RouteKey(adapter.Id);
        return new AdapterSnapshot(
            adapter.Id,
            adapter.Provider,
            reading.Detection,
            reading.Auth,
            reading.Models,
            adapter.Capabilities,
            RouteAcknowledged: routeKey is not null && (reading.Auth!.UsedWithoutAsking || _trust.IsRouteAcknowledged(routeKey)),
            PaidSpeedAuthorized: routeKey is not null && _trust.IsPaidSpeedAuthorized(routeKey));
    }

    public async Task<IReadOnlyDictionary<string, AdapterSnapshot>> GetManyAsync(IEnumerable<string> adapterIds, bool refresh, CancellationToken cancellationToken)
    {
        var ids = adapterIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var snapshots = await Task.WhenAll(ids.Select(id => GetAsync(id, refresh, cancellationToken))).ConfigureAwait(false);
        var result = new Dictionary<string, AdapterSnapshot>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < ids.Count; i++)
        {
            if (snapshots[i] is { } snapshot)
            {
                result[ids[i]] = snapshot;
            }
        }

        return result;
    }

    private async Task<Reading> ReadAsync(IAgentAdapter adapter, CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        var detection = await adapter.DetectAsync(cancellationToken).ConfigureAwait(false);
        if (!detection.Usable)
        {
            return new Reading(detection, null, [], errors, _clock.GetTimestamp());
        }

        AuthStatus? auth = null;
        try
        {
            auth = await adapter.GetAuthStatusAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            errors.Add($"The account of {adapter.DisplayName} could not be read: {ex.Message}");
        }

        IReadOnlyList<ModelInfo> models = [];
        if (auth is { Authenticated: true })
        {
            try
            {
                models = await adapter.ListModelsAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add($"The models of {adapter.DisplayName} could not be listed: {ex.Message}");
            }
        }

        return new Reading(detection, auth, models, errors, _clock.GetTimestamp());
    }
}
