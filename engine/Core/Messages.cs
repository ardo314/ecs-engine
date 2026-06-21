namespace Engine.Core.Messages;

public record TickStart
{
    public ulong TickId { get; init; }
    public float Dt { get; init; }
}

public record TickAck
{
    public ulong TickId { get; init; }
    public string InstanceId { get; init; } = "";
}

public record SystemDescriptor
{
    public string Name { get; init; } = "";
    public string InstanceId { get; init; } = "";
    public string[] Reads { get; init; } = [];
    public string[] Writes { get; init; } = [];
}

public record SystemUnregister
{
    public string Name { get; init; } = "";
    public string InstanceId { get; init; } = "";
}

public record SystemSchedule
{
    public ulong TickId { get; init; }
    public int ShardCount { get; init; }
}

public record ComponentShard
{
    public ulong TickId { get; init; }
    public ulong[] Entities { get; init; } = [];
    public string ComponentType { get; init; } = "";
    public byte[] Data { get; init; } = [];
}

public record ComponentChanges
{
    public ulong TickId { get; init; }
    public string ComponentType { get; init; } = "";
    public ulong[] Entities { get; init; } = [];
    public byte[] Data { get; init; } = [];
}

public record EntitySpawnRequest
{
    public string[] ComponentTypes { get; init; } = [];
    public byte[][] ComponentData { get; init; } = [];
}

public record EntityCreated
{
    public ulong EntityId { get; init; }
    public string[] ComponentTypes { get; init; } = [];
}

public record EntityDestroyed
{
    public ulong EntityId { get; init; }
}
