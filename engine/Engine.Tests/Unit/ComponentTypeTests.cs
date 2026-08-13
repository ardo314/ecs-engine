using Engine.Core;
using Engine.Coordinator;
using MessagePack;

namespace Engine.Tests.Unit;

[Trait("Category", "Unit")]
public class ComponentTypeTests
{
    private const string PidSettings = "Nova.Components.PidSettings";

    [Fact]
    public void GetOrCreateTypeEntity_CreatesEntityWithInfo()
    {
        var world = new WorldState();
        var typeEntity = world.GetOrCreateTypeEntity(PidSettings);

        var info = MessagePackSerializer.Deserialize<ComponentInfo>(
            world.GetComponent(typeEntity, ComponentInfo.Type)!, Serialization.Options);

        Assert.Equal(PidSettings, info.TypeName);
        Assert.Equal(typeEntity, world.FindTypeEntity(PidSettings));
    }

    [Fact]
    public void GetOrCreateTypeEntity_IsIdempotent()
    {
        var world = new WorldState();
        var first = world.GetOrCreateTypeEntity(PidSettings);

        Assert.Equal(first, world.GetOrCreateTypeEntity(PidSettings));
        Assert.Equal(1, world.EntityCount);
    }

    [Fact]
    public void TypeEntities_ShareTheEntityIdSpace()
    {
        var world = new WorldState();
        var entity = world.AllocateEntity();
        var typeEntity = world.GetOrCreateTypeEntity(PidSettings);

        Assert.Equal(1UL, entity);
        Assert.Equal(2UL, typeEntity);
        Assert.True(world.IsAlive(typeEntity));
    }

    [Fact]
    public void AttachedComponents_AreQueryableLikeAnyOther()
    {
        var world = new WorldState();
        world.SetComponent(world.GetOrCreateTypeEntity(PidSettings), "Nova.Components.Setting", [0xC0]);
        world.GetOrCreateTypeEntity("Nova.Components.MotorTelemetry");

        var settings = world.GetEntitiesWith(["Nova.Components.Setting"]);

        Assert.Single(settings);
        Assert.Equal(world.FindTypeEntity(PidSettings), settings[0]);
    }

    [Fact]
    public void DestroyEntity_ReleasesTypeName()
    {
        var world = new WorldState();
        var typeEntity = world.GetOrCreateTypeEntity(PidSettings);

        world.DestroyEntity(typeEntity);

        Assert.Null(world.FindTypeEntity(PidSettings));
        Assert.NotEqual(typeEntity, world.GetOrCreateTypeEntity(PidSettings));
    }

    [Fact]
    public void GetEntitiesWithAny_ReturnsUnion()
    {
        var world = new WorldState();
        var e1 = world.AllocateEntity();
        var e2 = world.AllocateEntity();
        var e3 = world.AllocateEntity();
        world.SetComponent(e1, "PidSettings", [1]);
        world.SetComponent(e2, "MotorSettings", [2]);
        world.SetComponent(e3, "Telemetry", [3]);

        var matched = world.GetEntitiesWithAny(["PidSettings", "MotorSettings"]);

        Assert.Equal([e1, e2], matched.Order());
    }
}
