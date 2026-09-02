using Ecs.V1;
using Engine.Core;
using Engine.Core.Messages;
using Engine.Coordinator;

namespace Engine.Tests.Unit;

[Trait("Category", "Unit")]
public class ComponentTypeTests
{
    private const string PidSettings = "nova.v1.PidSettings";

    [Fact]
    public void GetOrCreateTypeEntity_CreatesEntityWithInfo()
    {
        var world = new WorldState();
        var typeEntity = world.GetOrCreateTypeEntity(PidSettings);

        var info = ComponentInfo.Parser.ParseFrom(
            world.GetComponent(typeEntity, ComponentTypes.Info)!);

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
        world.SetComponent(world.GetOrCreateTypeEntity(PidSettings), "nova.v1.Setting", [0xC0]);
        world.GetOrCreateTypeEntity("nova.v1.MotorTelemetry");

        var settings = world.GetEntitiesWith(["nova.v1.Setting"]);

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

    // ── Tag joins ──────────────────────────────────────────────

    [Fact]
    public void GetTypesTaggedWith_ReturnsTypesCarryingTheTag()
    {
        var world = new WorldState();
        world.SetComponent(world.GetOrCreateTypeEntity(PidSettings), "nova.v1.Setting", [0xC0]);
        world.SetComponent(world.GetOrCreateTypeEntity("nova.v1.GainSettings"), "nova.v1.Setting", [0xC0]);
        world.GetOrCreateTypeEntity("nova.v1.MotorTelemetry");

        var tagged = world.GetTypesTaggedWith("nova.v1.Setting");

        Assert.Equal(2, tagged.Count);
        Assert.Contains(PidSettings, tagged);
        Assert.Contains("nova.v1.GainSettings", tagged);
    }

    [Fact]
    public void GetEntitiesMatchingQueries_TaggedTypes_MatchesEntitiesCarryingATaggedComponent()
    {
        var world = new WorldState();
        world.SetComponent(world.GetOrCreateTypeEntity(PidSettings), "nova.v1.Setting", [0xC0]);

        var withSetting = world.AllocateEntity();
        world.SetComponent(withSetting, PidSettings, [1]);
        var withoutSetting = world.AllocateEntity();
        world.SetComponent(withoutSetting, "nova.v1.MotorTelemetry", [2]);

        var matches = world.GetEntitiesMatchingQueries(
            [new QueryDescriptor { TaggedTypes = ["nova.v1.Setting"] }]);

        Assert.Single(matches);
        Assert.Equal(withSetting, matches[0]);
    }

    [Fact]
    public void GetEntitiesMatchingQueries_TaggedTypes_UntaggedTypeMatchesNothing()
    {
        var world = new WorldState();
        var entity = world.AllocateEntity();
        world.SetComponent(entity, PidSettings, [1]);

        var matches = world.GetEntitiesMatchingQueries(
            [new QueryDescriptor { TaggedTypes = ["nova.v1.Setting"] }]);

        Assert.Empty(matches);
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
