using Engine.Core;
using Engine.Core.Messages;
using Google.Protobuf;
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
            Target = CommandTarget.OfComponentType("nova.v1.PidSettings"),
            ComponentType = ComponentTypes.Info,
            Data = [1, 2, 3]
        };

        var result = RoundTrip(request);

        Assert.Equal("nova.v1.PidSettings", result.Target.ComponentType);
        Assert.Equal(0UL, result.Target.EntityId);
        Assert.Equal(request.ComponentType, result.ComponentType);
    }

    [Fact]
    public void ComponentAddRequest_RoundTripsEntityTarget()
    {
        var request = new ComponentAddRequest
        {
            Target = new CommandTarget(42),
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
            Target = CommandTarget.OfComponentType("nova.v1.PidSettings"),
            ComponentType = "nova.v1.Setting"
        };

        var result = RoundTrip(request);

        Assert.Equal("nova.v1.PidSettings", result.Target.ComponentType);
    }

    [Fact]
    public void ComponentInfo_RoundTripsThroughProtobuf()
    {
        var info = new Ecs.V1.ComponentInfo { TypeName = "nova.v1.PidSettings" };

        var result = Ecs.V1.ComponentInfo.Parser.ParseFrom(info.ToByteArray());

        Assert.Equal("nova.v1.PidSettings", result.TypeName);
    }
}
