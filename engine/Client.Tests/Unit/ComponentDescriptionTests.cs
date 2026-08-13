using Engine.Core;
using MessagePack;

namespace Client.Tests.Unit;

public readonly record struct TestSetting : IComponent;

public readonly record struct TestCategory(string Name) : IComponent;

public record struct TestDescribed(int Value) : IComponent
{
    public static void Describe(EntityCommandBuffer commands, EntityRef self)
    {
        commands.AddComponent(self, new TestSetting());
        commands.AddComponent(self, new TestCategory("Control"));
    }
}

public record struct TestUndescribed(int Value) : IComponent;

internal class DescribingSystem : SystemBase
{
    protected override void OnCreate() => NewQuery()
        .With(Query.ReadWrite<TestDescribed>())
        .With(Query.ReadOnly<TestUndescribed>());

    protected override Task OnUpdateAsync() => Task.CompletedTask;
}

internal class DuplicateQuerySystem : SystemBase
{
    protected override void OnCreate()
    {
        NewQuery().With(Query.ReadOnly<TestDescribed>());
        NewQuery().With(Query.ReadWrite<TestDescribed>());
    }

    protected override Task OnUpdateAsync() => Task.CompletedTask;
}

[Trait("Category", "Unit")]
public class ComponentDescriptionTests
{
    public ComponentDescriptionTests() => Serialization.Initialize();

    [Fact]
    public void OnCreate_BuffersDescriptionCommandsForQueriedTypes()
    {
        var system = new DescribingSystem();
        system.InvokeOnCreate();

        var described = system.Commands.Adds
            .Where(a => a.Target.ComponentType == ComponentTypeId.Of<TestDescribed>().TypeName)
            .ToArray();

        Assert.Equal(3, described.Length);
        Assert.All(described, a => Assert.Equal(0UL, a.Target.EntityId));
    }

    [Fact]
    public void DescriptionCommands_CarryComponentData()
    {
        var system = new DescribingSystem();
        system.InvokeOnCreate();

        var category = system.Commands.Adds
            .Single(a => a.ComponentType == ComponentTypeId.Of<TestCategory>().TypeName);

        Assert.Equal(
            new TestCategory("Control"),
            MessagePackSerializer.Deserialize<TestCategory>(category.Data, Serialization.Options));
    }

    [Fact]
    public void ComponentsWithoutDescription_StillGetComponentInfo()
    {
        var system = new DescribingSystem();
        system.InvokeOnCreate();

        var undescribed = system.Commands.Adds
            .Single(a => a.Target.ComponentType == ComponentTypeId.Of<TestUndescribed>().TypeName);

        Assert.Equal(ComponentInfo.Type, undescribed.ComponentType);
        Assert.Equal(
            new ComponentInfo(ComponentTypeId.Of<TestUndescribed>().TypeName),
            MessagePackSerializer.Deserialize<ComponentInfo>(undescribed.Data, Serialization.Options));
    }

    [Fact]
    public void TypeUsedByMultipleQueries_IsDescribedOnce()
    {
        var system = new DuplicateQuerySystem();
        system.InvokeOnCreate();

        Assert.Equal(3, system.Commands.Adds.Count);
    }
}
