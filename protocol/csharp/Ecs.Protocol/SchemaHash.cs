using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf.Reflection;

namespace Ecs.Protocol;

/// <summary>
/// The exact identity of a component type's structure.
/// </summary>
/// <remarks>
/// The hash is taken over a canonical textual rendering rather than over serialised
/// descriptors, because protobuf serialisation is not canonical — field ordering and
/// unknown-field retention both vary by runtime. The rendering below is fully
/// determined by the schema, so any language that can read a
/// <c>FileDescriptorSet</c> can reproduce the same value.
///
/// Two types hash equal exactly when they have the same full name, the same fields
/// (number, name, label, type and referenced type name), the same oneof grouping,
/// and transitively the same referenced messages and enums. Comments, options,
/// source file names and declaration order are all deliberately excluded: they
/// change without changing what a payload means.
/// </remarks>
public static class SchemaHash
{
    private const int MaxDepth = 64;

    /// <summary>The 64-bit schema hash of <paramref name="descriptor"/>.</summary>
    public static ulong Of(MessageDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(Canonicalize(descriptor)));
        return BinaryPrimitivesBigEndian(digest);
    }

    /// <summary>
    /// The canonical rendering the hash is taken over. Public so a mismatch can be
    /// diffed instead of guessed at.
    /// </summary>
    public static string Canonicalize(MessageDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var messages = new SortedDictionary<string, MessageDescriptor>(StringComparer.Ordinal);
        var enums = new SortedDictionary<string, EnumDescriptor>(StringComparer.Ordinal);
        Collect(descriptor, messages, enums, depth: 0);

        // The root is rendered first so the digest is anchored to it: two types with
        // an identical reference closure still hash differently.
        messages.Remove(descriptor.FullName);

        var sb = new StringBuilder();
        RenderMessage(descriptor, sb);
        foreach (var message in messages.Values) RenderMessage(message, sb);
        foreach (var @enum in enums.Values) RenderEnum(@enum, sb);
        return sb.ToString();
    }

    private static void Collect(
        MessageDescriptor message,
        SortedDictionary<string, MessageDescriptor> messages,
        SortedDictionary<string, EnumDescriptor> enums,
        int depth)
    {
        if (depth > MaxDepth)
            throw new InvalidOperationException(
                $"Schema for '{message.FullName}' nests deeper than {MaxDepth} levels.");

        if (!messages.TryAdd(message.FullName, message)) return;

        foreach (var field in message.Fields.InDeclarationOrder())
        {
            switch (field.FieldType)
            {
                case FieldType.Message or FieldType.Group:
                    Collect(field.MessageType, messages, enums, depth + 1);
                    break;
                case FieldType.Enum:
                    enums.TryAdd(field.EnumType.FullName, field.EnumType);
                    break;
            }
        }
    }

    private static void RenderMessage(MessageDescriptor message, StringBuilder sb)
    {
        sb.Append("message ").Append(message.FullName).Append('\n');

        foreach (var field in message.Fields.InFieldNumberOrder())
        {
            sb.Append("field ")
              .Append(field.FieldNumber.ToString(CultureInfo.InvariantCulture))
              .Append(' ').Append(field.Name)
              .Append(' ').Append(field.IsRepeated ? "repeated" : "optional")
              .Append(' ').Append(TypeName(field.FieldType));

            switch (field.FieldType)
            {
                case FieldType.Message or FieldType.Group:
                    sb.Append(' ').Append(field.MessageType.FullName);
                    break;
                case FieldType.Enum:
                    sb.Append(' ').Append(field.EnumType.FullName);
                    break;
            }

            sb.Append('\n');
        }

        // Oneof membership changes presence semantics, so it is part of the identity.
        // Synthetic oneofs (proto3 `optional`) are skipped: they are a compiler detail.
        foreach (var oneof in message.Oneofs.Where(o => !o.IsSynthetic).OrderBy(o => o.Index))
        {
            sb.Append("oneof ")
              .Append(oneof.Index.ToString(CultureInfo.InvariantCulture))
              .Append(' ').Append(oneof.Name).Append('\n');
        }
    }

    private static void RenderEnum(EnumDescriptor @enum, StringBuilder sb)
    {
        sb.Append("enum ").Append(@enum.FullName).Append('\n');

        foreach (var value in @enum.Values.OrderBy(v => v.Number).ThenBy(v => v.Name, StringComparer.Ordinal))
        {
            sb.Append("value ")
              .Append(value.Number.ToString(CultureInfo.InvariantCulture))
              .Append(' ').Append(value.Name).Append('\n');
        }
    }

    private static string TypeName(FieldType type) => type switch
    {
        FieldType.Double => "double",
        FieldType.Float => "float",
        FieldType.Int64 => "int64",
        FieldType.UInt64 => "uint64",
        FieldType.Int32 => "int32",
        FieldType.Fixed64 => "fixed64",
        FieldType.Fixed32 => "fixed32",
        FieldType.Bool => "bool",
        FieldType.String => "string",
        FieldType.Group => "group",
        FieldType.Message => "message",
        FieldType.Bytes => "bytes",
        FieldType.UInt32 => "uint32",
        FieldType.Enum => "enum",
        FieldType.SFixed32 => "sfixed32",
        FieldType.SFixed64 => "sfixed64",
        FieldType.SInt32 => "sint32",
        FieldType.SInt64 => "sint64",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown protobuf field type."),
    };

    private static ulong BinaryPrimitivesBigEndian(ReadOnlySpan<byte> digest) =>
        System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(digest);
}
