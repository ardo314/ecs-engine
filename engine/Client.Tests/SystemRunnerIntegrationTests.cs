using Client;
using Engine.Core;
using Engine.Core.Messages;
using MessagePack;
using NATS.Client.Core;

namespace Client.Tests;

/// <summary>
/// Integration tests for SystemRunner against a running engine coordinator.
/// Start the engine first: docker compose up nats engine -d
/// All verification is done via NATS query APIs — no direct engine references.
/// </summary>
[Collection("NATS")]
public class SystemRunnerIntegrationTests : IAsyncLifetime
{
    private readonly NatsClientFixture _fixture;
    private NatsConnection _nats = null!;

    public SystemRunnerIntegrationTests(NatsClientFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _fixture.EnsureAvailable();
        _nats = new NatsConnection(new NatsOpts { Url = _fixture.Url });
        await _nats.ConnectAsync();
    }

    public async Task DisposeAsync()
    {
        await _nats.DisposeAsync();
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private async Task<QuerySystemsResponse> QuerySystemsAsync()
    {
        var reply = await _nats.RequestAsync<byte[], byte[]>(
            "engine.query.systems", Array.Empty<byte>());
        return MessagePackSerializer.Deserialize<QuerySystemsResponse>(reply.Data!);
    }

    private async Task<QueryEntitiesResponse> QueryEntitiesAsync(string[]? filter = null)
    {
        var request = new QueryEntitiesRequest { ComponentFilter = filter };
        var reply = await _nats.RequestAsync<byte[], byte[]>(
            "engine.query.entities",
            MessagePackSerializer.Serialize(request));
        return MessagePackSerializer.Deserialize<QueryEntitiesResponse>(reply.Data!);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
                return;
            await Task.Delay(150);
        }
    }

    // ── Tests ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ConnectAsync_EstablishesConnection()
    {
        await using var runner = new SystemRunner("ConnectTest", natsUrl: _fixture.Url);
        await runner.ConnectAsync();
    }

    [Fact]
    public async Task SpawnEntityAsync_ThrowsBeforeConnect()
    {
        await using var runner = new SystemRunner("NoConnect", natsUrl: _fixture.Url);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.SpawnEntityAsync(new TestPosition()));
    }

    [Fact]
    public async Task RunAsync_ThrowsBeforeConnect()
    {
        await using var runner = new SystemRunner("NoConnectRun", natsUrl: _fixture.Url);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.RunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task InstanceId_IsUnique()
    {
        await using var r1 = new SystemRunner("Id1", natsUrl: _fixture.Url);
        await using var r2 = new SystemRunner("Id2", natsUrl: _fixture.Url);
        Assert.NotEqual(r1.InstanceId, r2.InstanceId);
        Assert.NotEmpty(r1.InstanceId);
    }

    [Fact]
    public async Task SpawnEntityAsync_EntityAppearsViaQuery()
    {
        await using var runner = new SystemRunner("SpawnQ", natsUrl: _fixture.Url);
        await runner.ConnectAsync();

        var typeName = ComponentTypeId.Of<TestPosition>().TypeName;
        await runner.SpawnEntityAsync(new TestPosition { X = 77.0f, Y = 88.0f });

        await WaitUntilAsync(async () =>
        {
            var resp = await QueryEntitiesAsync(new[] { typeName });
            return resp.Entities.Any(e => e.Components.ContainsKey(typeName));
        }, TimeSpan.FromSeconds(5));

        var entities = await QueryEntitiesAsync(new[] { typeName });
        Assert.NotEmpty(entities.Entities);
    }

    [Fact]
    public async Task SpawnEntityAsync_MultipleComponents_AllStoredCorrectly()
    {
        await using var runner = new SystemRunner("SpawnMulti", natsUrl: _fixture.Url);
        await runner.ConnectAsync();

        var posType = ComponentTypeId.Of<TestPosition>().TypeName;
        var velType = ComponentTypeId.Of<TestVelocity>().TypeName;

        await runner.SpawnEntityAsync(
            new TestPosition { X = 11.0f, Y = 22.0f },
            new TestVelocity { Vx = 3.0f, Vy = 4.0f });

        await WaitUntilAsync(async () =>
        {
            var resp = await QueryEntitiesAsync(new[] { posType, velType });
            return resp.Entities.Length > 0;
        }, TimeSpan.FromSeconds(5));

        var result = await QueryEntitiesAsync(new[] { posType, velType });
        Assert.NotEmpty(result.Entities);

        // Find the specific entity by matching the velocity (which is never mutated)
        EntitySnapshot? match = null;
        foreach (var e in result.Entities)
        {
            if (!e.Components.ContainsKey(velType)) continue;
            var v = MessagePackSerializer.Deserialize<TestVelocity>(e.Components[velType]);
            if (Math.Abs(v.Vx - 3.0f) < 0.01f && Math.Abs(v.Vy - 4.0f) < 0.01f)
            {
                match = e;
                break;
            }
        }
        Assert.NotNull(match);

        var pos = MessagePackSerializer.Deserialize<TestPosition>(match.Components[posType]);
        var vel = MessagePackSerializer.Deserialize<TestVelocity>(match.Components[velType]);
        Assert.Equal(11.0f, pos.X);
        Assert.Equal(22.0f, pos.Y);
        Assert.Equal(3.0f, vel.Vx);
        Assert.Equal(4.0f, vel.Vy);
    }

    [Fact]
    public async Task RunAsync_RegistersSystemVisibleViaQuery()
    {
        var name = $"RegQ_{Guid.NewGuid():N}"[..20];
        await using var runner = new SystemRunner(
            name,
            natsUrl: _fixture.Url,
            reads: [ComponentTypeId.Of<TestPosition>().TypeName]);

        await runner.ConnectAsync();
        using var cts = new CancellationTokenSource();
        var runTask = runner.RunAsync(cts.Token);

        await WaitUntilAsync(async () =>
        {
            var resp = await QuerySystemsAsync();
            return resp.Systems.Any(s => s.Name == name);
        }, TimeSpan.FromSeconds(5));

        var systems = await QuerySystemsAsync();
        var info = systems.Systems.FirstOrDefault(s => s.Name == name);
        Assert.NotNull(info);
        Assert.Contains(ComponentTypeId.Of<TestPosition>().TypeName, info.Reads);

        await cts.CancelAsync();
        try { await runTask; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task RunAsync_UnregistersOnCancellation()
    {
        var name = $"Unreg_{Guid.NewGuid():N}"[..20];
        await using var runner = new SystemRunner(
            name,
            natsUrl: _fixture.Url,
            reads: [ComponentTypeId.Of<TestPosition>().TypeName]);

        await runner.ConnectAsync();
        using var cts = new CancellationTokenSource();
        var runTask = runner.RunAsync(cts.Token);

        await WaitUntilAsync(async () =>
        {
            var resp = await QuerySystemsAsync();
            return resp.Systems.Any(s => s.Name == name);
        }, TimeSpan.FromSeconds(5));

        await cts.CancelAsync();
        try { await runTask; } catch (OperationCanceledException) { }
        await Task.Delay(500);

        var after = await QuerySystemsAsync();
        Assert.DoesNotContain(after.Systems, s => s.Name == name);
    }

    [Fact]
    public async Task RunAsync_ReceivesTicksAndProcessesComponents()
    {
        var posType = ComponentTypeId.Of<TestPosition>().TypeName;
        var velType = ComponentTypeId.Of<TestVelocity>().TypeName;

        // Spawn entity with both components
        await using var spawner = new SystemRunner("TickSpawn", natsUrl: _fixture.Url);
        await spawner.ConnectAsync();
        await spawner.SpawnEntityAsync(
            new TestPosition { X = 0.0f, Y = 0.0f },
            new TestVelocity { Vx = 10.0f, Vy = 5.0f });

        await WaitUntilAsync(async () =>
        {
            var resp = await QueryEntitiesAsync(new[] { posType, velType });
            return resp.Entities.Length > 0;
        }, TimeSpan.FromSeconds(5));

        // Run a system that reads velocity and writes position
        var ticksProcessed = 0;
        await using var runner = new SystemRunner(
            "TickProc",
            natsUrl: _fixture.Url,
            reads: [velType],
            writes: [posType],
            tickHandler: async (buffer, dt) =>
            {
                var velocities = buffer.GetComponents<TestVelocity>();
                var positions = buffer.GetComponents<TestPosition>();

                foreach (var (entity, vel) in velocities)
                {
                    var pos = positions.FirstOrDefault(p => p.Entity.Id == entity.Id);
                    buffer.SetComponent(entity, new TestPosition
                    {
                        X = pos.Component.X + vel.Vx * dt,
                        Y = pos.Component.Y + vel.Vy * dt
                    });
                }

                Interlocked.Increment(ref ticksProcessed);
                await Task.CompletedTask;
            });

        await runner.ConnectAsync();
        using var cts = new CancellationTokenSource();
        var runTask = runner.RunAsync(cts.Token);

        await WaitUntilAsync(async () =>
        {
            await Task.CompletedTask;
            return Volatile.Read(ref ticksProcessed) > 0;
        }, TimeSpan.FromSeconds(10));

        await cts.CancelAsync();
        try { await runTask; } catch (OperationCanceledException) { }

        Assert.True(ticksProcessed > 0, $"Expected ticks, got {ticksProcessed}");
    }
}
