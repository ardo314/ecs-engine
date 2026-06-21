using Engine.Coordinator;

namespace Engine.Tests;

public class WorldStateTests
{
    [Fact]
    public void AllocateEntity_ReturnsUniqueIds()
    {
        var world = new WorldState();
        var id1 = world.AllocateEntity();
        var id2 = world.AllocateEntity();
        var id3 = world.AllocateEntity();

        Assert.Equal(1UL, id1);
        Assert.Equal(2UL, id2);
        Assert.Equal(3UL, id3);
    }

    [Fact]
    public void AllocateEntity_MarksEntityAlive()
    {
        var world = new WorldState();
        var id = world.AllocateEntity();

        Assert.True(world.IsAlive(id));
        Assert.False(world.IsAlive(999));
    }

    [Fact]
    public void DestroyEntity_RemovesEntityAndComponents()
    {
        var world = new WorldState();
        var id = world.AllocateEntity();
        world.SetComponent(id, "Position", [1, 2, 3]);

        world.DestroyEntity(id);

        Assert.False(world.IsAlive(id));
        Assert.Null(world.GetComponent(id, "Position"));
        Assert.Equal(0, world.EntityCount);
    }

    [Fact]
    public void SetComponent_StoresAndRetrieves()
    {
        var world = new WorldState();
        var id = world.AllocateEntity();
        byte[] data = [10, 20, 30];

        world.SetComponent(id, "Velocity", data);

        var retrieved = world.GetComponent(id, "Velocity");
        Assert.NotNull(retrieved);
        Assert.Equal(data, retrieved);
    }

    [Fact]
    public void SetComponent_OverwritesExisting()
    {
        var world = new WorldState();
        var id = world.AllocateEntity();
        world.SetComponent(id, "Pos", [1]);
        world.SetComponent(id, "Pos", [2]);

        Assert.Equal([2], world.GetComponent(id, "Pos"));
    }

    [Fact]
    public void GetComponent_ReturnsNullForMissing()
    {
        var world = new WorldState();
        var id = world.AllocateEntity();

        Assert.Null(world.GetComponent(id, "NonExistent"));
        Assert.Null(world.GetComponent(999, "Position"));
    }

    [Fact]
    public void GetComponentTypes_ReturnsAllTypes()
    {
        var world = new WorldState();
        var id = world.AllocateEntity();
        world.SetComponent(id, "Position", [1]);
        world.SetComponent(id, "Velocity", [2]);

        var types = world.GetComponentTypes(id);

        Assert.Equal(2, types.Count);
        Assert.Contains("Position", types);
        Assert.Contains("Velocity", types);
    }

    [Fact]
    public void GetEntitiesWith_ReturnsMatchingEntities()
    {
        var world = new WorldState();
        var e1 = world.AllocateEntity();
        world.SetComponent(e1, "Position", [1]);
        world.SetComponent(e1, "Velocity", [2]);

        var e2 = world.AllocateEntity();
        world.SetComponent(e2, "Position", [3]);

        var e3 = world.AllocateEntity();
        world.SetComponent(e3, "Position", [4]);
        world.SetComponent(e3, "Velocity", [5]);
        world.SetComponent(e3, "Health", [6]);

        // Query for entities with both Position and Velocity
        var result = world.GetEntitiesWith(["Position", "Velocity"]);

        Assert.Equal(2, result.Count);
        Assert.Contains(e1, result);
        Assert.Contains(e3, result);
        Assert.DoesNotContain(e2, result);
    }

    [Fact]
    public void GetEntitiesWith_EmptyFilter_ReturnsAllEntities()
    {
        var world = new WorldState();
        var e1 = world.AllocateEntity();
        var e2 = world.AllocateEntity();

        var result = world.GetEntitiesWith([]);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void GetAllEntities_ReturnsAliveEntities()
    {
        var world = new WorldState();
        var e1 = world.AllocateEntity();
        var e2 = world.AllocateEntity();
        var e3 = world.AllocateEntity();
        world.DestroyEntity(e2);

        var all = world.GetAllEntities();

        Assert.Equal(2, all.Count);
        Assert.Contains(e1, all);
        Assert.Contains(e3, all);
    }

    [Fact]
    public void GetAllComponents_ReturnsAllComponentsForEntity()
    {
        var world = new WorldState();
        var id = world.AllocateEntity();
        world.SetComponent(id, "Position", [1, 2]);
        world.SetComponent(id, "Velocity", [3, 4]);

        var comps = world.GetAllComponents(id);

        Assert.NotNull(comps);
        Assert.Equal(2, comps.Count);
        Assert.Equal([1, 2], comps["Position"]);
        Assert.Equal([3, 4], comps["Velocity"]);
    }

    [Fact]
    public void GetAllComponents_ReturnsNullForDeadEntity()
    {
        var world = new WorldState();
        Assert.Null(world.GetAllComponents(999));
    }

    [Fact]
    public void EntityCount_TracksCorrectly()
    {
        var world = new WorldState();
        Assert.Equal(0, world.EntityCount);

        var e1 = world.AllocateEntity();
        world.AllocateEntity();
        Assert.Equal(2, world.EntityCount);

        world.DestroyEntity(e1);
        Assert.Equal(1, world.EntityCount);
    }
}
