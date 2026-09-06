using Ecs.Protocol;
using Ecs.V1;
using Testing.V1;

namespace Engine.Tests.Unit;

/// <summary>
/// The schema hash is the engine's notion of type identity, so what it is and is not
/// sensitive to is a contract, not an implementation detail.
/// </summary>
public class SchemaHashTests
{
    [Fact]
    public void SchemaHash_IsStableAcrossCalls()
    {
        Assert.Equal(SchemaHash.Of(TestPosition.Descriptor), SchemaHash.Of(TestPosition.Descriptor));
    }

    [Fact]
    public void SchemaHash_DistinguishesTypesWithIdenticalShape()
    {
        // TestPositionTwin has exactly the same fields as TestPosition. They are still
        // different types, so the root's full name has to be part of the identity.
        Assert.NotEqual(
            SchemaHash.Of(TestPosition.Descriptor),
            SchemaHash.Of(TestPositionTwin.Descriptor));
    }

    [Fact]
    public void SchemaHash_DistinguishesDifferentShapes()
    {
        Assert.NotEqual(SchemaHash.Of(TestPosition.Descriptor), SchemaHash.Of(ComponentInfo.Descriptor));
    }

    [Fact]
    public void SchemaHash_SurvivesARoundTripThroughDescriptors()
    {
        // A process that has never compiled the type must arrive at the same value, or
        // exact-match registration would reject everything.
        var rebuilt = Descriptors.Resolve(
            Descriptors.FileDescriptorSetFor(TestPosition.Descriptor), TestPosition.Descriptor.FullName);

        Assert.Equal(SchemaHash.Of(TestPosition.Descriptor), SchemaHash.Of(rebuilt));
    }

    [Fact]
    public void CanonicalForm_NamesTheRootFirst()
    {
        var canonical = SchemaHash.Canonicalize(ComponentSchema.Descriptor);

        Assert.StartsWith("message ecs.v1.ComponentSchema\n", canonical, StringComparison.Ordinal);
    }

    [Fact]
    public void CanonicalForm_RecordsEveryFieldWithItsNumberAndType()
    {
        var canonical = SchemaHash.Canonicalize(TestPosition.Descriptor);

        Assert.Contains("field 1 x optional float\n", canonical, StringComparison.Ordinal);
        Assert.Contains("field 2 y optional float\n", canonical, StringComparison.Ordinal);
    }

    [Fact]
    public void CanonicalForm_IncludesReferencedMessages()
    {
        // ParentRef holds an EntityId, so a change to EntityId changes ParentRef's identity.
        var canonical = SchemaHash.Canonicalize(ParentRef.Descriptor);

        Assert.Contains("message ecs.v1.EntityId\n", canonical, StringComparison.Ordinal);
        Assert.Contains("field 1 id optional uint64\n", canonical, StringComparison.Ordinal);
    }

    [Fact]
    public void Descriptors_RejectASetThatDoesNotContainTheName()
    {
        var set = Descriptors.FileDescriptorSetFor(TestPosition.Descriptor);

        Assert.Throws<InvalidDescriptorSetException>(
            () => Descriptors.Resolve(set, "testing.v1.NotInThisFile"));
    }
}
