using Ecs.Protocol.V1;
using Engine.Coordinator;
using Google.Protobuf;
using Testing.V1;

namespace Engine.Tests.Unit;

public class ComponentBatchCodecTests
{
    [Fact]
    public void ProtobufCodec_RoundTripsAColumn()
    {
        var rows = new byte[]?[]
        {
            new TestPosition { X = 1, Y = 2 }.ToByteArray(),
            new TestPosition { X = 4, Y = 5 }.ToByteArray(),
        };

        var decoded = ComponentBatchCodecs.Decode(
            ComponentBatchCodecs.Encode(new ComponentColumn(17, [10ul, 20ul], rows)));

        Assert.Equal(17u, decoded.TypeId);
        Assert.Equal([10ul, 20ul], decoded.Entities);
        Assert.Equal(rows[0], decoded.Rows[0]);
        Assert.Equal(rows[1], decoded.Rows[1]);
    }

    [Fact]
    public void AbsenceIsCarriedByThePresenceBitmapNotByLength()
    {
        // An all-default protobuf message encodes to zero bytes, so a zero-length row is
        // a genuine marker component and cannot be used to mean "missing".
        var marker = new TestPosition().ToByteArray();
        Assert.Empty(marker);

        var decoded = ComponentBatchCodecs.Decode(
            ComponentBatchCodecs.Encode(new ComponentColumn(1, [1ul, 2ul], [marker, null])));

        Assert.NotNull(decoded.Rows[0]);
        Assert.Empty(decoded.Rows[0]!);
        Assert.Null(decoded.Rows[1]);
    }

    [Fact]
    public void EncodedBatchNamesItsEncoding()
    {
        var batch = ComponentBatchCodecs.Encode(new ComponentColumn(1, [1ul], [[]]));

        Assert.Equal(BatchEncoding.Protobuf, batch.Encoding);
    }

    [Fact]
    public void AnUnspecifiedEncodingIsReadAsProtobuf()
    {
        // The encoding field was introduced alongside the first codec, so a batch that
        // does not name one can only be protobuf.
        var batch = ComponentBatchCodecs.Encode(new ComponentColumn(1, [7ul], [[]]));
        batch.Encoding = BatchEncoding.Unspecified;

        Assert.Equal([7ul], ComponentBatchCodecs.Decode(batch).Entities);
    }

    [Fact]
    public void AnEncodingThisProcessCannotSpeakIsRefusedRatherThanGuessedAt()
    {
        var batch = new ComponentBatch { TypeId = 1, Encoding = BatchEncoding.Arrow };

        Assert.Throws<NotSupportedException>(() => ComponentBatchCodecs.Decode(batch));
    }
}
