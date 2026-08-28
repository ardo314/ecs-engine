using Engine.Core;
using MessagePack;

namespace Client.Tests.Unit;

/// <summary>
/// The entity formatter only matters where components are actually deserialized —
/// the client SDK. The coordinator passes component payloads through as opaque bytes.
/// </summary>
[Trait("Category", "Unit")]
public class EntityReferenceSerializationTests
{
    public EntityReferenceSerializationTests() => Serialization.Initialize();

    private static T RoundTrip<T>(T value) =>
        MessagePackSerializer.Deserialize<T>(
            MessagePackSerializer.Serialize(value, Serialization.Options), Serialization.Options);

    [Fact]
    public void Entity_SerializesAsBareInteger()
    {
        var json = MessagePackSerializer.ConvertToJson(
            MessagePackSerializer.Serialize(new Entity(42), Serialization.Options));

        Assert.Equal("42", json);
    }

    [Fact]
    public void ReferenceComponent_SerializesEntityFieldAsBareInteger()
    {
        var json = MessagePackSerializer.ConvertToJson(
            MessagePackSerializer.Serialize(new ParentRef(new Entity(7)), Serialization.Options));

        Assert.Equal("{\"Parent\":7}", json);
    }

    [Fact]
    public void ReferenceComponent_RoundTrips()
    {
        var result = RoundTrip(new ParentRef(new Entity(9)));

        Assert.Equal(new Entity(9), result.Parent);
    }

    [Fact]
    public void Entity_RoundTripsZero()
    {
        Assert.Equal(new Entity(0), RoundTrip(new Entity(0)));
    }
}
