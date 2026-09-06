using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace Ecs.Protocol;

/// <summary>
/// A flat, fully-qualified view over a <c>FileDescriptorSet</c>.
/// </summary>
/// <remarks>
/// Name resolution is done here rather than through a runtime descriptor pool, because
/// pools are not neutral: they hide synthetic map entry types, normalise names, and
/// refuse files they already hold. The schema hash is a cross-language contract, so it
/// resolves against the bytes it was given and nothing else.
/// </remarks>
internal sealed class DescriptorIndex
{
    private readonly Dictionary<string, DescriptorProto> _messages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EnumDescriptorProto> _enums = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _syntax = new(StringComparer.Ordinal);

    public static DescriptorIndex Build(ByteString fileDescriptorSet)
    {
        FileDescriptorSet set;
        try
        {
            set = FileDescriptorSet.Parser.ParseFrom(fileDescriptorSet);
        }
        catch (InvalidProtocolBufferException ex)
        {
            throw new InvalidDescriptorSetException("FileDescriptorSet is not valid protobuf.", ex);
        }

        var index = new DescriptorIndex();
        foreach (var file in set.File)
        {
            // An absent `syntax` means proto2, which is what descriptor.proto itself is.
            var syntax = string.IsNullOrEmpty(file.Syntax) ? "proto2" : file.Syntax;

            foreach (var message in file.MessageType)
                index.AddMessage(file.Package, message, syntax);

            foreach (var @enum in file.EnumType)
                index.AddEnum(file.Package, @enum, syntax);
        }
        return index;
    }

    public DescriptorProto? Message(string fullName) =>
        _messages.TryGetValue(fullName, out var message) ? message : null;

    public EnumDescriptorProto? Enum(string fullName) =>
        _enums.TryGetValue(fullName, out var @enum) ? @enum : null;

    /// <summary>
    /// Only proto3 is specified. Rejecting anything else is deliberate: proto2 presence
    /// rules and editions would each need their own canonical form, and nothing needs
    /// them yet.
    /// </summary>
    public void RequireProto3(string fullName)
    {
        if (!_syntax.TryGetValue(fullName, out var syntax) || syntax == "proto3") return;

        throw new InvalidDescriptorSetException(
            $"'{fullName}' is declared in a {syntax} file. Only proto3 is supported.");
    }

    private void AddMessage(string scope, DescriptorProto message, string syntax)
    {
        var fullName = Qualify(scope, message.Name);
        _messages[fullName] = message;
        _syntax[fullName] = syntax;

        foreach (var nested in message.NestedType)
            AddMessage(fullName, nested, syntax);

        foreach (var @enum in message.EnumType)
            AddEnum(fullName, @enum, syntax);
    }

    private void AddEnum(string scope, EnumDescriptorProto @enum, string syntax)
    {
        var fullName = Qualify(scope, @enum.Name);
        _enums[fullName] = @enum;
        _syntax[fullName] = syntax;
    }

    private static string Qualify(string scope, string name) =>
        string.IsNullOrEmpty(scope) ? name : $"{scope}.{name}";
}
