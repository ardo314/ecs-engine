using Engine.Core;
using Engine.Core.Messages;
using MessagePack;

namespace Engine.Tests.Unit;

[Trait("Category", "Unit")]
public class MessageSerializationTests
{
    public MessageSerializationTests() => Serialization.Initialize();

    private static T RoundTrip<T>(T value) =>
        MessagePackSerializer.Deserialize<T>(
            MessagePackSerializer.Serialize(value, Serialization.Options), Serialization.Options);

    [Fact]
    public void ComponentAddRequest_RoundTripsComponentTypeTarget()
    {
        var request = new ComponentAddRequest
        {
            Target = CommandTarget.OfComponentType("Nova.Components.PidSettings"),
            ComponentType = ComponentInfo.Type,
            Data = [1, 2, 3]
        };

        var result = RoundTrip(request);

        Assert.Equal("Nova.Components.PidSettings", result.Target.ComponentType);
        Assert.Equal(0UL, result.Target.EntityId);
        Assert.Equal(request.ComponentType, result.ComponentType);
    }

    [Fact]
    public void ComponentAddRequest_RoundTripsEntityTarget()
    {
        var request = new ComponentAddRequest
        {
            Target = new Entity(42),
            ComponentType = "Position",
            Data = [7]
        };

        var result = RoundTrip(request);

        Assert.Equal(42UL, result.Target.EntityId);
        Assert.Null(result.Target.ComponentType);
    }

    [Fact]
    public void ComponentRemoveRequest_RoundTripsTarget()
    {
        var request = new ComponentRemoveRequest
        {
            Target = CommandTarget.OfComponentType("Nova.Components.PidSettings"),
            ComponentType = "Nova.Components.Setting"
        };

        var result = RoundTrip(request);

        Assert.Equal("Nova.Components.PidSettings", result.Target.ComponentType);
    }

    [Fact]
    public void ComponentInfo_RoundTrips()
    {
        var result = RoundTrip(new ComponentInfo("Nova.Components.PidSettings"));

        Assert.Equal("Nova.Components.PidSettings", result.TypeName);
    }
}
