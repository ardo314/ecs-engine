using Ecs.Protocol.V1;

namespace Engine.Coordinator;

/// <summary>
/// A system's claim on a slice of the world for exactly one tick.
/// </summary>
public sealed record Lease(
    string Id,
    ulong Tick,
    string SystemName,
    IReadOnlySet<ulong> Entities,
    IReadOnlySet<uint> Writable);

/// <summary>
/// Issues and validates tick-scoped leases.
/// </summary>
/// <remarks>
/// A single-process ECS gets this from the borrow checker: while a system holds
/// <c>&amp;mut Velocity</c> over a slice, nothing else can touch it. Spread across
/// processes there is no such guarantee to lean on, so the world hands out an explicit,
/// single-use claim and refuses anything that does not match it.
///
/// The property that matters most is the boring one: a lease dies with its tick. A
/// worker that stalls through tick 8291 and returns its writes during 8292 finds its
/// lease gone, and the world is not silently corrupted by stale data.
/// </remarks>
public sealed class LeaseManager
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Lease> _live = new(StringComparer.Ordinal);

    public Lease Issue(
        ulong tick,
        string systemName,
        IReadOnlyCollection<ulong> entities,
        IReadOnlySet<uint> writable)
    {
        var lease = new Lease(
            Guid.NewGuid().ToString("N"),
            tick,
            systemName,
            entities.ToHashSet(),
            writable);

        lock (_gate) _live[lease.Id] = lease;
        return lease;
    }

    /// <summary>
    /// Takes the lease named by <paramref name="leaseId"/>, if it is live and belongs to
    /// <paramref name="tick"/>. Leases are single-use: a second result under the same
    /// lease is refused.
    /// </summary>
    public bool TryClaim(string leaseId, ulong tick, out Lease lease, out ResultRejectionReason reason)
    {
        lock (_gate)
        {
            if (!_live.Remove(leaseId, out lease!))
            {
                // Already claimed, or retired when its stage ended. Either way the world
                // has moved on.
                reason = ResultRejectionReason.UnknownLease;
                return false;
            }

            if (lease.Tick != tick)
            {
                reason = ResultRejectionReason.TickMismatch;
                return false;
            }

            reason = ResultRejectionReason.Unspecified;
            return true;
        }
    }

    /// <summary>
    /// Retires every lease for <paramref name="tick"/> or earlier. Called when a stage
    /// closes, so a late result is refused rather than applied to a world that has
    /// advanced past it.
    /// </summary>
    public int RetireThrough(ulong tick)
    {
        lock (_gate)
        {
            var stale = _live.Where(kv => kv.Value.Tick <= tick).Select(kv => kv.Key).ToList();
            foreach (var id in stale) _live.Remove(id);
            return stale.Count;
        }
    }

    public int LiveCount
    {
        get { lock (_gate) return _live.Count; }
    }
}
