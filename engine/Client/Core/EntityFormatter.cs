using MessagePack;
using MessagePack.Formatters;

namespace Engine.Core;

/// <summary>
/// Serializes <see cref="Entity"/> as a bare integer rather than a map, so entity
/// reference components stay compact on the wire and readable in the editor.
/// </summary>
public sealed class EntityFormatter : IMessagePackFormatter<Entity>
{
    public void Serialize(ref MessagePackWriter writer, Entity value, MessagePackSerializerOptions options) =>
        writer.WriteUInt64(value.Id);

    public Entity Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options) =>
        new(reader.ReadUInt64());
}
