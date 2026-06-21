using Client;
using Engine.Core;
using Engine.Core.Messages;
using MessagePack;

namespace Client.Tests;

public record TestPosition : IComponent
{
    public float X { get; init; }
    public float Y { get; init; }
}

public record TestVelocity : IComponent
{
    public float Vx { get; init; }
    public float Vy { get; init; }
}

public class ComponentBufferTests
{
    public ComponentBufferTests()
    {
        Serialization.Initialize();
    }

    [Fact]
    public void GetComponents_EmptyBuffer_ReturnsEmpty()
    {
        var buffer = new ComponentBuffer();
        var result = buffer.GetComponents<TestPosition>();
        Assert.Empty(result);
    }

    [Fact]
    public void AddShard_ThenGetComponents_ReturnsDeserializedData()
    {
        var buffer = new ComponentBuffer();
        var entities = new ulong[] { 1, 2 };
        var positions = new[]
        {
            MessagePackSerializer.Serialize<TestPosition>(new() { X = 1.0f, Y = 2.0f }),
            MessagePackSerializer.Serialize<TestPosition>(new() { X = 3.0f, Y = 4.0f })
        };

        var shard = new ComponentShard
        {
            TickId = 42,
            Entities = entities,
            ComponentType = ComponentTypeId.Of<TestPosition>().TypeName,
            Data = MessagePackSerializer.Serialize(positions)
        };

        buffer.AddShard(shard);

        var result = buffer.GetComponents<TestPosition>();
        Assert.Equal(2, result.Length);
        Assert.Equal(1UL, result[0].Entity.Id);
        Assert.Equal(1.0f, result[0].Component.X);
        Assert.Equal(2.0f, result[0].Component.Y);
        Assert.Equal(2UL, result[1].Entity.Id);
        Assert.Equal(3.0f, result[1].Component.X);
    }

    [Fact]
    public void AddShard_SetsTickId()
    {
        var buffer = new ComponentBuffer();
        var shard = new ComponentShard
        {
            TickId = 99,
            Entities = [1],
            ComponentType = ComponentTypeId.Of<TestPosition>().TypeName,
            Data = MessagePackSerializer.Serialize(new[] { MessagePackSerializer.Serialize<TestPosition>(new()) })
        };

        buffer.AddShard(shard);
        Assert.Equal(99UL, buffer.TickId);
    }

    [Fact]
    public void SetComponent_ThenFlush_ReturnsMutations()
    {
        var buffer = new ComponentBuffer();
        buffer.SetTickId(10);

        var entity = new Entity(5);
        buffer.SetComponent(entity, new TestPosition { X = 10.0f, Y = 20.0f });

        var mutations = buffer.FlushMutations();
        Assert.Single(mutations);
        Assert.Equal(ComponentTypeId.Of<TestPosition>().TypeName, mutations[0].ComponentType);
        Assert.Equal(10UL, mutations[0].TickId);
        Assert.Single(mutations[0].Entities);
        Assert.Equal(5UL, mutations[0].Entities[0]);
    }

    [Fact]
    public void FlushMutations_ClearsMutationBuffer()
    {
        var buffer = new ComponentBuffer();
        buffer.SetTickId(1);
        buffer.SetComponent(new Entity(1), new TestPosition { X = 1.0f, Y = 1.0f });

        var first = buffer.FlushMutations();
        Assert.Single(first);

        var second = buffer.FlushMutations();
        Assert.Empty(second);
    }

    [Fact]
    public void SetComponent_MultipleEntities_GroupsByType()
    {
        var buffer = new ComponentBuffer();
        buffer.SetTickId(1);

        buffer.SetComponent(new Entity(1), new TestPosition { X = 1.0f, Y = 2.0f });
        buffer.SetComponent(new Entity(2), new TestPosition { X = 3.0f, Y = 4.0f });

        var mutations = buffer.FlushMutations();
        Assert.Single(mutations); // One ComponentChanges per type
        Assert.Equal(2, mutations[0].Entities.Length);
    }

    [Fact]
    public void SetComponent_MultipleTypes_ProducesMultipleMutations()
    {
        var buffer = new ComponentBuffer();
        buffer.SetTickId(1);

        buffer.SetComponent(new Entity(1), new TestPosition { X = 1.0f, Y = 2.0f });
        buffer.SetComponent(new Entity(1), new TestVelocity { Vx = 5.0f, Vy = 6.0f });

        var mutations = buffer.FlushMutations();
        Assert.Equal(2, mutations.Count);
    }

    [Fact]
    public void SetComponent_OverwritesSameEntity()
    {
        var buffer = new ComponentBuffer();
        buffer.SetTickId(1);

        buffer.SetComponent(new Entity(1), new TestPosition { X = 1.0f, Y = 2.0f });
        buffer.SetComponent(new Entity(1), new TestPosition { X = 99.0f, Y = 100.0f });

        var mutations = buffer.FlushMutations();
        Assert.Single(mutations);
        Assert.Single(mutations[0].Entities);

        // Deserialize the data to verify overwrite
        var dataArray = MessagePackSerializer.Deserialize<byte[][]>(mutations[0].Data);
        var pos = MessagePackSerializer.Deserialize<TestPosition>(dataArray[0]);
        Assert.Equal(99.0f, pos.X);
        Assert.Equal(100.0f, pos.Y);
    }

    [Fact]
    public void Clear_ResetsEverything()
    {
        var buffer = new ComponentBuffer();

        var shard = new ComponentShard
        {
            TickId = 42,
            Entities = [1],
            ComponentType = ComponentTypeId.Of<TestPosition>().TypeName,
            Data = MessagePackSerializer.Serialize(new[] { MessagePackSerializer.Serialize<TestPosition>(new()) })
        };
        buffer.AddShard(shard);
        buffer.SetComponent(new Entity(1), new TestPosition { X = 1.0f, Y = 1.0f });

        buffer.Clear();

        Assert.Equal(0UL, buffer.TickId);
        Assert.Empty(buffer.GetComponents<TestPosition>());
        Assert.Empty(buffer.FlushMutations());
    }

    [Fact]
    public void GetRaw_ReturnsRawData()
    {
        var buffer = new ComponentBuffer();
        var typeName = ComponentTypeId.Of<TestPosition>().TypeName;

        var shard = new ComponentShard
        {
            TickId = 1,
            Entities = [10, 20],
            ComponentType = typeName,
            Data = MessagePackSerializer.Serialize(new[]
            {
                MessagePackSerializer.Serialize<TestPosition>(new() { X = 1f, Y = 2f }),
                MessagePackSerializer.Serialize<TestPosition>(new() { X = 3f, Y = 4f })
            })
        };
        buffer.AddShard(shard);

        var raw = buffer.GetRaw(typeName);
        Assert.NotNull(raw);
        Assert.Equal(2, raw.Value.Entities.Length);
        Assert.Equal(10UL, raw.Value.Entities[0]);
    }

    [Fact]
    public void GetRaw_MissingType_ReturnsNull()
    {
        var buffer = new ComponentBuffer();
        Assert.Null(buffer.GetRaw("NonExistent.Component"));
    }

    [Fact]
    public void MultipleShards_DifferentTypes_AllAccessible()
    {
        var buffer = new ComponentBuffer();

        var posShard = new ComponentShard
        {
            TickId = 1,
            Entities = [1],
            ComponentType = ComponentTypeId.Of<TestPosition>().TypeName,
            Data = MessagePackSerializer.Serialize(new[] { MessagePackSerializer.Serialize<TestPosition>(new() { X = 1f, Y = 2f }) })
        };
        var velShard = new ComponentShard
        {
            TickId = 1,
            Entities = [1],
            ComponentType = ComponentTypeId.Of<TestVelocity>().TypeName,
            Data = MessagePackSerializer.Serialize(new[] { MessagePackSerializer.Serialize<TestVelocity>(new() { Vx = 3f, Vy = 4f }) })
        };

        buffer.AddShard(posShard);
        buffer.AddShard(velShard);

        var positions = buffer.GetComponents<TestPosition>();
        var velocities = buffer.GetComponents<TestVelocity>();
        Assert.Single(positions);
        Assert.Single(velocities);
        Assert.Equal(1.0f, positions[0].Component.X);
        Assert.Equal(3.0f, velocities[0].Component.Vx);
    }
}
