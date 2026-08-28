using MessagePack;
using MessagePack.Resolvers;

namespace Engine.Core;

/// <summary>
/// Configures MessagePack to use contractless resolution (no attributes required on types).
/// Call <see cref="Initialize"/> once at startup in every process.
/// </summary>
/// <remarks>
/// No entity formatter is registered here: the coordinator only ever deserializes
/// <see cref="ComponentInfo"/> and message envelopes, none of which hold an entity
/// reference. Component payloads pass through as opaque bytes.
/// </remarks>
public static class Serialization
{
    private static bool _initialized;

    public static MessagePackSerializerOptions Options { get; } =
        MessagePackSerializerOptions.Standard
            .WithResolver(ContractlessStandardResolver.Instance);

    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        MessagePackSerializer.DefaultOptions = Options;
    }
}
