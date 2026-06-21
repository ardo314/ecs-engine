using Engine.Core;
using Engine.Core.Messages;
using MessagePack;
using NATS.Client.Core;

namespace Engine.Tests.Integration;

/// <summary>
/// Integration tests that verify the coordinator's NATS query APIs
/// by sending real messages through a NATS server.
/// Uses the shared coordinator from NatsFixture.
/// </summary>
[Collection("NATS")]
[Trait("Category", "Integration")]
public class NatsQueryIntegrationTests : IAsyncLifetime
{
    private readonly NatsFixture _fixture;
    private NatsConnection _clientNats = null!;

    public NatsQueryIntegrationTests(NatsFixture fixture)
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
    public async Task SystemRegistration_ViaQuery_ReturnsRegisteredSystems()
    {
        var descriptor = new SystemDescriptor
        {
            Name = "TestMovement",
            InstanceId = Guid.NewGuid().ToString(),
            Reads = ["Velocity"],
            Writes = ["Position"]
        };
        await _clientNats.PublishAsync("engine.system.register",
            MessagePackSerializer.Serialize(descriptor));

        await Task.Delay(300);

        var reply = await _clientNats.RequestAsync<byte[], byte[]>(
            "engine.query.systems", [],
            cancellationToken: new CancellationTokenSource(5000).Token);

        var response = MessagePackSerializer.Deserialize<QuerySystemsResponse>(reply.Data!);

        Assert.Contains(response.Systems, s => s.Name == "TestMovement");
        var sys = response.Systems.First(s => s.Name == "TestMovement");
        Assert.Equal(["Velocity"], sys.Reads);
        Assert.Equal(["Position"], sys.Writes);
    }

    [Fact]
    public async Task QuerySystems_ReturnsStages()
    {
        _fixture.Registry.Register(new SystemDescriptor
        {
            Name = "Physics_Q",
            InstanceId = Guid.NewGuid().ToString(),
            Reads = [],
            Writes = ["Transform_Q"]
        });
        _fixture.Registry.Register(new SystemDescriptor
        {
            Name = "Render_Q",
            InstanceId = Guid.NewGuid().ToString(),
            Reads = ["Transform_Q"],
            Writes = []
        });

        var reply = await _clientNats.RequestAsync<byte[], byte[]>(
            "engine.query.systems", [],
            cancellationToken: new CancellationTokenSource(5000).Token);

        var response = MessagePackSerializer.Deserialize<QuerySystemsResponse>(reply.Data!);

        // Physics_Q and Render_Q conflict, so at least 2 stages
        Assert.True(response.Stages.Length >= 2);
    }

    [Fact]
    public async Task QueryEntities_ReturnsEntities()
    {
        var e1 = _fixture.World.AllocateEntity();
        _fixture.World.SetComponent(e1, "QPos", [1, 2]);

        var reply = await _clientNats.RequestAsync<byte[], byte[]>(
            "engine.query.entities", [],
            cancellationToken: new CancellationTokenSource(5000).Token);

        var response = MessagePackSerializer.Deserialize<QueryEntitiesResponse>(reply.Data!);

        Assert.Contains(response.Entities, e => e.EntityId == e1);
    }

    [Fact]
    public async Task QueryEntities_WithComponentFilter_FiltersCorrectly()
    {
        var e1 = _fixture.World.AllocateEntity();
        _fixture.World.SetComponent(e1, "FilterA", [1]);
        _fixture.World.SetComponent(e1, "FilterB", [2]);

        var e2 = _fixture.World.AllocateEntity();
        _fixture.World.SetComponent(e2, "FilterA", [3]);
        // e2 only has FilterA, not FilterB

        var request = new QueryEntitiesRequest { ComponentFilter = ["FilterA", "FilterB"] };
        var reply = await _clientNats.RequestAsync<byte[], byte[]>(
            "engine.query.entities",
            MessagePackSerializer.Serialize(request),
            cancellationToken: new CancellationTokenSource(5000).Token);

        var response = MessagePackSerializer.Deserialize<QueryEntitiesResponse>(reply.Data!);

        Assert.Contains(response.Entities, e => e.EntityId == e1);
        Assert.DoesNotContain(response.Entities, e => e.EntityId == e2);
    }

    [Fact]
    public async Task QueryEntities_IncludesComponentData()
    {
        var e = _fixture.World.AllocateEntity();
        byte[] posData = [10, 20, 30];
        _fixture.World.SetComponent(e, "QCompData", posData);

        var reply = await _clientNats.RequestAsync<byte[], byte[]>(
            "engine.query.entities", [],
            cancellationToken: new CancellationTokenSource(5000).Token);

        var response = MessagePackSerializer.Deserialize<QueryEntitiesResponse>(reply.Data!);
        var entity = response.Entities.First(ent => ent.EntityId == e);

        Assert.True(entity.Components.ContainsKey("QCompData"));
        Assert.Equal(posData, entity.Components["QCompData"]);
    }
}
