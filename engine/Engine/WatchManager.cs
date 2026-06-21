using Engine.Core.Messages;

namespace Engine.Coordinator;

/// <summary>
/// Tracks active watch subscriptions. Thread-safe for concurrent access from NATS handlers and the tick loop.
/// </summary>
public class WatchManager
{
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, WatchSpec> _watches = new();
    private int _systemsVersion;

    public WatchResponse Register(WatchRequest request)
    {
        var spec = new WatchSpec
        {
            WatchId = request.WatchId,
            IncludeSystems = request.IncludeSystems,
            IncludeEntities = request.IncludeEntities,
            ComponentFilter = request.ComponentFilter,
            DataSubject = $"engine.watch.data.{request.WatchId}",
            LastSystemsVersion = -1
        };

        lock (_lock)
        {
            _watches[request.WatchId] = spec;
        }

        Console.WriteLine($"[WatchManager] Watch registered: {request.WatchId} (systems={request.IncludeSystems}, entities={request.IncludeEntities})");

        return new WatchResponse
        {
            WatchId = request.WatchId,
            DataSubject = spec.DataSubject
        };
    }

    public void Cancel(Guid watchId)
    {
        lock (_lock)
        {
            _watches.Remove(watchId);
        }

        Console.WriteLine($"[WatchManager] Watch cancelled: {watchId}");
    }

    /// <summary>
    /// Called when a system registers or unregisters to bump the version.
    /// </summary>
    public void NotifySystemsChanged()
    {
        lock (_lock)
        {
            _systemsVersion++;
        }
    }

    /// <summary>
    /// Returns a snapshot of active watches with their specs. The tick loop calls this to push data.
    /// </summary>
    public List<WatchSpec> GetActiveWatches()
    {
        lock (_lock)
        {
            return [.. _watches.Values];
        }
    }

    /// <summary>
    /// Returns true if systems metadata should be included for this watch, and marks it as sent.
    /// </summary>
    public bool ShouldIncludeSystems(WatchSpec spec)
    {
        lock (_lock)
        {
            if (!spec.IncludeSystems)
                return false;

            if (spec.LastSystemsVersion == _systemsVersion)
                return false;

            // Update the version in the stored spec
            if (_watches.TryGetValue(spec.WatchId, out var stored))
            {
                stored.LastSystemsVersion = _systemsVersion;
            }

            return true;
        }
    }
}

public class WatchSpec
{
    public Guid WatchId { get; init; }
    public bool IncludeSystems { get; init; }
    public bool IncludeEntities { get; init; }
    public string[]? ComponentFilter { get; init; }
    public string DataSubject { get; init; } = "";
    public int LastSystemsVersion { get; set; } = -1;
}
