using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace Engine.Core;

/// <summary>
/// Protobuf encoding for component payloads, plus the reflection the SDK needs to
/// describe a component type to the coordinator.
/// </summary>
public static class ProtoCodec
{
    public static byte[] Encode(IMessage component) => component.ToByteArray();

    public static T Decode<T>(byte[] data) where T : IMessage<T>, new()
    {
        var message = new T();
        message.MergeFrom(data);
        return message;
    }

    /// <summary>
    /// The transitively closed FileDescriptorSet for the descriptor's own file, so a
    /// consumer that has never seen the type can still decode instances of it.
    /// </summary>
    public static byte[] FileDescriptorSetFor(MessageDescriptor descriptor)
    {
        var set = new FileDescriptorSet();
        Collect(descriptor.File, set, new HashSet<string>());
        return set.ToByteArray();
    }

    /// <summary>
    /// The components a type declares for its own type entity through the
    /// <c>ecs.v1.description</c> message option.
    /// </summary>
    public static IEnumerable<(string TypeName, byte[] Data)> DescriptionOf(MessageDescriptor descriptor)
    {
        var options = descriptor.GetOptions();
        if (options is null) yield break;

        foreach (var attachment in options.GetExtension(Ecs.V1.ComponentExtensions.Description))
        {
            var typeUrl = attachment.TypeUrl;
            yield return (typeUrl[(typeUrl.LastIndexOf('/') + 1)..], attachment.Value.ToByteArray());
        }
    }

    private static void Collect(FileDescriptor file, FileDescriptorSet set, HashSet<string> seen)
    {
        if (!seen.Add(file.Name)) return;
        foreach (var dependency in file.Dependencies)
            Collect(dependency, set, seen);
        set.File.Add(file.ToProto());
    }
}

/// <summary>Per-type protobuf reflection, resolved once.</summary>
public static class ProtoType<T> where T : IMessage<T>, new()
{
    public static readonly MessageDescriptor Descriptor = new T().Descriptor;
    public static readonly string FullName = Descriptor.FullName;
}
