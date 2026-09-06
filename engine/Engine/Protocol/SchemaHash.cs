using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace Engine.Coordinator;

/// <summary>
/// The exact identity of a component type's structure, per <c>protocol/SPEC.md</c> §1.
/// </summary>
/// <remarks>
/// The coordinator's own implementation. The C# SDK has a separate one, and they are
/// kept honest by <c>protocol/conformance/schema-hash.json</c> rather than by sharing
/// code — the same mechanism that keeps the TypeScript implementation honest. Nothing
/// here may be changed without regenerating those vectors.
///
/// Computed over <c>FileDescriptorProto</c> rather than over this runtime's reflection
/// API. That is not an implementation preference — runtimes genuinely disagree about
/// how to model a schema. C# reports a map field as repeated and exposes its synthetic
/// entry type; protobuf-es hides the entry entirely; Python surfaces it with the
/// <c>map_entry</c> option set. Hashing what C# sees would mean no other language could
/// register a component type that contains a map.
/// </remarks>
internal static class SchemaHash
{
    /// <summary>The 64-bit schema hash of <paramref name="logicalName"/> within the set.</summary>
    public static ulong Of(ByteString fileDescriptorSet, string logicalName) =>
        Digest(Canonicalize(fileDescriptorSet, logicalName));

    /// <summary>
    /// Convenience for a type this process compiled against. Builds the transitively
    /// closed set and hashes that, so it takes exactly the path a foreign consumer would.
    /// </summary>
    public static ulong Of(MessageDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return Of(Descriptors.FileDescriptorSetFor(descriptor), descriptor.FullName);
    }

    public static string Canonicalize(MessageDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return Canonicalize(Descriptors.FileDescriptorSetFor(descriptor), descriptor.FullName);
    }

    /// <summary>
    /// The canonical rendering the digest is taken over. Public because a mismatch
    /// between two implementations should be a diff, not a pair of 64-bit numbers.
    /// </summary>
    public static string Canonicalize(ByteString fileDescriptorSet, string logicalName)
    {
        ArgumentNullException.ThrowIfNull(fileDescriptorSet);
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalName);

        var index = DescriptorIndex.Build(fileDescriptorSet);
        return Canonicalize(index, logicalName);
    }

    internal static string Canonicalize(DescriptorIndex index, string logicalName)
    {
        var root = index.Message(logicalName)
            ?? throw new InvalidDescriptorSetException(
                $"FileDescriptorSet does not contain a message named '{logicalName}'.");

        index.RequireProto3(logicalName);

        var messages = new SortedSet<string>(StringComparer.Ordinal);
        var enums = new SortedSet<string>(StringComparer.Ordinal);
        Collect(index, logicalName, messages, enums);

        // The root is rendered first, which anchors the digest to it: two types with
        // identical reference closures still hash differently.
        messages.Remove(logicalName);

        var sb = new StringBuilder();
        RenderMessage(root, logicalName, sb);
        foreach (var name in messages) RenderMessage(index.Message(name)!, name, sb);
        foreach (var name in enums) RenderEnum(index.Enum(name)!, name, sb);
        return sb.ToString();
    }

    private static ulong Digest(string canonical) =>
        BinaryPrimitives.ReadUInt64BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));

    /// <summary>
    /// The transitive reference closure. A set rather than a tree, so mutual recursion
    /// terminates and a type reached by several paths is rendered once.
    /// </summary>
    private static void Collect(
        DescriptorIndex index, string root, SortedSet<string> messages, SortedSet<string> enums)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var name = pending.Pop();
            if (!messages.Add(name)) continue;

            index.RequireProto3(name);

            var message = index.Message(name)
                ?? throw new InvalidDescriptorSetException(
                    $"'{name}' is referenced but not present in the FileDescriptorSet.");

            foreach (var field in message.Field)
            {
                if (!HasTypeName(field.Type)) continue;

                var target = Strip(field.TypeName);
                if (field.Type == FieldDescriptorProto.Types.Type.Enum)
                {
                    if (index.Enum(target) is null)
                    {
                        throw new InvalidDescriptorSetException(
                            $"'{name}.{field.Name}' references enum '{target}', which is not in the set.");
                    }

                    index.RequireProto3(target);
                    enums.Add(target);
                }
                else
                {
                    pending.Push(target);
                }
            }
        }
    }

    private static void RenderMessage(DescriptorProto message, string fullName, StringBuilder sb)
    {
        sb.Append("message ").Append(fullName).Append('\n');

        foreach (var field in message.Field.OrderBy(f => f.Number))
        {
            sb.Append("field ")
              .Append(field.Number.ToString(CultureInfo.InvariantCulture))
              .Append(' ').Append(field.Name)
              .Append(' ').Append(Label(field))
              .Append(' ').Append(TypeName(field.Type));

            if (HasTypeName(field.Type))
                sb.Append(' ').Append(Strip(field.TypeName));

            sb.Append('\n');
        }

        // Oneof membership changes presence semantics, so it is part of the identity.
        // Synthetic oneofs are not: they exist only to carry proto3 `optional`, which
        // the field's own label already records.
        var synthetic = SyntheticOneofs(message);
        for (var index = 0; index < message.OneofDecl.Count; index++)
        {
            if (synthetic.Contains(index)) continue;

            sb.Append("oneof ")
              .Append(index.ToString(CultureInfo.InvariantCulture))
              .Append(' ').Append(message.OneofDecl[index].Name).Append('\n');
        }
    }

    private static void RenderEnum(EnumDescriptorProto @enum, string fullName, StringBuilder sb)
    {
        sb.Append("enum ").Append(fullName).Append('\n');

        // Aliases share a number, so the name breaks the tie.
        foreach (var value in @enum.Value.OrderBy(v => v.Number).ThenBy(v => v.Name, StringComparer.Ordinal))
        {
            sb.Append("value ")
              .Append(value.Number.ToString(CultureInfo.InvariantCulture))
              .Append(' ').Append(value.Name).Append('\n');
        }
    }

    /// <summary>
    /// Which oneofs exist only to carry proto3 <c>optional</c>.
    /// </summary>
    /// <remarks>
    /// Derived from the fields rather than from the oneofs, because the obvious
    /// definition — "a oneof declared by exactly one proto3-optional field" — needs to
    /// know whether a field declares <c>oneof_index</c> at all, and not every runtime
    /// can say. descriptor.proto is proto2, and protobuf-es reports <c>oneof_index</c>
    /// as 0 for a field in no oneof. Reading it only for proto3-optional fields avoids
    /// the question entirely: those always have it set.
    /// </remarks>
    private static HashSet<int> SyntheticOneofs(DescriptorProto message)
    {
        var synthetic = new HashSet<int>();
        foreach (var field in message.Field)
        {
            if (field.Proto3Optional) synthetic.Add(field.OneofIndex);
        }
        return synthetic;
    }

    private static string Label(FieldDescriptorProto field) => field.Label switch
    {
        FieldDescriptorProto.Types.Label.Repeated => "repeated",
        _ when field.Proto3Optional => "optional",
        _ => "singular",
    };

    private static bool HasTypeName(FieldDescriptorProto.Types.Type type) =>
        type is FieldDescriptorProto.Types.Type.Message
             or FieldDescriptorProto.Types.Type.Group
             or FieldDescriptorProto.Types.Type.Enum;

    /// <summary>`type_name` is fully qualified with a leading dot; the rendering is not.</summary>
    private static string Strip(string typeName) =>
        typeName.StartsWith('.') ? typeName[1..] : typeName;

    /// <summary>
    /// Canonical protobuf type names. Spelled out rather than derived from the C# enum,
    /// because the rendering is a cross-language contract and no language's enum
    /// formatter is authoritative.
    /// </summary>
    private static string TypeName(FieldDescriptorProto.Types.Type type) => type switch
    {
        FieldDescriptorProto.Types.Type.Double => "TYPE_DOUBLE",
        FieldDescriptorProto.Types.Type.Float => "TYPE_FLOAT",
        FieldDescriptorProto.Types.Type.Int64 => "TYPE_INT64",
        FieldDescriptorProto.Types.Type.Uint64 => "TYPE_UINT64",
        FieldDescriptorProto.Types.Type.Int32 => "TYPE_INT32",
        FieldDescriptorProto.Types.Type.Fixed64 => "TYPE_FIXED64",
        FieldDescriptorProto.Types.Type.Fixed32 => "TYPE_FIXED32",
        FieldDescriptorProto.Types.Type.Bool => "TYPE_BOOL",
        FieldDescriptorProto.Types.Type.String => "TYPE_STRING",
        FieldDescriptorProto.Types.Type.Group => "TYPE_GROUP",
        FieldDescriptorProto.Types.Type.Message => "TYPE_MESSAGE",
        FieldDescriptorProto.Types.Type.Bytes => "TYPE_BYTES",
        FieldDescriptorProto.Types.Type.Uint32 => "TYPE_UINT32",
        FieldDescriptorProto.Types.Type.Enum => "TYPE_ENUM",
        FieldDescriptorProto.Types.Type.Sfixed32 => "TYPE_SFIXED32",
        FieldDescriptorProto.Types.Type.Sfixed64 => "TYPE_SFIXED64",
        FieldDescriptorProto.Types.Type.Sint32 => "TYPE_SINT32",
        FieldDescriptorProto.Types.Type.Sint64 => "TYPE_SINT64",
        _ => throw new InvalidDescriptorSetException($"Unknown protobuf field type '{type}'."),
    };
}
