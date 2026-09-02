using Engine.Core;
using Engine.Core.Messages;
using Google.Protobuf;
using MessagePack;
using Testing.V1;

namespace Client.Tests.Unit;

/// <summary>
/// A protobuf message whose fields are all at their default value encodes to zero
/// bytes, so a shard cannot use payload length to mean "absent". These tests walk a
/// component through the coordinator's shard encoding and back.
/// </summary>
[Trait("Category", "Unit")]
public class ShardPresenceTests
{
    public ShardPresenceTests() => Serialization.Initialize();

    private static readonly string Position = ComponentTypeId.Of<TestPosition>().TypeName;
    private static readonly string Velocity = ComponentTypeId.Of<TestVelocity>().TypeName;

    /// <summary>Mirrors how TickLoop packs a shard and SystemRunner unpacks it.</summary>
    private static Dictionary<string, (ulong[] Entities, byte[][] Data)> RoundTripShards(
        ulong[] entities,
        params (string TypeName, byte[]?[] Data)[] entries)
    {
        var shards = new Dictionary<string, (ulong[], byte[][])>();
        foreach (var (typeName, data) in entries)
        {
            var packed = MessagePackSerializer.Serialize(data, Serialization.Options);
            shards[typeName] = (entities, MessagePackSerializer.Deserialize<byte[][]>(packed, Serialization.Options));
        }
        return shards;
    }

    [Fact]
    public void AllDefaultComponent_EncodesToZeroBytes()
    {
        Assert.Empty(new TestPosition().ToByteArray());
    }

    [Fact]
    public void ZeroLengthPayload_CountsAsPresent()
    {
        var query = new EntityQuery()
            .With(Query.ReadWrite<TestPosition>())
            .With(Query.ReadOnly<TestVelocity>());
        query.Freeze();

        // Entity 1 sits at the origin, so its Position is zero bytes on the wire.
        var shards = RoundTripShards(
            [1],
            (Position, [new TestPosition().ToByteArray()]),
            (Velocity, [new TestVelocity { Vx = 1f }.ToByteArray()]));

        query.Populate(shards, tickId: 1);

        Assert.Single(query.Entities);
        Assert.Equal(0f, query.Get<TestPosition>(new Entity(1)).X);
    }

    [Fact]
    public void NullPayload_CountsAsAbsent()
    {
        var query = new EntityQuery()
            .With(Query.ReadWrite<TestPosition>())
            .With(Query.ReadOnly<TestVelocity>());
        query.Freeze();

        var shards = RoundTripShards(
            [1, 2],
            (Position, [new TestPosition().ToByteArray(), new TestPosition { X = 5f }.ToByteArray()]),
            (Velocity, [null, new TestVelocity { Vx = 1f }.ToByteArray()]));

        query.Populate(shards, tickId: 1);

        Assert.Single(query.Entities);
        Assert.Equal(2UL, query.Entities[0].Id);
    }
}
