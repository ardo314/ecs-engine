using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace NovaInstaller;

/// <summary>A system image to install, plus the app name it is deployed under.</summary>
public sealed record SystemImage(string Name, string Image);

/// <summary>Installer configuration, read entirely from environment variables.</summary>
public sealed class InstallerOptions
{
    public const string DefaultRegistry = "ghcr.io/ardo314/ecs-engine";

    /// <summary>
    /// Baked in at build time (<c>-p:ImageTag=…</c>) so the installer deploys the images
    /// built from its own revision instead of whatever <c>latest</c> currently points at.
    /// </summary>
    public static readonly string DefaultImageTag =
        typeof(InstallerOptions).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "ImageTag")?.Value is { Length: > 0 } bakedTag
            ? bakedTag
            : "latest";

    public required string NovaBaseUrl { get; init; }
    public required string AccessToken { get; init; }
    public required string Cell { get; init; }
    public required string AppPrefix { get; init; }
    public required string EngineImage { get; init; }
    public required string EditorBackendImage { get; init; }
    public required string EditorFrontendImage { get; init; }
    public required IReadOnlyList<SystemImage> SystemImages { get; init; }
    public required bool InstallEditor { get; init; }

    /// <summary>
    /// Overrides the broker address. Left null the containers fall back to
    /// NATS_BROKER, which NOVA injects.
    /// </summary>
    public string? NatsUrl { get; init; }

    public required int TickRate { get; init; }
    public string? RegistryUser { get; init; }
    public string? RegistryPassword { get; init; }
    public required bool DryRun { get; init; }

    public static InstallerOptions FromEnvironment(Func<string, string?> read)
    {
        var registry = Value(read, "ECS_IMAGE_REGISTRY") ?? DefaultRegistry;
        var tag = Value(read, "ECS_IMAGE_TAG") ?? DefaultImageTag;
        var prefix = AppName.Sanitize(Value(read, "ECS_APP_PREFIX") ?? "ecs");
        if (prefix.Length == 0)
            throw new InstallerConfigurationException("ECS_APP_PREFIX must contain at least one alphanumeric character.");

        return new InstallerOptions
        {
            NovaBaseUrl = NormalizeBaseUrl(Value(read, "NOVA_BASE_URL") ?? Value(read, "NOVA_API") ?? "http://localhost:80"),
            AccessToken = Value(read, "NOVA_ACCESS_TOKEN") ?? "",
            Cell = Value(read, "NOVA_CELL") ?? Value(read, "CELL_NAME") ?? "cell",
            AppPrefix = prefix,
            EngineImage = Value(read, "ECS_ENGINE_IMAGE") ?? $"{registry}/engine:{tag}",
            EditorBackendImage = Value(read, "ECS_EDITOR_BACKEND_IMAGE") ?? $"{registry}/editor-backend:{tag}",
            EditorFrontendImage = Value(read, "ECS_EDITOR_FRONTEND_IMAGE") ?? $"{registry}/editor-frontend:{tag}",
            SystemImages = ParseSystemImages(Value(read, "ECS_SYSTEM_IMAGES")),
            InstallEditor = ParseBool(Value(read, "ECS_INSTALL_EDITOR"), true),
            NatsUrl = Value(read, "ECS_NATS_URL"),
            TickRate = ParseInt(read, "ECS_TICK_RATE", 20),
            RegistryUser = Value(read, "ECS_REGISTRY_USER"),
            RegistryPassword = Value(read, "ECS_REGISTRY_PASSWORD"),
            DryRun = ParseBool(Value(read, "ECS_DRY_RUN"), false)
        };
    }

    /// <summary>
    /// Parses a comma-separated list where each entry is <c>name=image</c> or a bare
    /// image reference whose repository segment becomes the app name.
    /// </summary>
    internal static IReadOnlyList<SystemImage> ParseSystemImages(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];

        var result = new List<SystemImage>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = entry.IndexOf('=');
            var image = separator < 0 ? entry : entry[(separator + 1)..].Trim();
            var rawName = separator < 0 ? DeriveName(image) : entry[..separator].Trim();

            if (image.Length == 0)
                throw new InstallerConfigurationException($"ECS_SYSTEM_IMAGES entry '{entry}' has no image reference.");

            var name = AppName.Sanitize(rawName);
            if (name.Length == 0)
                throw new InstallerConfigurationException($"ECS_SYSTEM_IMAGES entry '{entry}' yields an empty app name.");

            if (!seen.Add(name))
                throw new InstallerConfigurationException($"ECS_SYSTEM_IMAGES contains duplicate system name '{name}'.");

            result.Add(new SystemImage(name, image));
        }

        return result;
    }

    /// <summary>
    /// Reduces a NOVA address to the instance root. NOVA injects <c>NOVA_API</c> pointing at
    /// the API root (<c>…/api/v1</c>) and sometimes without a scheme, while
    /// <see cref="NovaAppClient"/> appends its own versioned path.
    /// </summary>
    internal static string NormalizeBaseUrl(string raw)
    {
        var value = raw.Trim();
        if (!value.Contains("://", StringComparison.Ordinal))
            value = "http://" + value;

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InstallerConfigurationException($"'{raw}' is not a valid NOVA base URL.");

        var path = Regex.Replace(uri.AbsolutePath.TrimEnd('/'), @"/api(/v\d+)?$", "", RegexOptions.IgnoreCase);

        return new UriBuilder(uri) { Path = path, Query = "", Fragment = "" }.Uri.ToString().TrimEnd('/');
    }

    /// <summary>Takes the repository segment of an image reference, minus tag or digest.</summary>
    internal static string DeriveName(string image)
    {
        var withoutDigest = image.Split('@')[0];
        var lastSegment = withoutDigest[(withoutDigest.LastIndexOf('/') + 1)..];
        var colon = lastSegment.LastIndexOf(':');
        return colon < 0 ? lastSegment : lastSegment[..colon];
    }

    private static string? Value(Func<string, string?> read, string key)
    {
        var value = read(key);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static bool ParseBool(string? value, bool fallback) => value switch
    {
        null => fallback,
        _ when value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1" => true,
        _ when value.Equals("false", StringComparison.OrdinalIgnoreCase) || value == "0" => false,
        _ => throw new InstallerConfigurationException($"Expected a boolean but got '{value}'.")
    };

    private static int ParseInt(Func<string, string?> read, string key, int fallback)
    {
        var value = Value(read, key);
        if (value is null) return fallback;
        if (!int.TryParse(value, out var parsed) || parsed <= 0)
            throw new InstallerConfigurationException($"{key} must be a positive integer but was '{value}'.");
        return parsed;
    }
}

/// <summary>Reduces a string to an RFC 1035 label, as required for NOVA app names.</summary>
public static class AppName
{
    private const int MaxLength = 63;

    public static string Sanitize(string value)
    {
        var builder = new StringBuilder(value.Length);

        foreach (var c in value.ToLowerInvariant())
        {
            if (char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c))
                builder.Append(c);
            else if (builder.Length > 0 && builder[^1] != '-')
                builder.Append('-');
        }

        var result = builder.ToString().Trim('-');

        // A label must start with a letter.
        if (result.Length > 0 && !char.IsAsciiLetterLower(result[0]))
            result = "a" + result;

        return result.Length > MaxLength ? result[..MaxLength].TrimEnd('-') : result;
    }
}

public sealed class InstallerConfigurationException(string message) : Exception(message);
