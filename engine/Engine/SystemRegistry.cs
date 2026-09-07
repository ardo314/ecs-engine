using Ecs.Protocol.V1;

namespace Engine.Coordinator;

/// <summary>
/// Tracks registered systems and packs them into conflict-free execution stages.
/// </summary>
/// <remarks>
/// The scheduler never learns what a component means. It sees a system as two sets of
/// integers and applies one rule:
///
///     conflict(A, B) = A.writes ∩ B.reads  ≠ ∅
///                   || A.reads  ∩ B.writes ≠ ∅
///                   || A.writes ∩ B.writes ≠ ∅
///
/// so <c>Read&lt;X&gt;</c> pairs with <c>Read&lt;X&gt;</c> and everything else serialises.
/// Because the rule is expressed over ids alone, a component type introduced at runtime
/// participates in scheduling without the coordinator being rebuilt.
/// </remarks>
public sealed class SystemRegistry
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, SystemRegistration> _instances = new(StringComparer.Ordinal);

    public void Register(SystemRegistration registration)
    {
        lock (_gate)
            _instances[$"{registration.Name}:{registration.InstanceId}"] = registration;

        Console.WriteLine(
            $"[Registry] Registered '{registration.Name}' (instance {registration.InstanceId})");
    }

    public void Unregister(SystemUnregistration message)
    {
        lock (_gate)
            _instances.Remove($"{message.Name}:{message.InstanceId}");

        Console.WriteLine(
            $"[Registry] Unregistered '{message.Name}' (instance {message.InstanceId})");
    }

    /// <summary>
    /// One representative registration per system name. Instances of the same system are
    /// interchangeable — they share a queue group, so the coordinator schedules the name.
    /// </summary>
    public List<SystemRegistration> UniqueSystems()
    {
        lock (_gate)
        {
            return [.. _instances.Values
                .GroupBy(s => s.Name, StringComparer.Ordinal)
                .Select(g => g.First())];
        }
    }

    public List<string> SystemNames() => [.. UniqueSystems().Select(s => s.Name)];

    public static HashSet<uint> ReadsOf(SystemRegistration system) =>
        [.. system.Queries
            .SelectMany(q => q.Required.Concat(q.Optional))
            .Where(a => a.Access == Access.Read)
            .Select(a => a.TypeId)];

    public static HashSet<uint> WritesOf(SystemRegistration system) =>
        [.. system.Queries
            .SelectMany(q => q.Required.Concat(q.Optional))
            .Where(a => a.Access == Access.Write)
            .Select(a => a.TypeId)];

    public static HashSet<uint> TagsOf(SystemRegistration system) =>
        [.. system.Queries.SelectMany(q => q.Tagged).Select(t => t.TagTypeId)];

    /// <summary>
    /// Greedily packs systems into stages. Systems in a stage are proved conflict-free,
    /// so they run in parallel; stages run in order.
    /// </summary>
    /// <param name="tagResolution">
    /// Tag type id to the component type ids carrying it. Tag joins read whatever they
    /// resolve to this tick, so those types must participate in conflict detection or a
    /// tag-reading system could run beside a system writing one of them.
    /// </param>
    public List<List<SystemRegistration>> ComputeStages(
        IReadOnlyDictionary<uint, uint[]>? tagResolution = null)
    {
        var systems = UniqueSystems();
        var stages = new List<List<SystemRegistration>>();
        var placed = new HashSet<string>(StringComparer.Ordinal);

        while (placed.Count < systems.Count)
        {
            var stage = new List<SystemRegistration>();
            var stageReads = new HashSet<uint>();
            var stageWrites = new HashSet<uint>();

            foreach (var system in systems)
            {
                if (placed.Contains(system.Name)) continue;

                var reads = ExpandReads(system, tagResolution);
                var writes = WritesOf(system);

                if (writes.Overlaps(stageReads) ||
                    writes.Overlaps(stageWrites) ||
                    reads.Overlaps(stageWrites))
                {
                    continue;
                }

                stage.Add(system);
                placed.Add(system.Name);
                stageReads.UnionWith(reads);
                stageWrites.UnionWith(writes);
            }

            if (stage.Count == 0) break;
            stages.Add(stage);
        }

        return stages;
    }

    private static HashSet<uint> ExpandReads(
        SystemRegistration system,
        IReadOnlyDictionary<uint, uint[]>? tagResolution)
    {
        var reads = ReadsOf(system);
        if (tagResolution is null) return reads;

        foreach (var tag in TagsOf(system))
        {
            if (tagResolution.TryGetValue(tag, out var types))
                reads.UnionWith(types);
        }
        return reads;
    }
}
