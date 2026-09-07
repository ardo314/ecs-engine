using Ecs.Protocol.V1;
using Engine.Core;
using Google.Protobuf;
using Testing.V1;

namespace Client.Tests.Unit;

internal sealed class LateQuerySystem : SystemBase
{
    protected override void OnAdd() => NewQuery().With(Query.Read<TestPosition>());

    protected override Task OnUpdateAsync() => Task.CompletedTask;
}

internal sealed class CountingSystem : SystemBase
{
    public int Added;
    public int Removed;

    public CountingSystem() =>
        NewQuery().With(Query.Read<TestPosition>()).With(Query.Write<TestVelocity>());

    protected override void OnAdd() => Added++;

    protected override void OnRemove() => Removed++;

    protected override Task OnUpdateAsync() => Task.CompletedTask;
}

internal sealed class SpawningSystem : SystemBase
{
    public SpawningSystem() => NewQuery().With(Query.Read<TestPosition>());

    protected override void OnAdd() => Commands.CreateEntity(new TestDescribed { Value = 3 });

    protected override Task OnUpdateAsync() => Task.CompletedTask;
}

public class SystemLifecycleTests
{
    [Fact]
    public void SystemName_StripsTheSuffix()
    {
        Assert.Equal("Counting", new CountingSystem().SystemName);
    }

    [Fact]
    public void QueriesDeclaredInTheConstructor_AreReported()
    {
        var system = new CountingSystem();
        system.InvokeOnAdd();

        Assert.Single(system.GetQueries());
        Assert.Contains("testing.v1.TestPosition", system.ReadNames());
        Assert.Contains("testing.v1.TestVelocity", system.WriteNames());
        Assert.DoesNotContain("testing.v1.TestVelocity", system.ReadNames());
    }

    [Fact]
    public void DeclaringAQueryAfterJoiningAWorld_Throws()
    {
        var system = new LateQuerySystem();

        var error = Assert.Throws<InvalidOperationException>(system.InvokeOnAdd);
        Assert.Contains("constructor", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejoiningAWorld_DoesNotDuplicateQueries()
    {
        var system = new CountingSystem();

        system.InvokeOnAdd();
        system.InvokeOnRemove();
        system.InvokeOnAdd();

        Assert.Single(system.GetQueries());
        Assert.Equal(2, system.Added);
        Assert.Equal(1, system.Removed);
    }

    [Fact]
    public void Declarations_CoverQueriedAndCommandedTypesAlike()
    {
        var system = new SpawningSystem();
        system.InvokeOnAdd();

        var names = system.Declarations().Select(d => d.Type.LogicalName).ToList();

        Assert.Contains("testing.v1.TestPosition", names);
        Assert.Contains("testing.v1.TestDescribed", names);
    }

    [Fact]
    public void QueryDescriptors_AreOnlyMeaningfulAfterBinding()
    {
        var system = new CountingSystem();
        system.InvokeOnAdd();

        var bindings = new SchemaBindings();
        bindings.Add("testing.v1.TestPosition", 17);
        bindings.Add("testing.v1.TestVelocity", 22);
        system.BindQueries(bindings);

        var descriptor = Assert.Single(system.QueryDescriptors());
        Assert.Contains(descriptor.Required, a => a.TypeId == 17 && a.Access == Access.Read);
        Assert.Contains(descriptor.Required, a => a.TypeId == 22 && a.Access == Access.Write);
    }

    [Fact]
    public void BindingWithoutAnIdForADeclaredType_Throws()
    {
        var system = new CountingSystem();
        system.InvokeOnAdd();

        Assert.Throws<InvalidOperationException>(() => system.BindQueries(new SchemaBindings()));
    }
}

public class EntityCommandBufferTests
{
    [Fact]
    public void CreateEntity_BuffersASpawnCarryingTypedComponents()
    {
        var commands = new EntityCommandBuffer();
        commands.CreateEntity(new TestPosition { X = 1.5f }, new TestDescribed { Value = 2 });

        var command = Assert.Single(commands.Commands);
        Assert.Equal(StructuralCommand.CommandOneofCase.Spawn, command.CommandCase);
        Assert.Equal(2, command.Spawn.Components.Count);
        Assert.Equal(
            ComponentType<TestPosition>.SchemaHash,
            command.Spawn.Components[0].Type.SchemaHash);
    }

    [Fact]
    public void Commands_CollectTheSchemasTheyDependOn()
    {
        var commands = new EntityCommandBuffer();
        commands.CreateEntity(new TestPosition());
        commands.AddComponent(new Entity(1), new TestVelocity());

        var names = commands.Schemas.Select(s => s.Type.LogicalName).ToList();

        Assert.Contains("testing.v1.TestPosition", names);
        Assert.Contains("testing.v1.TestVelocity", names);
    }

    [Fact]
    public void ADescribedTypeBringsItsAttachmentsAlong()
    {
        var commands = new EntityCommandBuffer();
        commands.Declare<TestDescribed>();

        var declaration = Assert.Single(commands.Schemas, s => s.Type.LogicalName == "testing.v1.TestDescribed");
        Assert.Contains(declaration.Description, v => v.Type.LogicalName == "testing.v1.TestSetting");
        Assert.Contains(declaration.Description, v => v.Type.LogicalName == "testing.v1.TestCategory");
    }

    [Fact]
    public void ATypeIsDeclaredOnceHoweverOftenItIsUsed()
    {
        var commands = new EntityCommandBuffer();
        commands.Declare<TestPosition>();
        commands.AddComponent(new Entity(1), new TestPosition());
        commands.RemoveComponent<TestPosition>(new Entity(2));

        Assert.Single(commands.Schemas, s => s.Type.LogicalName == "testing.v1.TestPosition");
    }

    [Fact]
    public void RemoveComponent_TargetsAComponentTypeEntityByName()
    {
        var commands = new EntityCommandBuffer();
        commands.AddComponent(Target.OfComponentType<TestPosition>(), new TestSetting());

        var command = Assert.Single(commands.Commands);
        Assert.Equal("testing.v1.TestPosition", command.Add.Target.ComponentType);
        Assert.Equal("testing.v1.TestSetting", command.Add.Component.Type.LogicalName);
    }

    [Fact]
    public void Drain_TakesTheCommandsButKeepsTheSchemas()
    {
        var commands = new EntityCommandBuffer();
        commands.DestroyEntity(new Entity(7));
        commands.AddComponent(new Entity(1), new TestPosition());

        Assert.Equal(2, commands.Drain().Count);
        Assert.False(commands.HasPendingCommands);

        // Schemas are per-connection state, not per-flush, so they survive.
        Assert.Contains(commands.Schemas, s => s.Type.LogicalName == "testing.v1.TestPosition");
    }
}
