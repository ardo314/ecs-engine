using Ecs.Protocol;
using Ecs.Protocol.V1;
using Engine.Core;
using Google.Protobuf;
using Testing.V1;

namespace Client.Tests.Unit;

/// <summary>
/// The SDK's half of the type contract: a generated protobuf message carries enough
/// information to become an ECS component with no hand-maintained metadata.
/// </summary>
public class ComponentTypeTests
{
    [Fact]
    public void Name_IsTheProtobufFullName()
    {
        Assert.Equal("testing.v1.TestPosition", ComponentType<TestPosition>.Name);
    }

    [Fact]
    public void SchemaHash_AgreesWithWhatTheCoordinatorWouldCompute()
    {
        Assert.Equal(SchemaHash.Of(TestPosition.Descriptor), ComponentType<TestPosition>.SchemaHash);
    }

    [Fact]
    public void Declaration_CarriesIdentityAndTransitivelyClosedDescriptors()
    {
        var declaration = ComponentType<TestPosition>.Declaration;

        Assert.Equal("testing.v1.TestPosition", declaration.Type.LogicalName);
        Assert.Equal(ComponentType<TestPosition>.SchemaHash, declaration.Type.SchemaHash);

        var set = Google.Protobuf.Reflection.FileDescriptorSet.Parser.ParseFrom(
            declaration.FileDescriptorSet);

        Assert.Contains(set.File, f => f.Name == "testing/v1/testing.proto");

        // testing.proto imports ecs/v1/component.proto, which imports the well-known
        // types. All of it has to be present or the coordinator cannot rebuild the type.
        Assert.Contains(set.File, f => f.Name == "ecs/v1/component.proto");
        Assert.Contains(set.File, f => f.Name == "google/protobuf/descriptor.proto");
    }

    [Fact]
    public void Declaration_ReadsTheTypeOwnDescriptionAttachments()
    {
        var description = ComponentType<TestDescribed>.Declaration.Description;

        Assert.Contains(description, v => v.Type.LogicalName == "testing.v1.TestSetting");

        var category = Assert.Single(description, v => v.Type.LogicalName == "testing.v1.TestCategory");
        Assert.Equal("Control", TestCategory.Parser.ParseFrom(category.Payload).Name);
    }

    [Fact]
    public void DescriptionAttachments_CarryTheirOwnSchemaIdentity()
    {
        // An attachment is an ordinary component on the type entity, so it needs the
        // same exact identity as anything else the world stores.
        var category = ComponentType<TestDescribed>.Declaration.Description
            .Single(v => v.Type.LogicalName == "testing.v1.TestCategory");

        Assert.Equal(SchemaHash.Of(TestCategory.Descriptor), category.Type.SchemaHash);
    }

    [Fact]
    public void AnUndescribedType_DeclaresNoAttachments()
    {
        Assert.Empty(ComponentType<TestUndescribed>.Declaration.Description);
    }

    [Fact]
    public void Value_TagsThePayloadWithItsType()
    {
        var value = ComponentType<TestPosition>.Value(new TestPosition { X = 1.5f });

        Assert.Equal("testing.v1.TestPosition", value.Type.LogicalName);
        Assert.Equal(1.5f, TestPosition.Parser.ParseFrom(value.Payload).X);
    }
}

public class SchemaBindingsTests
{
    [Fact]
    public void Require_ReturnsTheBoundId()
    {
        var bindings = new SchemaBindings();
        bindings.Add("testing.v1.TestPosition", 17);

        Assert.Equal(17u, bindings.Require<TestPosition>());
        Assert.Equal("testing.v1.TestPosition", bindings.NameOf(17));
    }

    [Fact]
    public void Require_ExplainsItselfWhenATypeWasNeverRegistered()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => new SchemaBindings().Require<TestPosition>());

        Assert.Contains("testing.v1.TestPosition", error.Message, StringComparison.Ordinal);
        Assert.Contains("never registered", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NameOf_AnUnknownIdDoesNotThrow()
    {
        Assert.Equal("<unbound:5>", new SchemaBindings().NameOf(5));
    }
}

public class QueryAccessTests
{
    [Fact]
    public void ReadAndWrite_ProduceTheProtocolLevelRequest()
    {
        var read = Query.Read<TestPosition>();
        var write = Query.Write<TestVelocity>();

        Assert.Equal("testing.v1.TestPosition", read.Name);
        Assert.Equal(Access.Read, read.Access);
        Assert.False(read.IsWrite);

        Assert.Equal(Access.Write, write.Access);
        Assert.True(write.IsWrite);
        Assert.Equal(ComponentType<TestVelocity>.SchemaHash, write.Type.SchemaHash);
    }
}
