using Engine.Core;
using Engine.Core.Messages;
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

internal class SpawningSystem : SystemBase
{
    protected override void OnCreate() => Commands.CreateEntity(new TestUndescribed(1));

    protected override Task OnUpdateAsync() => Task.CompletedTask;
}

[Trait("Category", "Unit")]
public class ComponentDescriptionTests
{
    public ComponentDescriptionTests() => Serialization.Initialize();

    private static ComponentAddRequest[] InfoFor<T>(SystemBase system) where T : IComponent =>
        system.Commands.Adds
            .Where(a => a.Target.ComponentType == ComponentTypeId.Of<T>().TypeName
                        && a.ComponentType == ComponentInfo.Type)
            .ToArray();

    [Fact]
    public void EveryQueriedType_GetsATypeEntity()
    {
        var system = new DescribingSystem();
        system.InvokeOnCreate();

        Assert.Single(InfoFor<TestDescribed>(system));
        Assert.Single(InfoFor<TestUndescribed>(system));
    }

    [Fact]
    public void TypeInfo_CarriesTheTypeName()
    {
        var system = new DescribingSystem();
        system.InvokeOnCreate();

        var info = MessagePackSerializer.Deserialize<ComponentInfo>(
            InfoFor<TestUndescribed>(system).Single().Data, Serialization.Options);

        Assert.Equal(ComponentTypeId.Of<TestUndescribed>().TypeName, info.TypeName);
    }

    [Fact]
    public void DescribedType_AlsoGetsItsAttachments()
    {
        var system = new DescribingSystem();
        system.InvokeOnCreate();

        var self = ComponentTypeId.Of<TestDescribed>().TypeName;
        var category = system.Commands.Adds.Single(a =>
            a.Target.ComponentType == self &&
            a.ComponentType == ComponentTypeId.Of<TestCategory>().TypeName);

        Assert.Equal(
            new TestCategory("Control"),
            MessagePackSerializer.Deserialize<TestCategory>(category.Data, Serialization.Options));
        Assert.Contains(system.Commands.Adds, a =>
            a.Target.ComponentType == self &&
            a.ComponentType == ComponentTypeId.Of<TestSetting>().TypeName);
    }

    [Fact]
    public void AttachedComponentTypes_GetTypeEntitiesToo()
    {
        var system = new DescribingSystem();
        system.InvokeOnCreate();

        Assert.Single(InfoFor<TestCategory>(system));
        Assert.Single(InfoFor<TestSetting>(system));
    }

    [Fact]
    public void TypeUsedByMultipleQueries_IsDescribedOnce()
    {
        var system = new DuplicateQuerySystem();
        system.InvokeOnCreate();

        Assert.Single(InfoFor<TestDescribed>(system));
    }

    [Fact]
    public void SpawnedComponentTypes_GetTypeEntities()
    {
        var system = new SpawningSystem();
        system.InvokeOnCreate();

        Assert.Single(InfoFor<TestUndescribed>(system));
    }
}
