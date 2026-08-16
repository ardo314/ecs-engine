using Engine.Core;

namespace Client.Tests.Unit;

internal class LateQuerySystem : SystemBase
{
    protected override void OnAdd() => NewQuery().With(Query.ReadOnly<TestUndescribed>());

    protected override Task OnUpdateAsync() => Task.CompletedTask;
}

internal class LifecycleCountingSystem : SystemBase
{
    public int Adds;
    public int Removes;

    public LifecycleCountingSystem() => NewQuery().With(Query.ReadOnly<TestUndescribed>());

    protected override void OnAdd() => Adds++;

    protected override void OnRemove() => Removes++;

    protected override Task OnUpdateAsync() => Task.CompletedTask;
}

[Trait("Category", "Unit")]
public class SystemLifecycleTests
{
    public SystemLifecycleTests() => Serialization.Initialize();

    [Fact]
    public void QueriesDeclaredInConstructor_AreRegistered()
    {
        var system = new LifecycleCountingSystem();

        Assert.Single(system.GetQueryDescriptors());
        Assert.Contains(ComponentTypeId.Of<TestUndescribed>().TypeName, system.GetAllReadTypes());
    }

    [Fact]
    public void DeclaringQueryAfterAdd_Throws()
    {
        var system = new LateQuerySystem();

        var ex = Assert.Throws<InvalidOperationException>(system.InvokeOnAdd);
        Assert.Contains("constructor", ex.Message);
    }

    [Fact]
    public void ReAddingSystem_DoesNotDuplicateQueries()
    {
        var system = new LifecycleCountingSystem();

        system.InvokeOnAdd();
        system.InvokeOnRemove();
        system.InvokeOnAdd();

        Assert.Single(system.GetQueryDescriptors());
        Assert.Equal(2, system.Adds);
        Assert.Equal(1, system.Removes);
    }

    [Fact]
    public void ReAddingSystem_DoesNotRedescribeComponentTypes()
    {
        var system = new LifecycleCountingSystem();

        system.InvokeOnAdd();
        var afterFirst = system.Commands.Adds.Count;
        system.InvokeOnRemove();
        system.InvokeOnAdd();

        Assert.Equal(afterFirst, system.Commands.Adds.Count);
    }
}
