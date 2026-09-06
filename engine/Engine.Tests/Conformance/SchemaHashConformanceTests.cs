using System.Text.Json;
using Ecs.Protocol;
using Ecs.Protocol.Conformance;
using Google.Protobuf.Reflection;

namespace Engine.Tests.Conformance;

/// <summary>
/// Pins the schema hash so a second implementation has something to be right about.
/// </summary>
/// <remarks>
/// These vectors are the whole reason the protocol can be implemented more than once.
/// The canonical form cannot be generated from the schema — it is an algorithm — so it
/// is pinned here instead, and every language checks itself against the same file.
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
    private static readonly MessageDescriptor[] Cases =
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

    private static string VectorPath => Path.Combine(RepositoryRoot(), "protocol", "conformance", "schema-hash.json");

    private static SchemaHashVectors Load()
    {
        var json = File.ReadAllText(VectorPath);
        return JsonSerializer.Deserialize<SchemaHashVectors>(json, SchemaHashVectors.Json)
            ?? throw new InvalidOperationException("Vector file is empty.");
    }

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

        Directory.CreateDirectory(Path.GetDirectoryName(VectorPath)!);
        File.WriteAllText(VectorPath, JsonSerializer.Serialize(vectors, SchemaHashVectors.Json) + "\n");
    }

    [Fact]
    public void VectorFile_DeclaresTheAlgorithmItPins()
    {
        var vectors = Load();

        Assert.Equal(SchemaHashVectors.CurrentVersion, vectors.Version);
        Assert.Equal(SchemaHashVectors.CurrentAlgorithm, vectors.Algorithm);
        Assert.NotEmpty(vectors.Cases);
    }

    [Fact]
    public void EveryCoveredTypeIsPinned()
    {
        var pinned = Load().Cases.Select(c => c.LogicalName).ToHashSet(StringComparer.Ordinal);

        foreach (var descriptor in Cases)
            Assert.Contains(descriptor.FullName, pinned);
    }

    [Fact]
    public void CanonicalFormMatchesTheVectors()
    {
        foreach (var vector in Load().Cases)
        {
            var descriptor = Cases.Single(d => d.FullName == vector.LogicalName);

            // Compared before the hash: a rendering diff says which line is wrong.
            Assert.Equal(vector.Canonical, SchemaHash.Canonicalize(descriptor));
        }
    }

    [Fact]
    public void HashMatchesTheVectors()
    {
        foreach (var vector in Load().Cases)
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

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "protocol", "SPEC.md")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root from the test binary.");
    }
}
