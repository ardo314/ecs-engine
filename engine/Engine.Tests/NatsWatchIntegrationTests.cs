using Engine.Core;
using Engine.Core.Messages;
using MessagePack;
using NATS.Client.Core;

namespace Engine.Tests;

/// <summary>
/// Integration tests for the watch subscribe/unsubscribe/data push flow over NATS.
/// Uses the shared coordinator from NatsFixture.
/// </summary>
[Collection("NATS")]
public class NatsWatchIntegrationTests : IAsyncLifetime
{
    private readonly NatsFixture _fixture;
    private NatsConnection _clientNats = null!;

    public NatsWatchIntegrationTests(NatsFixture fixture)
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
    public async Task WatchSubscribe_ReturnsDataSubject()
    {
        var watchId = Guid.NewGuid();
        var request = new WatchRequest
        {
            WatchId = watchId,
            IncludeSystems = true,
            IncludeEntities = true
        };

        var reply = await _clientNats.RequestAsync<byte[], byte[]>(
            "engine.watch.subscribe",
            MessagePackSerializer.Serialize(request),
            cancellationToken: new CancellationTokenSource(5000).Token);

        var response = MessagePackSerializer.Deserialize<WatchResponse>(reply.Data!);

        Assert.Equal(watchId, response.WatchId);
        Assert.Equal($"engine.watch.data.{watchId}", response.DataSubject);

        // Cleanup
        _fixture.WatchManager.Cancel(watchId);
    }

    [Fact]
    public async Task WatchSubscribe_CreatesActiveWatch()
    {
        var watchId = Guid.NewGuid();
        var request = new WatchRequest
        {
            WatchId = watchId,
            IncludeSystems = true,
            IncludeEntities = false
        };

        await _clientNats.RequestAsync<byte[], byte[]>(
            "engine.watch.subscribe",
            MessagePackSerializer.Serialize(request),
            cancellationToken: new CancellationTokenSource(5000).Token);

        var watches = _fixture.WatchManager.GetActiveWatches();
        Assert.Contains(watches, w => w.WatchId == watchId);

        // Cleanup
        _fixture.WatchManager.Cancel(watchId);
    }

    [Fact]
    public async Task WatchUnsubscribe_RemovesWatch()
    {
        var watchId = Guid.NewGuid();

        // Subscribe first
        await _clientNats.RequestAsync<byte[], byte[]>(
            "engine.watch.subscribe",
            MessagePackSerializer.Serialize(new WatchRequest
            {
                WatchId = watchId,
                IncludeSystems = true,
                IncludeEntities = true
            }),
            cancellationToken: new CancellationTokenSource(5000).Token);

        Assert.Contains(_fixture.WatchManager.GetActiveWatches(), w => w.WatchId == watchId);

        // Unsubscribe
        await _clientNats.PublishAsync("engine.watch.unsubscribe",
            MessagePackSerializer.Serialize(new WatchCancel { WatchId = watchId }));

        await Task.Delay(300);

        Assert.DoesNotContain(_fixture.WatchManager.GetActiveWatches(), w => w.WatchId == watchId);
    }

    [Fact]
    public async Task WatchDataPush_DeliversEntityData()
    {
        // Set up an entity
        var e = _fixture.World.AllocateEntity();
        _fixture.World.SetComponent(e, "WatchPos", [42]);

        // Subscribe a watch via NATS
        var watchId = Guid.NewGuid();
        var reply = await _clientNats.RequestAsync<byte[], byte[]>(
            "engine.watch.subscribe",
            MessagePackSerializer.Serialize(new WatchRequest
            {
                WatchId = watchId,
                IncludeSystems = true,
                IncludeEntities = true
            }),
            cancellationToken: new CancellationTokenSource(5000).Token);

        var watchResponse = MessagePackSerializer.Deserialize<WatchResponse>(reply.Data!);

        // Subscribe to the data subject BEFORE pushing
        var dataSub = await _clientNats.SubscribeCoreAsync<byte[]>(
            watchResponse.DataSubject);

        // Simulate what the tick loop does: push watch data
        var watches = _fixture.WatchManager.GetActiveWatches();
        var watch = watches.First(w => w.WatchId == watchId);

        var data = new WatchData { WatchId = watch.WatchId, TickId = 1 };

        if (_fixture.WatchManager.ShouldIncludeSystems(watch))
        {
            var sysResp = _fixture.Handlers.BuildSystemsResponse();
            data = data with { Systems = sysResp.Systems, Stages = sysResp.Stages };
        }

        if (watch.IncludeEntities)
        {
            var entResp = _fixture.Handlers.BuildEntitiesResponse(watch.ComponentFilter);
            data = data with { Entities = entResp.Entities };
        }

        var coordNats = await _fixture.ConnectAsync();
        await coordNats.PublishAsync(watch.DataSubject, MessagePackSerializer.Serialize(data));

        // Read the data from the subscription
        using var readCts = new CancellationTokenSource(5000);
        WatchData? received = null;

        await foreach (var msg in dataSub.Msgs.ReadAllAsync(readCts.Token))
        {
            received = MessagePackSerializer.Deserialize<WatchData>(msg.Data!);
            break;
        }

        Assert.NotNull(received);
        Assert.Equal(watchId, received.WatchId);
        Assert.Equal(1UL, received.TickId);
        Assert.NotNull(received.Entities);
        Assert.Contains(received.Entities, ent => ent.EntityId == e);

        var entity = received.Entities.First(ent => ent.EntityId == e);
        Assert.True(entity.Components.ContainsKey("WatchPos"));
        Assert.Equal([42], entity.Components["WatchPos"]);

        // Cleanup
        _fixture.WatchManager.Cancel(watchId);
        await dataSub.UnsubscribeAsync();
        await coordNats.DisposeAsync();
    }

    [Fact]
    public async Task WatchDataPush_IncludesSystemsOnFirstPush()
    {
        // Register a system directly
        _fixture.Registry.Register(new SystemDescriptor
        {
            Name = "WatchSysTest",
            InstanceId = Guid.NewGuid().ToString(),
            Reads = ["WA"],
            Writes = ["WB"]
        });
        _fixture.WatchManager.NotifySystemsChanged();

        // Subscribe a watch
        var watchId = Guid.NewGuid();
        _fixture.WatchManager.Register(new WatchRequest
        {
            WatchId = watchId,
            IncludeSystems = true,
            IncludeEntities = false
        });

        // First call should include systems
        var spec = _fixture.WatchManager.GetActiveWatches().First(w => w.WatchId == watchId);
        Assert.True(_fixture.WatchManager.ShouldIncludeSystems(spec));

        // Cleanup
        _fixture.WatchManager.Cancel(watchId);
    }

    [Fact]
    public async Task WatchDataPush_OmitsSystemsWhenUnchanged()
    {
        _fixture.Registry.Register(new SystemDescriptor
        {
            Name = "StableWatch",
            InstanceId = Guid.NewGuid().ToString(),
            Reads = [],
            Writes = ["WStable"]
        });
        _fixture.WatchManager.NotifySystemsChanged();

        var watchId = Guid.NewGuid();
        _fixture.WatchManager.Register(new WatchRequest
        {
            WatchId = watchId,
            IncludeSystems = true,
            IncludeEntities = false
        });

        // First push — should include systems
        var spec = _fixture.WatchManager.GetActiveWatches().First(w => w.WatchId == watchId);
        Assert.True(_fixture.WatchManager.ShouldIncludeSystems(spec));

        // Second push — should NOT include systems (no change)
        spec = _fixture.WatchManager.GetActiveWatches().First(w => w.WatchId == watchId);
        Assert.False(_fixture.WatchManager.ShouldIncludeSystems(spec));

        // Cleanup
        _fixture.WatchManager.Cancel(watchId);
    }
}
