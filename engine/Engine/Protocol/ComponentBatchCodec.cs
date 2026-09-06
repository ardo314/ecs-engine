using System.Collections.Concurrent;
using Ecs.Protocol.V1;
using Google.Protobuf;

namespace Engine.Coordinator;

/// <summary>
/// A column of one component type over a slice of entities, in the shape the rest of
/// the engine works with. <c>null</c> means the entity does not have the component —
/// an empty protobuf message is zero bytes, so length cannot carry absence.
/// </summary>
internal sealed record ComponentColumn(uint TypeId, IReadOnlyList<ulong> Entities, IReadOnlyList<byte[]?> Rows)
{
    public int Count => Math.Min(Entities.Count, Rows.Count);
}

/// <summary>
/// How component columns are put on the wire. The protocol names the encoding but does
/// not depend on it, so the data plane can move to a columnar or shared-memory layout
/// without the control plane changing.
/// </summary>
internal interface IComponentBatchCodec
{
    BatchEncoding Encoding { get; }

    ComponentBatch Encode(ComponentColumn column);

    ComponentColumn Decode(ComponentBatch batch);
}

/// <summary>
/// The codecs this process can speak. Protobuf is always present; others register
/// themselves.
/// </summary>
internal static class ComponentBatchCodecs
{
    private static readonly ConcurrentDictionary<BatchEncoding, IComponentBatchCodec> Registered = new();

    static ComponentBatchCodecs() => Register(ProtobufBatchCodec.Instance);

    /// <summary>
    /// The codec used for outbound batches. Inbound batches are decoded by whatever
    /// encoding they declare, so changing this on one side does not break the other.
    /// </summary>
    public static IComponentBatchCodec Default { get; set; } = ProtobufBatchCodec.Instance;

    public static void Register(IComponentBatchCodec codec)
    {
        ArgumentNullException.ThrowIfNull(codec);
        Registered[codec.Encoding] = codec;
    }

    public static IComponentBatchCodec For(BatchEncoding encoding)
    {
        // Unspecified means "whatever the default was when this was written" — the
        // encoding field was added alongside the first codec, so protobuf is the
        // only thing an unspecified batch can be.
        if (encoding == BatchEncoding.Unspecified) return ProtobufBatchCodec.Instance;

        return Registered.TryGetValue(encoding, out var codec)
            ? codec
            : throw new NotSupportedException(
                $"No component batch codec registered for encoding '{encoding}'.");
    }

    public static ComponentBatch Encode(ComponentColumn column) => Default.Encode(column);

    public static ComponentColumn Decode(ComponentBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        return For(batch.Encoding).Decode(batch);
    }
}

/// <summary>
/// One encoded protobuf message per entity, with a parallel presence bitmap.
/// </summary>
internal sealed class ProtobufBatchCodec : IComponentBatchCodec
{
    public static ProtobufBatchCodec Instance { get; } = new();

    public BatchEncoding Encoding => BatchEncoding.Protobuf;

    public ComponentBatch Encode(ComponentColumn column)
    {
        ArgumentNullException.ThrowIfNull(column);

        var batch = new ComponentBatch
        {
            TypeId = column.TypeId,
            Encoding = BatchEncoding.Protobuf,
        };

        var count = column.Count;
        for (var i = 0; i < count; i++)
        {
            var row = column.Rows[i];
            batch.Entities.Add(column.Entities[i]);
            batch.Present.Add(row is not null);
            batch.Rows.Add(row is null ? ByteString.Empty : ByteString.CopyFrom(row));
        }

        return batch;
    }

    public ComponentColumn Decode(ComponentBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);

        var count = Math.Min(batch.Entities.Count, batch.Rows.Count);
        var entities = new ulong[count];
        var rows = new byte[count][];

        for (var i = 0; i < count; i++)
        {
            entities[i] = batch.Entities[i];
            // A batch written by an older peer may have no presence bitmap at all.
            var present = i >= batch.Present.Count || batch.Present[i];
            rows[i] = present ? batch.Rows[i].ToByteArray() : null!;
        }

        return new ComponentColumn(batch.TypeId, entities, rows!);
    }
}
