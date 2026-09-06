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
    public void CanonicalForm_NamesTheRootFirst()
    {
        var canonical = SchemaHash.Canonicalize(ComponentSchema.Descriptor);

        Assert.StartsWith("message ecs.v1.ComponentSchema\n", canonical, StringComparison.Ordinal);
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
    public void CanonicalForm_RecordsEveryFieldWithItsNumberAndType()
    {
        var canonical = SchemaHash.Canonicalize(TestPosition.Descriptor);

        Assert.Contains("field 1 x singular TYPE_FLOAT\n", canonical, StringComparison.Ordinal);
        Assert.Contains("field 2 y singular TYPE_FLOAT\n", canonical, StringComparison.Ordinal);
    }

    [Fact]
    public void CanonicalForm_IncludesReferencedMessages()
    {
        // ParentRef holds an EntityId, so a change to EntityId changes ParentRef's identity.
        var canonical = SchemaHash.Canonicalize(ParentRef.Descriptor);

        Assert.Contains("message ecs.v1.EntityId\n", canonical, StringComparison.Ordinal);
        Assert.Contains("field 1 id singular TYPE_UINT64\n", canonical, StringComparison.Ordinal);
    }

    [Fact]
    public void CanonicalForm_UsesCanonicalProtobufTypeNamesNotThisRuntimeSpelling()
    {
        // The rendering is a cross-language contract, so it cannot depend on how C#
        // happens to name its FieldType enum members.
        var canonical = SchemaHash.Canonicalize(ConformanceScalars.Descriptor);

        foreach (var expected in new[]
                 {
                     "TYPE_DOUBLE", "TYPE_FLOAT", "TYPE_INT64", "TYPE_UINT64", "TYPE_INT32",
                     "TYPE_FIXED64", "TYPE_FIXED32", "TYPE_BOOL", "TYPE_STRING", "TYPE_BYTES",
                     "TYPE_UINT32", "TYPE_SFIXED32", "TYPE_SFIXED64", "TYPE_SINT32", "TYPE_SINT64",
                 })
        {
            Assert.Contains(expected, canonical, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void MapFields_AreRenderedAsTheDescriptorModelsThem()
    {
        // The place runtimes disagree most: C# reports a map as repeated with a synthetic
        // entry type, protobuf-es hides the entry. Working in descriptor space means there
        // is no special case at all.
        var canonical = SchemaHash.Canonicalize(ConformanceMaps.Descriptor);

        Assert.Contains(
            "field 1 counts repeated TYPE_MESSAGE testing.v1.ConformanceMaps.CountsEntry\n",
            canonical, StringComparison.Ordinal);
        Assert.Contains("message testing.v1.ConformanceMaps.CountsEntry\n", canonical, StringComparison.Ordinal);
        Assert.Contains("field 1 key singular TYPE_STRING\n", canonical, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitAndImplicitPresenceHashDifferently()
    {
        var canonical = SchemaHash.Canonicalize(ConformancePresence.Descriptor);

        Assert.Contains("field 1 implicit_field singular TYPE_STRING\n", canonical, StringComparison.Ordinal);
        Assert.Contains("field 2 explicit_field optional TYPE_STRING\n", canonical, StringComparison.Ordinal);
        Assert.Contains("field 3 repeated_field repeated TYPE_STRING\n", canonical, StringComparison.Ordinal);
    }

    [Fact]
    public void SyntheticOneofsAreNotPartOfTheShape()
    {
        // proto3 `optional` generates a oneof named `_explicit_field`. The presence it
        // encodes is already carried by the field's label, so rendering it would count
        // the same fact twice — and no other runtime exposes it the same way.
        var canonical = SchemaHash.Canonicalize(ConformancePresence.Descriptor);

        Assert.DoesNotContain("oneof", canonical, StringComparison.Ordinal);
    }

    [Fact]
    public void RealOneofsArePartOfTheShape()
    {
        var canonical = SchemaHash.Canonicalize(ConformanceOneof.Descriptor);

        Assert.Contains("oneof 0 choice\n", canonical, StringComparison.Ordinal);
    }

    [Fact]
    public void FieldsAndEnumValuesAreOrderedByNumberNotByDeclaration()
    {
        var fields = SchemaHash.Canonicalize(ConformanceOrdering.Descriptor);
        Assert.Equal(
            "message testing.v1.ConformanceOrdering\n" +
            "field 1 a singular TYPE_STRING\n" +
            "field 20 b singular TYPE_STRING\n" +
            "field 30 c singular TYPE_STRING\n",
            fields);

        var values = SchemaHash.Canonicalize(ConformanceEnums.Descriptor);
        Assert.Contains(
            "value 0 CONFORMANCE_COLOUR_UNSPECIFIED\nvalue 3 CONFORMANCE_COLOUR_GREEN\n" +
            "value 7 CONFORMANCE_COLOUR_RED\n",
            values, StringComparison.Ordinal);
    }

    [Fact]
    public void MutualRecursionTerminates()
    {
        // google.protobuf.Struct references Value references ListValue references Value.
        var canonical = SchemaHash.Canonicalize(ConformanceWellKnown.Descriptor);

        Assert.Contains("message google.protobuf.ListValue\n", canonical, StringComparison.Ordinal);
        Assert.Equal(1, Occurrences(canonical, "message google.protobuf.Value\n"));
    }

    [Fact]
    public void Descriptors_RejectASetThatDoesNotContainTheName()
    {
        var set = Descriptors.FileDescriptorSetFor(TestPosition.Descriptor);

        Assert.Throws<InvalidDescriptorSetException>(
            () => Descriptors.Resolve(set, "testing.v1.NotInThisFile"));

        Assert.Throws<InvalidDescriptorSetException>(
            () => SchemaHash.Of(set, "testing.v1.NotInThisFile"));
    }

    private static int Occurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }
}
