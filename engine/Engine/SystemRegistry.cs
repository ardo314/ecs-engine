using Engine.Core.Messages;

namespace Engine.Coordinator;

/// <summary>
/// Tracks registered systems and computes conflict-free execution stages.
/// Two systems conflict if one writes a component type that the other reads or writes.
/// </summary>
public class SystemRegistry
{
    private readonly Dictionary<string, SystemDescriptor> _systems = new();

    public void Register(SystemDescriptor descriptor)
    {
        var key = $"{descriptor.Name}:{descriptor.InstanceId}";
        _systems[key] = descriptor;
        Console.WriteLine($"[Registry] Registered system '{descriptor.Name}' (instance {descriptor.InstanceId})");
    }

    public void Unregister(SystemUnregister msg)
    {
        var key = $"{msg.Name}:{msg.InstanceId}";
        _systems.Remove(key);
        Console.WriteLine($"[Registry] Unregistered system '{msg.Name}' (instance {msg.InstanceId})");
    }

    /// <summary>
    /// Returns the distinct system names that are currently registered (deduplicates instances).
    /// </summary>
    public List<string> GetSystemNames() =>
        _systems.Values.Select(s => s.Name).Distinct().ToList();

    /// <summary>
    /// Returns one representative descriptor per unique system name.
    /// </summary>
    public List<SystemDescriptor> GetUniqueSystems() =>
        _systems.Values
            .GroupBy(s => s.Name)
            .Select(g => g.First())
            .ToList();

    /// <summary>
    /// Computes execution stages. Systems within the same stage can run in parallel.
    /// Systems that conflict (one writes what the other reads/writes) go in separate stages.
    /// </summary>
    public List<List<SystemDescriptor>> ComputeStages()
    {
        var systems = GetUniqueSystems();
        var stages = new List<List<SystemDescriptor>>();

        var placed = new HashSet<string>();

        while (placed.Count < systems.Count)
        {
            var stage = new List<SystemDescriptor>();
            var stageWrites = new HashSet<string>();
            var stageReads = new HashSet<string>();

            foreach (var sys in systems)
            {
                if (placed.Contains(sys.Name))
                    continue;

                // Check conflicts with already-placed systems in this stage
                var conflicts = false;

                // Conflict: this system writes something the stage reads or writes
                foreach (var w in sys.Writes)
                {
                    if (stageReads.Contains(w) || stageWrites.Contains(w))
                    {
                        conflicts = true;
                        break;
                    }
                }

                // Conflict: this system reads something the stage writes
                if (!conflicts)
                {
                    foreach (var r in sys.Reads)
                    {
                        if (stageWrites.Contains(r))
                        {
                            conflicts = true;
                            break;
                        }
                    }
                }

                if (!conflicts)
                {
                    stage.Add(sys);
                    placed.Add(sys.Name);
                    foreach (var w in sys.Writes) stageWrites.Add(w);
                    foreach (var r in sys.Reads) stageReads.Add(r);
                }
            }

            if (stage.Count > 0)
                stages.Add(stage);
        }

        return stages;
    }
}
