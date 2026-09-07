using Ecs.Protocol.V1;

namespace Engine.Coordinator;

/// <summary>
/// A live subscription to the world's per-tick state.
/// </summary>
public sealed class WatchSpec
{
    public required string WatchId { get; init; }
    public required bool IncludeSystems { get; init; }
    public required bool IncludeEntities { get; init; }
    public EntityFilter? Filter { get; init; }
    public required string DataSubject { get; init; }
    public int LastSystemsVersion { get; set; } = -1;
    public int LastSchemaVersion { get; set; } = -1;
}

/// <summary>
/// Tracks watch subscriptions and what each watcher has already been told.
/// </summary>
/// <remarks>
/// Systems, stages and schemas change rarely; entities change every tick. Tracking a
/// version per watcher means a steady world costs entity data alone, and a watcher that
/// connects mid-flight still gets the schema registry it needs to decode anything.
/// </remarks>
public sealed class WatchManager
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, WatchSpec> _watches = new(StringComparer.Ordinal);
    private int _systemsVersion;

    public WatchResponse Register(WatchRequest request)
    {
        var spec = new WatchSpec
        {
            WatchId = request.WatchId,
            IncludeSystems = request.IncludeSystems,
            IncludeEntities = request.IncludeEntities,
            Filter = request.Filter,
            DataSubject = Subjects.WatchData(request.WatchId),
        };

        lock (_gate) _watches[request.WatchId] = spec;

        Console.WriteLine(
            $"[Watch] Registered {request.WatchId} " +
            $"(systems={request.IncludeSystems}, entities={request.IncludeEntities})");

        return new WatchResponse { WatchId = request.WatchId, DataSubject = spec.DataSubject };
    }

    public void Cancel(string watchId)
    {
        lock (_gate) _watches.Remove(watchId);
        Console.WriteLine($"[Watch] Cancelled {watchId}");
    }

    /// <summary>Called when a system registers or unregisters.</summary>
    public void NotifySystemsChanged()
    {
        lock (_gate) _systemsVersion++;
    }

    public List<WatchSpec> ActiveWatches()
    {
        lock (_gate) return [.. _watches.Values];
    }

    /// <summary>
    /// Whether this watcher still needs the system list, marking it as sent.
    /// </summary>
    public bool ClaimSystems(WatchSpec spec)
    {
        lock (_gate)
        {
            if (!spec.IncludeSystems) return false;
            if (spec.LastSystemsVersion == _systemsVersion) return false;

            spec.LastSystemsVersion = _systemsVersion;
            return true;
        }
    }

    /// <summary>
    /// Whether this watcher still needs the schema registry, marking it as sent.
    /// </summary>
    public bool ClaimSchemas(WatchSpec spec, int schemaVersion)
    {
        lock (_gate)
        {
            if (spec.LastSchemaVersion == schemaVersion) return false;

            spec.LastSchemaVersion = schemaVersion;
            return true;
        }
    }
}
