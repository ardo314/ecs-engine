using Engine.Coordinator;
using Engine.Core.Messages;

namespace Engine.Tests.Unit;

[Trait("Category", "Unit")]
public class SystemRegistryTests
{
    private static SystemDescriptor MakeSystem(string name, string[] reads, string[] writes) =>
        new()
        {
            Name = name,
            InstanceId = Guid.NewGuid().ToString(),
            Reads = reads,
            Writes = writes
        };

    [Fact]
    public void Register_AddsSystem()
    {
        var registry = new SystemRegistry();
        registry.Register(MakeSystem("Movement", ["Velocity"], ["Position"]));

        Assert.Single(registry.GetSystemNames());
        Assert.Equal("Movement", registry.GetSystemNames()[0]);
    }

    [Fact]
    public void Register_MultipleInstances_DeduplicatesNames()
    {
        var registry = new SystemRegistry();
        registry.Register(MakeSystem("Movement", ["Velocity"], ["Position"]));
        registry.Register(MakeSystem("Movement", ["Velocity"], ["Position"]));

        Assert.Single(registry.GetSystemNames());
        Assert.Single(registry.GetUniqueSystems());
    }

    [Fact]
    public void Unregister_RemovesSystem()
    {
        var registry = new SystemRegistry();
        var desc = MakeSystem("AI", ["Health"], ["Action"]);
        registry.Register(desc);

        registry.Unregister(new SystemUnregister { Name = desc.Name, InstanceId = desc.InstanceId });

        Assert.Empty(registry.GetSystemNames());
    }

    [Fact]
    public void ComputeStages_NonConflicting_SameStage()
    {
        var registry = new SystemRegistry();
        registry.Register(MakeSystem("Movement", ["Velocity"], ["Position"]));
        registry.Register(MakeSystem("AI", ["Health"], ["Action"]));

        var stages = registry.ComputeStages();

        Assert.Single(stages);
        Assert.Equal(2, stages[0].Count);
    }

    [Fact]
    public void ComputeStages_WriteReadConflict_SeparateStages()
    {
        var registry = new SystemRegistry();
        // Physics writes Transform, Render reads Transform → conflict
        registry.Register(MakeSystem("Physics", [], ["Transform"]));
        registry.Register(MakeSystem("Render", ["Transform"], []));

        var stages = registry.ComputeStages();

        Assert.Equal(2, stages.Count);
        Assert.Single(stages[0]);
        Assert.Single(stages[1]);
    }

    [Fact]
    public void ComputeStages_WriteWriteConflict_SeparateStages()
    {
        var registry = new SystemRegistry();
        registry.Register(MakeSystem("SystemA", [], ["Health"]));
        registry.Register(MakeSystem("SystemB", [], ["Health"]));

        var stages = registry.ComputeStages();

        Assert.Equal(2, stages.Count);
    }

    [Fact]
    public void ComputeStages_Empty_ReturnsEmpty()
    {
        var registry = new SystemRegistry();
        Assert.Empty(registry.ComputeStages());
    }

    [Fact]
    public void ComputeStages_ComplexConflicts_ProducesCorrectStages()
    {
        var registry = new SystemRegistry();
        // A writes X, B reads X (conflict), C writes Y (no conflict with A)
        registry.Register(MakeSystem("A", [], ["X"]));
        registry.Register(MakeSystem("B", ["X"], []));
        registry.Register(MakeSystem("C", [], ["Y"]));

        var stages = registry.ComputeStages();

        // A and C can run together (no conflict), B must be separate
        Assert.Equal(2, stages.Count);
        var stage1Names = stages[0].Select(s => s.Name).ToList();
        var stage2Names = stages[1].Select(s => s.Name).ToList();

        Assert.Contains("A", stage1Names);
        Assert.Contains("C", stage1Names);
        Assert.Contains("B", stage2Names);
    }

    [Fact]
    public void GetUniqueSystems_ReturnsOnePerName()
    {
        var registry = new SystemRegistry();
        registry.Register(MakeSystem("Movement", ["Vel"], ["Pos"]));
        registry.Register(MakeSystem("Movement", ["Vel"], ["Pos"]));
        registry.Register(MakeSystem("AI", ["Health"], []));

        var unique = registry.GetUniqueSystems();

        Assert.Equal(2, unique.Count);
        Assert.Contains(unique, s => s.Name == "Movement");
        Assert.Contains(unique, s => s.Name == "AI");
    }
}
