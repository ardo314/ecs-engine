using Engine.Coordinator;
using Google.Protobuf.Reflection;

namespace Engine.Tests.Conformance;

/// <summary>
/// Pins the coordinator's schema hash implementation.
/// </summary>
/// <remarks>
/// There are three implementations of this algorithm — the coordinator's, the C# SDK's
/// and the TypeScript SDK's — and no shared code between any of them. The vectors in
/// <c>protocol/conformance/</c> are the only thing holding them together, so each one
/// checks itself against the same file.
///
/// To change the rendering deliberately, run with
/// <c>ECS_WRITE_CONFORMANCE_VECTORS=1</c> and commit the result on its own. CI fails if
/// the file moves without that.
/// </remarks>
public class SchemaHashConformanceTests
{
    /// <summary>
    /// Chosen to cover every place runtimes disagree, plus real protocol types so the
    /// vectors break if the wire contract shifts underneath them.
    /// </summary>
    internal static readonly MessageDescriptor[] Cases =
    [
        Testing.V1.ConformanceLeaf.Descriptor,
        Testing.V1.ConformanceLeafTwin.Descriptor,
        Testing.V1.ConformanceMaps.Descriptor,
        Testing.V1.ConformancePresence.Descriptor,
        Testing.V1.ConformanceOneof.Descriptor,
        Testing.V1.ConformanceEnums.Descriptor,
        Testing.V1.ConformanceRecursive.Descriptor,
        Testing.V1.ConformanceWellKnown.Descriptor,
        Testing.V1.ConformanceScalars.Descriptor,
        Testing.V1.ConformanceOrdering.Descriptor,
        Ecs.V1.ComponentInfo.Descriptor,
        Ecs.V1.ComponentSchema.Descriptor,
        Ecs.V1.ParentRef.Descriptor,
        Movement.V1.Position.Descriptor,
        Ecs.Protocol.V1.ComponentBatch.Descriptor,
        Ecs.Protocol.V1.StructuralCommand.Descriptor,
    ];

    public SchemaHashConformanceTests()
    {
        if (Environment.GetEnvironmentVariable("ECS_WRITE_CONFORMANCE_VECTORS") != "1") return;

        var vectors = new SchemaHashVectors
        {
            Version = SchemaHashVectors.CurrentVersion,
            Algorithm = SchemaHashVectors.CurrentAlgorithm,
            Spec = SchemaHashVectors.SpecReference,
            Cases = [.. Cases
                .Select(d => new SchemaHashCase
                {
                    LogicalName = d.FullName,
                    SchemaHash = SchemaHashVectors.Format(SchemaHash.Of(d)),
                    Canonical = SchemaHash.Canonicalize(d),
                })
                .OrderBy(c => c.LogicalName, StringComparer.Ordinal)],
        };

        Directory.CreateDirectory(Path.GetDirectoryName(SchemaHashVectors.Path)!);
        File.WriteAllText(
            SchemaHashVectors.Path,
            System.Text.Json.JsonSerializer.Serialize(vectors, SchemaHashVectors.Json) + "\n");
    }

    [Fact]
    public void VectorFile_DeclaresTheAlgorithmItPins()
    {
        var vectors = SchemaHashVectors.Load();

        Assert.Equal(SchemaHashVectors.CurrentVersion, vectors.Version);
        Assert.Equal(SchemaHashVectors.CurrentAlgorithm, vectors.Algorithm);
        Assert.NotEmpty(vectors.Cases);
    }

    [Fact]
    public void EveryCoveredTypeIsPinned()
    {
        var pinned = SchemaHashVectors.Load().Cases
            .Select(c => c.LogicalName)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var descriptor in Cases)
            Assert.Contains(descriptor.FullName, pinned);
    }

    [Fact]
    public void CanonicalFormMatchesTheVectors()
    {
        foreach (var vector in SchemaHashVectors.Load().Cases)
        {
            var descriptor = Cases.Single(d => d.FullName == vector.LogicalName);

            // Compared before the hash: a rendering diff says which line is wrong.
            Assert.Equal(vector.Canonical, SchemaHash.Canonicalize(descriptor));
        }
    }

    [Fact]
    public void HashMatchesTheVectors()
    {
        foreach (var vector in SchemaHashVectors.Load().Cases)
        {
            var descriptor = Cases.Single(d => d.FullName == vector.LogicalName);

            Assert.Equal(vector.SchemaHash, SchemaHashVectors.Format(SchemaHash.Of(descriptor)));
        }
    }

    [Fact]
    public void HashIsDerivedFromTheDescriptorSetAlone()
    {
        // The path a foreign implementation takes: bytes in, hash out, no reflection.
        foreach (var descriptor in Cases)
        {
            var set = Descriptors.FileDescriptorSetFor(descriptor);

            Assert.Equal(SchemaHash.Of(descriptor), SchemaHash.Of(set, descriptor.FullName));
        }
    }
}
