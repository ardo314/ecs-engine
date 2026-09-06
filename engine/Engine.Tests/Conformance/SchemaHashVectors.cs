using System.Text.Json;
using System.Text.Json.Serialization;

namespace Engine.Tests.Conformance;

/// <summary>
/// The pinned expectation for one component type.
/// </summary>
/// <remarks>
/// <see cref="Canonical"/> is carried alongside the hash on purpose: when another
/// implementation disagrees, a diff of the rendering says which line is wrong, whereas
/// two 64-bit numbers say nothing at all.
/// </remarks>
public sealed record SchemaHashCase
{
    [JsonPropertyName("logicalName")]
    public required string LogicalName { get; init; }

    /// <summary>Hex, zero-padded to 16 digits. A string, because JSON numbers cannot hold uint64.</summary>
    [JsonPropertyName("schemaHash")]
    public required string SchemaHash { get; init; }

    [JsonPropertyName("canonical")]
    public required string Canonical { get; init; }
}

public sealed record SchemaHashVectors
{
    [JsonPropertyName("version")]
    public required int Version { get; init; }

    [JsonPropertyName("algorithm")]
    public required string Algorithm { get; init; }

    [JsonPropertyName("spec")]
    public required string Spec { get; init; }

    [JsonPropertyName("cases")]
    public required IReadOnlyList<SchemaHashCase> Cases { get; init; }

    public const int CurrentVersion = 1;
    public const string CurrentAlgorithm = "sha256-truncated-64-be";
    public const string SpecReference = "protocol/SPEC.md#1-schema-hash";

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Format(ulong hash) => $"0x{hash:x16}";

    public static SchemaHashVectors Load() =>
        JsonSerializer.Deserialize<SchemaHashVectors>(File.ReadAllText(Path), Json)
            ?? throw new InvalidOperationException("Vector file is empty.");

    public static string Path =>
        System.IO.Path.Combine(RepositoryRoot(), "protocol", "conformance", "schema-hash.json");

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(System.IO.Path.Combine(directory.FullName, "protocol", "SPEC.md")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root from the test binary.");
    }
}
