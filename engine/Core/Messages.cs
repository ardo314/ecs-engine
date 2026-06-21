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

// ── Query / Watch API ─────────────────────────────────────────

public record SystemInfo
{
    public string Name { get; init; } = "";
    public string InstanceId { get; init; } = "";
    public string[] Reads { get; init; } = [];
    public string[] Writes { get; init; } = [];
}

public record QuerySystemsResponse
{
    public SystemInfo[] Systems { get; init; } = [];
    public string[][] Stages { get; init; } = [];
}

public record QueryEntitiesRequest
{
    public string[]? ComponentFilter { get; init; }
}

public record QueryEntitiesResponse
{
    public EntitySnapshot[] Entities { get; init; } = [];
}

public record EntitySnapshot
{
    public ulong EntityId { get; init; }
    public Dictionary<string, byte[]> Components { get; init; } = new();
}

public record WatchRequest
{
    public Guid WatchId { get; init; }
    public bool IncludeSystems { get; init; }
    public bool IncludeEntities { get; init; }
    public string[]? ComponentFilter { get; init; }
}

public record WatchResponse
{
    public Guid WatchId { get; init; }
    public string DataSubject { get; init; } = "";
}

public record WatchCancel
{
    public Guid WatchId { get; init; }
}

public record WatchData
{
    public Guid WatchId { get; init; }
    public ulong TickId { get; init; }
    public SystemInfo[]? Systems { get; init; }
    public string[][]? Stages { get; init; }
    public EntitySnapshot[]? Entities { get; init; }
}
