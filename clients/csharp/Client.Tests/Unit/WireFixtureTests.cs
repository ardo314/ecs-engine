using System.Text.Json;
using Ecs.V1;
using Engine.Core;
using Engine.Core.Messages;
using Google.Protobuf;
using MessagePack;
using Movement.V1;

namespace Client.Tests.Unit;

/// <summary>
/// Writes the wire fixtures the Node editor's Vitest suite reads back, so the C# and
/// TypeScript sides of the protocol cannot drift apart unnoticed.
/// </summary>
[Trait("Category", "Unit")]
public class WireFixtureTests
{
    private static readonly string FixtureDirectory = Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "editor", "test", "fixtures");

    private static readonly Guid WatchId = Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff");

    public WireFixtureTests() => Serialization.Initialize();

    [Fact]
    public void WritesWireFixturesForTheEditor()
    {
        Directory.CreateDirectory(FixtureDirectory);

        var envelopes = new Dictionary<string, object>
        {
            ["watchResponse"] = new WatchResponse
            {
                WatchId = WatchId,
                DataSubject = $"engine.watch.data.{WatchId}"
            },
            ["watchData"] = BuildWatchData()
        };

        var manifest = new Dictionary<string, string>();
        foreach (var (name, value) in envelopes)
        {
            var bytes = MessagePackSerializer.Serialize(value.GetType(), value, Serialization.Options);
            File.WriteAllBytes(Path.Combine(FixtureDirectory, $"{name}.msgpack"), bytes);
            manifest[name] = MessagePackSerializer.ConvertToJson(bytes, Serialization.Options);
        }

        File.WriteAllBytes(
            Path.Combine(FixtureDirectory, "positionSchema.bin"),
            SchemaOf<Position>().ToByteArray());
        File.WriteAllBytes(
            Path.Combine(FixtureDirectory, "position.bin"),
            new Position { X = 1.5f, Y = -2f, Z = 0f }.ToByteArray());

        File.WriteAllText(
            Path.Combine(FixtureDirectory, "manifest.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

        Assert.True(File.Exists(Path.Combine(FixtureDirectory, "positionSchema.bin")));
    }

    [Fact]
    public void GuidSerializesAsItsCanonicalString()
    {
        var json = MessagePackSerializer.ConvertToJson(
            MessagePackSerializer.Serialize(new WatchCancel { WatchId = WatchId }, Serialization.Options));

        Assert.Equal("{\"WatchId\":\"6f9619ff-8b86-d011-b42d-00c04fc964ff\"}", json);
    }

    private static ComponentSchema SchemaOf<T>() where T : IMessage<T>, new() => new()
    {
        FileDescriptorSet = ByteString.CopyFrom(
            ProtoCodec.FileDescriptorSetFor(ProtoType<T>.Descriptor))
    };

    private static WatchData BuildWatchData() => new()
    {
        WatchId = WatchId,
        TickId = 12345678901234UL,
        Systems =
        [
            new SystemInfo
            {
                Name = "Movement",
                InstanceId = "instance-1",
                Reads = ["movement.v1.Velocity"],
                Writes = ["movement.v1.Position"],
                Queries =
                [
                    new QueryDescriptor
                    {
                        RequiredTypes = ["movement.v1.Position", "movement.v1.Velocity"],
                        ReadTypes = ["movement.v1.Velocity"],
                        WriteTypes = ["movement.v1.Position"]
                    }
                ]
            }
        ],
        Stages = [["Movement"], ["Render"]],
        Entities =
        [
            new EntitySnapshot
            {
                EntityId = 7,
                Components = new Dictionary<string, byte[]>
                {
                    ["movement.v1.Position"] = new Position { X = 1.5f, Y = -2f }.ToByteArray(),
                    ["testing.v1.TestSetting"] = []
                }
            }
        ]
    };
}
