using System.Linq;

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

public record QueryDescriptor
{
    public string[] RequiredTypes { get; init; } = [];
    public string[] OptionalTypes { get; init; } = [];
    public string[] ExcludedTypes { get; init; } = [];
    public string[] ReadTypes { get; init; } = [];
    public string[] WriteTypes { get; init; } = [];

    /// <summary>
    /// Tag component type names. For each entry, an entity must carry at least one
    /// component whose type entity has that component. Resolved per tick by the coordinator.
    /// </summary>
    public string[] TaggedTypes { get; init; } = [];
}

public record SystemDescriptor
{
    public string Name { get; init; } = "";
    public string InstanceId { get; init; } = "";
    public QueryDescriptor[] Queries { get; init; } = [];

    /// <summary>
    /// Union of all read types across all queries. Used for conflict detection.
    /// Methods (not properties) so ContractlessStandardResolver won't serialize them.
    /// </summary>
    public string[] GetAllReads() => Queries.SelectMany(q => q.ReadTypes).Distinct().ToArray();

    /// <summary>
    /// Union of all write types across all queries. Used for conflict detection.
    /// </summary>
    public string[] GetAllWrites() => Queries.SelectMany(q => q.WriteTypes).Distinct().ToArray();

    /// <summary>
    /// Union of all tag types across all queries.
    /// </summary>
    public string[] GetAllTags() => Queries.SelectMany(q => q.TaggedTypes).Distinct().ToArray();
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

    /// <summary>
    /// Tag component type name → the component type names carrying it this tick.
    /// </summary>
    public Dictionary<string, string[]> TaggedTypes { get; init; } = new();
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

public record EntityDestroyRequest
{
    public ulong[] EntityIds { get; init; } = [];
}

public record ComponentAddRequest
{
    public EntityRef Target { get; init; }
    public string ComponentType { get; init; } = "";
    public byte[] Data { get; init; } = [];
}

public record ComponentRemoveRequest
{
    public EntityRef Target { get; init; }
    public string ComponentType { get; init; } = "";
}

// ── Query / Watch API ─────────────────────────────────────────

public record SystemInfo
{
    public string Name { get; init; } = "";
    public string InstanceId { get; init; } = "";
    public string[] Reads { get; init; } = [];
    public string[] Writes { get; init; } = [];
    public QueryDescriptor[] Queries { get; init; } = [];
}

public record QuerySystemsResponse
{
    public SystemInfo[] Systems { get; init; } = [];
    public string[][] Stages { get; init; } = [];
}

public record QueryEntitiesRequest
{
    /// <summary>Entity must have ALL of these component types.</summary>
    public string[]? ComponentFilter { get; init; }

    /// <summary>Entity must have ANY of these component types.</summary>
    public string[]? AnyTypes { get; init; }
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
    public string[]? AnyTypes { get; init; }
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
