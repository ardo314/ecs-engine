using Engine.Core;
using Engine.Core.Messages;
using MessagePack;
using NATS.Client.Core;

namespace Engine.Tests.Integration;

/// <summary>
/// Integration tests for system registration/unregistration and entity spawn via NATS.
/// Uses the shared coordinator from NatsFixture.
/// </summary>
[Collection("NATS")]
[Trait("Category", "Integration")]
public class NatsSystemLifecycleIntegrationTests : IAsyncLifetime
{
    private readonly NatsFixture _fixture;
    private NatsConnection _clientNats = null!;

    public NatsSystemLifecycleIntegrationTests(NatsFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _fixture.EnsureAvailable();
        Serialization.Initialize();
        _clientNats = await _fixture.ConnectAsync();
    }

    public async Task DisposeAsync()
    {
        await _clientNats.DisposeAsync();
    }

    [Fact]
    public async Task SystemRegister_AppearsInRegistry()
    {
        var descriptor = new SystemDescriptor
        {
            Name = "LifecycleTest",
            InstanceId = Guid.NewGuid().ToString(),
            Reads = ["LA"],
            Writes = ["LB"]
        };

        await _clientNats.PublishAsync("engine.system.register",
            MessagePackSerializer.Serialize(descriptor));

        await Task.Delay(300);

        Assert.Contains("LifecycleTest", _fixture.Registry.GetSystemNames());
    }

    [Fact]
    public async Task SystemUnregister_RemovesFromRegistry()
    {
        var instanceId = Guid.NewGuid().ToString();
        var descriptor = new SystemDescriptor
        {
            Name = "ToRemove",
            InstanceId = instanceId,
            Reads = [],
            Writes = ["LC"]
        };

        await _clientNats.PublishAsync("engine.system.register",
            MessagePackSerializer.Serialize(descriptor));
        await Task.Delay(300);
        Assert.Contains("ToRemove", _fixture.Registry.GetSystemNames());

        await _clientNats.PublishAsync("engine.system.unregister",
            MessagePackSerializer.Serialize(new SystemUnregister { Name = "ToRemove", InstanceId = instanceId }));
        await Task.Delay(300);

        Assert.DoesNotContain("ToRemove", _fixture.Registry.GetSystemNames());
    }

    [Fact]
    public async Task SystemRegister_NotifiesWatchManager()
    {
        // Register a watch
        var watchId = Guid.NewGuid();
        _fixture.WatchManager.Register(new WatchRequest
        {
            WatchId = watchId,
            IncludeSystems = true,
            IncludeEntities = false
        });

        // Consume initial systems version
        var spec = _fixture.WatchManager.GetActiveWatches().First(w => w.WatchId == watchId);
        _fixture.WatchManager.ShouldIncludeSystems(spec);

        // Should be false now (no change yet)
        spec = _fixture.WatchManager.GetActiveWatches().First(w => w.WatchId == watchId);
        Assert.False(_fixture.WatchManager.ShouldIncludeSystems(spec));

        // Register a system via NATS — should bump version
        await _clientNats.PublishAsync("engine.system.register",
            MessagePackSerializer.Serialize(new SystemDescriptor
            {
                Name = "NotifyTest",
                InstanceId = Guid.NewGuid().ToString(),
                Reads = [],
                Writes = ["LD"]
            }));
        await Task.Delay(300);

        // Now should be true again (systems changed)
        spec = _fixture.WatchManager.GetActiveWatches().First(w => w.WatchId == watchId);
        Assert.True(_fixture.WatchManager.ShouldIncludeSystems(spec));

        // Cleanup
        _fixture.WatchManager.Cancel(watchId);
    }

    [Fact]
    public async Task EntitySpawnRequest_EnqueuesSpawn()
    {
        var spawnReq = new EntitySpawnRequest
        {
            ComponentTypes = ["SpawnPos", "SpawnVel"],
            ComponentData =
            [
                MessagePackSerializer.Serialize(new { X = 1.0f }),
                MessagePackSerializer.Serialize(new { X = 0.5f })
            ]
        };

        await _clientNats.PublishAsync("engine.entity.spawn.request",
            MessagePackSerializer.Serialize(spawnReq));

        await Task.Delay(300);

        Assert.True(_fixture.PendingSpawns.TryDequeue(out var dequeued));
        Assert.Equal(["SpawnPos", "SpawnVel"], dequeued.ComponentTypes);
        Assert.Equal(2, dequeued.ComponentData.Length);
    }

    [Fact]
    public async Task FullFlow_RegisterSystem_CreateEntity_QueryBoth()
    {
        // Register a system via NATS
        await _clientNats.PublishAsync("engine.system.register",
            MessagePackSerializer.Serialize(new SystemDescriptor
            {
                Name = "FullFlowSys",
                InstanceId = Guid.NewGuid().ToString(),
                Reads = ["Vel_FF"],
                Writes = ["Pos_FF"]
            }));
        await Task.Delay(300);

        // Create an entity directly
        var e = _fixture.World.AllocateEntity();
        _fixture.World.SetComponent(e, "Pos_FF", MessagePackSerializer.Serialize(new { X = 10f }));
        _fixture.World.SetComponent(e, "Vel_FF", MessagePackSerializer.Serialize(new { X = 1f }));

        // Query systems
        var sysReply = await _clientNats.RequestAsync<byte[], byte[]>(
            "engine.query.systems", [],
            cancellationToken: new CancellationTokenSource(5000).Token);
        var sysResp = MessagePackSerializer.Deserialize<QuerySystemsResponse>(sysReply.Data!);

        Assert.Contains(sysResp.Systems, s => s.Name == "FullFlowSys");

        // Query entities with filter
        var entReply = await _clientNats.RequestAsync<byte[], byte[]>(
            "engine.query.entities",
            MessagePackSerializer.Serialize(new QueryEntitiesRequest { ComponentFilter = ["Pos_FF", "Vel_FF"] }),
            cancellationToken: new CancellationTokenSource(5000).Token);
        var entResp = MessagePackSerializer.Deserialize<QueryEntitiesResponse>(entReply.Data!);

        Assert.Contains(entResp.Entities, ent => ent.EntityId == e);
        var entity = entResp.Entities.First(ent => ent.EntityId == e);
        Assert.True(entity.Components.ContainsKey("Pos_FF"));
        Assert.True(entity.Components.ContainsKey("Vel_FF"));
    }
}
