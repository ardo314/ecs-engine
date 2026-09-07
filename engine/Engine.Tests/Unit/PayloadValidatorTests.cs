using Engine.Coordinator;
using Google.Protobuf;
using Testing.V1;

namespace Engine.Tests.Unit;

/// <summary>
/// The world stores payloads it cannot decode, so the only thing standing between a
/// corrupt or misrouted message and the world's state is this check.
/// </summary>
public class PayloadValidatorTests
{
    [Fact]
    public void AcceptsAPayloadOfTheDeclaredType()
    {
        var payload = new TestPosition { X = 1.5f, Y = -2f }.ToByteArray();

        Assert.True(PayloadValidator.IsValid(TestPosition.Descriptor, payload, out var error));
        Assert.Equal("", error);
    }

    [Fact]
    public void AcceptsAnEmptyPayload()
    {
        // A marker component is zero bytes, and that is a valid message.
        Assert.True(PayloadValidator.IsValid(TestPosition.Descriptor, [], out _));
    }

    [Fact]
    public void RejectsTruncatedBytes()
    {
        var payload = new TestPosition { X = 1.5f, Y = -2f }.ToByteArray();

        Assert.False(PayloadValidator.IsValid(TestPosition.Descriptor, payload.AsSpan(0, 2), out var error));
        Assert.NotEqual("", error);
    }

    [Fact]
    public void RejectsAFieldCarryingTheWrongWireType()
    {
        // testing.v1.TestPosition field 1 is a float, so it must arrive as fixed32.
        // Field 1 as a varint is a different schema wearing the same field numbers.
        var payload = new byte[] { 0x08, 0x01 };

        Assert.False(PayloadValidator.IsValid(TestPosition.Descriptor, payload, out var error));
        Assert.Contains("x", error, StringComparison.Ordinal);
    }

    [Fact]
    public void AllowsFieldNumbersTheSchemaDoesNotKnow()
    {
        // proto3 keeps unknown fields, and rejecting them here would make the world
        // stricter than protobuf itself.
        var payload = new TestPosition { X = 1f }.ToByteArray()
            .Concat([(byte)0xF8, (byte)0x03, (byte)0x2A])
            .ToArray();

        Assert.True(PayloadValidator.IsValid(TestPosition.Descriptor, payload, out _));
    }

    [Fact]
    public void ValidatesNestedMessages()
    {
        var valid = new TestParent { Position = new TestPosition { X = 1f } }.ToByteArray();
        Assert.True(PayloadValidator.IsValid(TestParent.Descriptor, valid, out _));

        // Field 1 of TestParent is a message; hand it bytes that are not one.
        var corrupt = new byte[] { 0x0A, 0x02, 0x08, 0xFF };
        Assert.False(PayloadValidator.IsValid(TestParent.Descriptor, corrupt, out _));
    }
}
