using System.Text.Json;
using System.Text.Json.Serialization;

namespace NovaInstaller;

/// <summary>
/// Mirrors the body accepted by <c>POST /api/v2/cells/{cell}/apps</c>.
/// Property names map to the API's snake_case via <see cref="NovaJson.Options"/>.
/// </summary>
public sealed class AppManifest
{
    public required string Name { get; init; }
    public string? AppIcon { get; init; }
    public required ContainerImage ContainerImage { get; init; }
    public int? Port { get; init; }
    public string? HealthPath { get; init; }
    public List<EnvVar>? Environment { get; init; }
    public AppStorage? Storage { get; init; }
    public AppResources? Resources { get; init; }
}

public sealed class ContainerImage
{
    public required string Image { get; init; }
    public RegistryCredentials? Credentials { get; init; }
}

public sealed class RegistryCredentials
{
    public required string Registry { get; init; }
    public required string User { get; init; }
    public required string Password { get; init; }
}

public sealed class EnvVar
{
    public required string Name { get; init; }
    public required string Value { get; init; }
}

public sealed class AppStorage
{
    public required string MountPath { get; init; }
    public required string Capacity { get; init; }
}

public sealed class AppResources
{
    public string? MemoryLimit { get; init; }
    public int? IntelGpu { get; init; }
}

public static class NovaJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static readonly JsonSerializerOptions Pretty = new(Options) { WriteIndented = true };
}
