using Ecs.Protocol.V1;
using Engine.Coordinator;

namespace Engine.Tests.Unit;

/// <summary>
/// The scheduler works entirely in integers. These tests are written that way on
/// purpose: if they needed a component type to mean something, the design would be
/// leaking.
/// </summary>
public class SystemRegistryTests
{
    private const uint Position = 17;
    private const uint Velocity = 22;
    private const uint Health = 31;

    private static SystemRegistration System(string name, uint[] reads, uint[] writes, string? instance = null)
    {
        var query = new QueryDescriptor();
        foreach (var id in reads)
            query.Required.Add(new Ecs.Protocol.V1.ComponentAccess { TypeId = id, Access = Access.Read });
        foreach (var id in writes)
            query.Required.Add(new Ecs.Protocol.V1.ComponentAccess { TypeId = id, Access = Access.Write });

        return new SystemRegistration
        {
            Name = name,
            InstanceId = instance ?? Guid.NewGuid().ToString("N"),
            Queries = { query },
        };
    }

    private static List<string> Names(List<List<SystemRegistration>> stages, int index) =>
        [.. stages[index].Select(s => s.Name).Order(StringComparer.Ordinal)];

    [Fact]
    public void Register_TracksTheSystem()
    {
        var registry = new SystemRegistry();
        registry.Register(System("Movement", [Position], [Velocity]));

        Assert.Equal(["Movement"], registry.SystemNames());
    }

    [Fact]
    public void Instances_OfTheSameSystemCollapseToOneScheduledUnit()
    {
        var registry = new SystemRegistry();
        registry.Register(System("Movement", [Position], [], "a"));
        registry.Register(System("Movement", [Position], [], "b"));

        Assert.Single(registry.SystemNames());
        Assert.Single(registry.UniqueSystems());
    }

    [Fact]
    public void Unregister_RemovesOnlyThatInstance()
    {
        var registry = new SystemRegistry();
        registry.Register(System("Movement", [Position], [], "a"));
        registry.Register(System("Movement", [Position], [], "b"));

        registry.Unregister(new SystemUnregistration { Name = "Movement", InstanceId = "a" });

        Assert.Single(registry.SystemNames());
    }

    [Fact]
    public void SharedReadsRunInParallel()
    {
        var registry = new SystemRegistry();
        registry.Register(System("A", [Position], []));
        registry.Register(System("B", [Position], []));

        var stages = registry.ComputeStages();

        Assert.Single(stages);
        Assert.Equal(["A", "B"], Names(stages, 0));
    }

    [Fact]
    public void AWriteAgainstAReadIsSerialised()
    {
        var registry = new SystemRegistry();
        registry.Register(System("Writer", [], [Position]));
        registry.Register(System("Reader", [Position], []));

        Assert.Equal(2, registry.ComputeStages().Count);
    }

    [Fact]
    public void TwoWritesToTheSameTypeAreSerialised()
    {
        var registry = new SystemRegistry();
        registry.Register(System("A", [], [Position]));
        registry.Register(System("B", [], [Position]));

        Assert.Equal(2, registry.ComputeStages().Count);
    }

    [Fact]
    public void DisjointWritesRunInParallel()
    {
        var registry = new SystemRegistry();
        registry.Register(System("A", [], [Position]));
        registry.Register(System("B", [], [Health]));

        Assert.Single(registry.ComputeStages());
    }

    [Fact]
    public void AChainOfDependenciesProducesAStagePerLink()
    {
        var registry = new SystemRegistry();
        registry.Register(System("A", [Position], [Velocity]));
        registry.Register(System("B", [Velocity], [Health]));
        registry.Register(System("C", [Position], [], "c"));

        var stages = registry.ComputeStages();

        // A writes Velocity, B reads it: they cannot share a stage. C only reads
        // Position, which A also only reads, so it joins A.
        Assert.Equal(2, stages.Count);
        Assert.Equal(["A", "C"], Names(stages, 0));
        Assert.Equal(["B"], Names(stages, 1));
    }

    [Fact]
    public void ATagJoinConflictsWithAWriteToWhateverItResolvedTo()
    {
        var registry = new SystemRegistry();
        registry.Register(new SystemRegistration
        {
            Name = "Reader",
            InstanceId = "r",
            Queries = { new QueryDescriptor { Tagged = { new TaggedAccess { TagTypeId = 99 } } } },
        });
        registry.Register(System("Writer", [], [Position]));

        // With no resolution the tag reads nothing, so the two can share a stage.
        Assert.Single(registry.ComputeStages());

        // Once the tag resolves to Position, the reader is reading what the writer writes.
        var resolution = new Dictionary<uint, uint[]> { [99] = [Position] };
        Assert.Equal(2, registry.ComputeStages(resolution).Count);
    }

    [Fact]
    public void AnEmptyRegistryHasNothingToSchedule()
    {
        Assert.Empty(new SystemRegistry().ComputeStages());
    }
}
