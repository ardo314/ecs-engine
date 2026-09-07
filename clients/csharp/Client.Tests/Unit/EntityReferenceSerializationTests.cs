using Ecs.V1;
using Engine.Core;
using Google.Protobuf;

namespace Client.Tests.Unit;

/// <summary>
/// Reference components hold an <c>ecs.v1.Entity</c> message on the wire. The SDK's
/// <see cref="Entity"/> struct converts to and from it so authoring code never sees
/// the generated type.
/// </summary>
[Trait("Category", "Unit")]
public class EntityReferenceSerializationTests
{
    [Fact]
    public void ReferenceComponent_RoundTrips()
    {
        var reference = new ParentRef { Parent = new Entity(9) };

        var result = ParentRef.Parser.ParseFrom(reference.ToByteArray());

        Assert.Equal(new Entity(9), (Entity)result.Parent);
    }

    [Fact]
    public void UnsetReference_ReadsAsEntityZero()
    {
        var result = ParentRef.Parser.ParseFrom(new ParentRef().ToByteArray());

        Assert.Null(result.Parent);
        Assert.Equal(new Entity(0), (Entity)result.Parent);
    }

    [Fact]
    public void EntityZero_RoundTrips()
    {
        var reference = new ParentRef { Parent = new Entity(0) };

        var result = ParentRef.Parser.ParseFrom(reference.ToByteArray());

        Assert.Equal(new Entity(0), (Entity)result.Parent);
    }
}
