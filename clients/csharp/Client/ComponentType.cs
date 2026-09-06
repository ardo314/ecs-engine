using Ecs.Protocol;
using Ecs.Protocol.V1;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace Engine.Core;

/// <summary>
/// Everything the SDK knows about a component type, resolved once per type.
/// </summary>
/// <remarks>
/// This is where a generated protobuf message becomes an ECS component. The SDK derives
/// the type's identity from the descriptors the protobuf compiler already produced, so
/// there is nothing to hand-maintain and nothing to get out of step: the name, the
/// schema hash and the descriptor set are all functions of the <c>.proto</c> file.
///
/// C# cannot add static members to a generated class, so the plan's
/// <c>Position.SCHEMA_HASH</c> is spelled <c>ComponentType&lt;Position&gt;.SchemaHash</c>.
/// Authoring code never touches it — it declares <c>Query.Read&lt;Position&gt;()</c> and
/// the SDK turns that into a protocol-level request.
/// </remarks>
public static class ComponentType<T> where T : IMessage<T>, new()
{
    public static MessageDescriptor Descriptor { get; } = new T().Descriptor;

    /// <summary>The protobuf full name, e.g. <c>movement.v1.Position</c>.</summary>
    public static string Name { get; } = Descriptor.FullName;

    /// <summary>Exact identity of the type's structure.</summary>
    public static ulong SchemaHash { get; } = Ecs.Protocol.SchemaHash.Of(Descriptor);

    public static ByteString FileDescriptorSet { get; } = Descriptors.FileDescriptorSetFor(Descriptor);

    public static ComponentTypeRef Ref { get; } = new() { LogicalName = Name, SchemaHash = SchemaHash };

    /// <summary>
    /// The declaration sent to the coordinator: identity, descriptors, and whatever the
    /// type says about itself through the <c>ecs.v1.description</c> option.
    /// </summary>
    public static ComponentTypeDeclaration Declaration { get; } = BuildDeclaration();

    public static ComponentValue Value(T component) => new()
    {
        Type = Ref,
        Payload = component.ToByteString(),
    };

    private static ComponentTypeDeclaration BuildDeclaration()
    {
        var declaration = new ComponentTypeDeclaration
        {
            Type = Ref,
            FileDescriptorSet = FileDescriptorSet,
        };
        declaration.Description.AddRange(ComponentDescription.Of(Descriptor));
        return declaration;
    }
}

/// <summary>
/// Reads the <c>ecs.v1.description</c> message option — the open set of contracts a
/// component type attaches to itself.
/// </summary>
internal static class ComponentDescription
{
    /// <summary>
    /// Each attachment as a <see cref="ComponentValue"/>. The attachment's own message
    /// type is resolved out of the declaring file's descriptor pool, so its schema
    /// identity is derived the same way as any other component's — no special case, and
    /// no need for the attachment type to be linked into this process.
    /// </summary>
    public static IEnumerable<ComponentValue> Of(MessageDescriptor descriptor)
    {
        var options = descriptor.GetOptions();
        if (options is null) yield break;

        foreach (var attachment in options.GetExtension(Ecs.V1.ComponentExtensions.Description))
        {
            var typeUrl = attachment.TypeUrl;
            var name = typeUrl[(typeUrl.LastIndexOf('/') + 1)..];

            if (Lookup(descriptor.File, name, new HashSet<string>(StringComparer.Ordinal)) is not { } target)
            {
                throw new InvalidOperationException(
                    $"'{descriptor.FullName}' describes itself with '{name}', but that type is not " +
                    $"reachable from '{descriptor.File.Name}'. Import the file that defines it.");
            }

            yield return new ComponentValue
            {
                Type = new ComponentTypeRef
                {
                    LogicalName = target.FullName,
                    SchemaHash = SchemaHash.Of(target),
                },
                Payload = attachment.Value,
            };
        }
    }

    private static MessageDescriptor? Lookup(FileDescriptor file, string fullName, HashSet<string> seen)
    {
        if (!seen.Add(file.Name)) return null;

        if (Search(file.MessageTypes, fullName) is { } found) return found;

        foreach (var dependency in file.Dependencies)
        {
            if (Lookup(dependency, fullName, seen) is { } nested) return nested;
        }
        return null;
    }

    private static MessageDescriptor? Search(IEnumerable<MessageDescriptor> messages, string fullName)
    {
        foreach (var message in messages)
        {
            if (message.FullName == fullName) return message;
            if (Search(message.NestedTypes, fullName) is { } nested) return nested;
        }
        return null;
    }
}
