using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace Ecs.Protocol;

/// <summary>
/// Turning a component type into descriptors and back. This is the hinge the whole
/// design turns on: a process that has never compiled a type can still hold its
/// schema, hash it and validate payloads against it.
/// </summary>
public static class Descriptors
{
    /// <summary>
    /// The transitively closed <c>FileDescriptorSet</c> for the descriptor's own file,
    /// dependency-first so it can be rebuilt in a single pass.
    /// </summary>
    public static ByteString FileDescriptorSetFor(MessageDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var set = new FileDescriptorSet();
        Collect(descriptor.File, set, new HashSet<string>(StringComparer.Ordinal));
        return set.ToByteString();
    }

    /// <summary>
    /// Rebuilds a descriptor pool from a serialised <c>FileDescriptorSet</c> and returns
    /// the message named <paramref name="logicalName"/>.
    /// </summary>
    /// <exception cref="InvalidDescriptorSetException">
    /// The set was unreadable, was missing a dependency, or did not contain the name.
    /// </exception>
    public static MessageDescriptor Resolve(ByteString fileDescriptorSet, string logicalName)
    {
        ArgumentNullException.ThrowIfNull(fileDescriptorSet);
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalName);

        FileDescriptorSet set;
        try
        {
            set = FileDescriptorSet.Parser.ParseFrom(fileDescriptorSet);
        }
        catch (InvalidProtocolBufferException ex)
        {
            throw new InvalidDescriptorSetException("FileDescriptorSet is not valid protobuf.", ex);
        }

        IReadOnlyList<FileDescriptor> files;
        try
        {
            files = FileDescriptor.BuildFromByteStrings(set.File.Select(f => f.ToByteString()));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidProtocolBufferException)
        {
            throw new InvalidDescriptorSetException(
                $"FileDescriptorSet for '{logicalName}' could not be built: {ex.Message}", ex);
        }

        foreach (var file in files)
        {
            if (FindMessage(file.MessageTypes, logicalName) is { } found)
                return found;
        }

        throw new InvalidDescriptorSetException(
            $"FileDescriptorSet does not contain a message named '{logicalName}'.");
    }

    private static MessageDescriptor? FindMessage(IEnumerable<MessageDescriptor> messages, string fullName)
    {
        foreach (var message in messages)
        {
            if (message.FullName == fullName) return message;
            if (FindMessage(message.NestedTypes, fullName) is { } nested) return nested;
        }
        return null;
    }

    private static void Collect(FileDescriptor file, FileDescriptorSet set, HashSet<string> seen)
    {
        if (!seen.Add(file.Name)) return;
        foreach (var dependency in file.Dependencies)
            Collect(dependency, set, seen);
        set.File.Add(file.ToProto());
    }
}

public sealed class InvalidDescriptorSetException : Exception
{
    public InvalidDescriptorSetException(string message) : base(message) { }
    public InvalidDescriptorSetException(string message, Exception inner) : base(message, inner) { }
}
