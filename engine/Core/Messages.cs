using MessagePack;

namespace Engine.Core.Messages;

[MessagePackObject]
public record TickStart
{
    [Key(0)]
    public ulong TickId { get; init; }

    [Key(1)]
    public float Dt { get; init; }
}

[MessagePackObject]
public record TickAck
{
    [Key(0)]
    public ulong TickId { get; init; }

    [Key(1)]
    public string InstanceId { get; init; } = "";
}

[MessagePackObject]
public record SystemDescriptor
{
    [Key(0)]
    public string Name { get; init; } = "";

    [Key(1)]
    public string InstanceId { get; init; } = "";

    [Key(2)]
    public string[] Reads { get; init; } = [];

    [Key(3)]
    public string[] Writes { get; init; } = [];
}

[MessagePackObject]
public record SystemUnregister
{
    [Key(0)]
    public string Name { get; init; } = "";

    [Key(1)]
    public string InstanceId { get; init; } = "";
}

[MessagePackObject]
public record SystemSchedule
{
    [Key(0)]
    public ulong TickId { get; init; }

    [Key(1)]
    public int ShardCount { get; init; }
}

[MessagePackObject]
public record ComponentShard
{
    [Key(0)]
    public ulong TickId { get; init; }

    [Key(1)]
    public ulong[] Entities { get; init; } = [];

    [Key(2)]
    public string ComponentType { get; init; } = "";

    [Key(3)]
    public byte[] Data { get; init; } = [];
}

[MessagePackObject]
public record ComponentChanges
{
    [Key(0)]
    public ulong TickId { get; init; }

    [Key(1)]
    public string ComponentType { get; init; } = "";

    [Key(2)]
    public ulong[] Entities { get; init; } = [];

    [Key(3)]
    public byte[] Data { get; init; } = [];
}

[MessagePackObject]
public record EntitySpawnRequest
{
    [Key(0)]
    public string[] ComponentTypes { get; init; } = [];

    [Key(1)]
    public byte[][] ComponentData { get; init; } = [];
}

[MessagePackObject]
public record EntityCreated
{
    [Key(0)]
    public ulong EntityId { get; init; }

    [Key(1)]
    public string[] ComponentTypes { get; init; } = [];
}

[MessagePackObject]
public record EntityDestroyed
{
    [Key(0)]
    public ulong EntityId { get; init; }
}
