using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace Engine.Core;

/// <summary>
/// Turning a component type into the descriptors that travel with it,
/// per <c>protocol/SPEC.md</c> §2.
/// </summary>
/// <remarks>
/// The SDK only ever builds descriptor sets; it never rebuilds a pool from one, because
/// a C# system is compiled against every type it names. The coordinator owns the other
/// direction, and so would a client that decodes types it was never built against.
/// </remarks>
internal static class Descriptors
{
    /// <summary>
    /// The transitively closed <c>FileDescriptorSet</c> for the descriptor's own file,
    /// dependency-first so the receiver can rebuild it in a single pass.
    /// </summary>
    public static ByteString FileDescriptorSetFor(MessageDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var set = new FileDescriptorSet();
        Collect(descriptor.File, set, new HashSet<string>(StringComparer.Ordinal));
        return set.ToByteString();
    }

    private static void Collect(FileDescriptor file, FileDescriptorSet set, HashSet<string> seen)
    {
        if (!seen.Add(file.Name)) return;
        foreach (var dependency in file.Dependencies)
            Collect(dependency, set, seen);
        set.File.Add(file.ToProto());
    }
}

internal sealed class InvalidDescriptorSetException : Exception
{
    public InvalidDescriptorSetException(string message) : base(message) { }
    public InvalidDescriptorSetException(string message, Exception inner) : base(message, inner) { }
}
