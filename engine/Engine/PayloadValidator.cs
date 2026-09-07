using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace Engine.Coordinator;

/// <summary>
/// Checks that a component payload is well-formed protobuf shaped like the schema it
/// claims to be, without compiling the type.
/// </summary>
/// <remarks>
/// Coordinator-side only. The world is the one party that has to distrust a payload;
/// a client validating what it just encoded would only be testing its own protobuf
/// runtime. That is why this lives here rather than in <c>Ecs.Protocol</c> — the shared
/// library holds what both sides must agree on, not everything the protocol mentions.
///
/// The check is structural, not semantic: the bytes parse, every field number the
/// schema knows about carries a wire type the schema permits, and nested messages are
/// themselves valid. Unknown field numbers are allowed — proto3 keeps them, and
/// rejecting them would make this stricter than protobuf itself. Combined with exact
/// schema-hash matching, that is enough to stop a misrouted or corrupted payload from
/// entering the world.
/// </remarks>
public static class PayloadValidator
{
    private const int MaxDepth = 64;

    private const int WireVarint = 0;
    private const int WireFixed64 = 1;
    private const int WireLengthDelimited = 2;
    private const int WireStartGroup = 3;
    private const int WireFixed32 = 5;

    public static bool IsValid(MessageDescriptor descriptor, ReadOnlySpan<byte> payload, out string error)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        try
        {
            // CodedInputStream needs a contiguous buffer it can hold onto.
            Validate(descriptor, payload.ToArray(), depth: 0);
            error = "";
            return true;
        }
        catch (InvalidPayloadException ex)
        {
            error = ex.Message;
            return false;
        }
        catch (InvalidProtocolBufferException ex)
        {
            error = $"{descriptor.FullName}: {ex.Message}";
            return false;
        }
    }

    private static void Validate(MessageDescriptor descriptor, byte[] payload, int depth)
    {
        if (depth > MaxDepth)
            throw new InvalidPayloadException($"{descriptor.FullName}: nested deeper than {MaxDepth} levels.");

        var input = new CodedInputStream(payload);
        while (true)
        {
            var tag = input.ReadTag();
            if (tag == 0) break;

            var fieldNumber = (int)(tag >> 3);
            var wireType = (int)(tag & 7);
            var field = descriptor.FindFieldByNumber(fieldNumber);

            if (field is null)
            {
                // Unknown to this schema. Skip it the way any protobuf runtime would.
                input.SkipLastField();
                continue;
            }

            if (!Accepts(field, wireType))
            {
                throw new InvalidPayloadException(
                    $"{descriptor.FullName}.{field.Name}: wire type {wireType} is not valid for " +
                    $"{field.FieldType.ToString().ToLowerInvariant()}.");
            }

            if (field.FieldType is FieldType.Message or FieldType.Group &&
                wireType == WireLengthDelimited)
            {
                Validate(field.MessageType, input.ReadBytes().ToByteArray(), depth + 1);
                continue;
            }

            input.SkipLastField();
        }
    }

    private static bool Accepts(FieldDescriptor field, int wireType)
    {
        var expected = ExpectedWireType(field.FieldType);
        if (wireType == expected) return true;

        // Repeated scalars may arrive packed, which is always length-delimited.
        return wireType == WireLengthDelimited
            && field.IsRepeated
            && expected != WireLengthDelimited;
    }

    private static int ExpectedWireType(FieldType type) => type switch
    {
        FieldType.Double or FieldType.Fixed64 or FieldType.SFixed64 => WireFixed64,
        FieldType.Float or FieldType.Fixed32 or FieldType.SFixed32 => WireFixed32,
        FieldType.String or FieldType.Bytes or FieldType.Message => WireLengthDelimited,
        FieldType.Group => WireStartGroup,
        _ => WireVarint,
    };

    private sealed class InvalidPayloadException(string message) : Exception(message);
}
