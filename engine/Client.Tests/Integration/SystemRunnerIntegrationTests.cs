using Client;
using Engine.Core;
using Engine.Core.Messages;
using MessagePack;
using NATS.Client.Core;

using Client.Tests.Unit;

namespace Client.Tests.Integration;

// ── Test system definitions ───────────────────────────────────

public class EmptySystem : SystemBase
{
    protected override Task OnUpdateAsync() => Task.CompletedTask;
}

public class SpawnSystem : SystemBase
{
    private readonly IComponent[] _components;
    public SpawnSystem(params IComponent[] components) { _components = components; }
    protected override void OnCreate()
    {
        Commands.CreateEntity(_components);
    }
    protected override Task OnUpdateAsync() => Task.CompletedTask;
}

public class ReadPositionSystem : SystemBase
{
    private EntityQuery _q = null!;

    protected override void OnCreate()
    {
        _q = NewQuery()
            .With(Query.ReadOnly<TestPosition>());
    }

    protected override Task OnUpdateAsync() => Task.CompletedTask;
}

public class TickProcessorSystem : SystemBase
{
    private EntityQuery _q = null!;
    public int TicksProcessed;

    protected override void OnCreate()
    {
        _q = NewQuery()
            .With(Query.ReadWrite<TestPosition>())
            .With(Query.ReadOnly<TestVelocity>());
    }

    protected override Task OnUpdateAsync()
    {
        foreach (var entity in _q.Entities)
        {
            var pos = _q.Get<TestPosition>(entity);
            var vel = _q.Get<TestVelocity>(entity);
            _q.Set(entity, new TestPosition(
                pos.X + vel.Vx * DeltaTime,
                pos.Y + vel.Vy * DeltaTime));
        }
        Interlocked.Increment(ref TicksProcessed);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Integration tests for SystemRunner against a running engine coordinator.
/// Start the engine first: docker compose up nats engine -d
/// All verification is done via NATS query APIs — no direct engine references.
/// </summary>
[Collection("NATS")]
[Trait("Category", "Integration")]
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
        var system = new EmptySystem();
        system.InvokeOnCreate();
        await using var runner = new SystemRunner(system, _fixture.Url);
        await runner.ConnectAsync();
    }

    [Fact]
    public async Task RunAsync_ThrowsBeforeConnect()
    {
        var system = new EmptySystem();
        system.InvokeOnCreate();
        await using var runner = new SystemRunner(system, _fixture.Url);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.RunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task InstanceId_IsUnique()
    {
        var s1 = new EmptySystem();
        var s2 = new EmptySystem();
        s1.InvokeOnCreate();
        s2.InvokeOnCreate();
        await using var r1 = new SystemRunner(s1, _fixture.Url);
        await using var r2 = new SystemRunner(s2, _fixture.Url);
        Assert.NotEqual(r1.InstanceId, r2.InstanceId);
        Assert.NotEmpty(r1.InstanceId);
    }

    [Fact]
    public async Task SpawnEntityViaEcb_EntityAppearsViaQuery()
    {
        var system = new SpawnSystem(new TestPosition { X = 77.0f, Y = 88.0f });
        system.InvokeOnCreate();
        await using var runner = new SystemRunner(system, _fixture.Url);
        await runner.ConnectAsync();

        // ECB is flushed on connect in RunAsync, but we need to manually flush here since we don't call RunAsync
        // Instead, the ECB gets flushed when RunAsync starts. Let's use a short-lived RunAsync.
        var typeName = ComponentTypeId.Of<TestPosition>().TypeName;

        // Run for a brief time to let the ECB flush
        using var cts = new CancellationTokenSource();
        var runTask = runner.RunAsync(cts.Token);
        await Task.Delay(500);
        await cts.CancelAsync();
        try { await runTask; } catch (OperationCanceledException) { }

        await WaitUntilAsync(async () =>
        {
            var resp = await QueryEntitiesAsync(new[] { typeName });
            return resp.Entities.Any(e => e.Components.ContainsKey(typeName));
        }, TimeSpan.FromSeconds(5));

        var entities = await QueryEntitiesAsync(new[] { typeName });
        Assert.NotEmpty(entities.Entities);
    }

    [Fact]
    public async Task RunAsync_RegistersSystemVisibleViaQuery()
    {
        var system = new ReadPositionSystem();
        var name = system.SystemName;
        system.InvokeOnCreate();
        await using var runner = new SystemRunner(system, _fixture.Url);

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
        var system = new ReadPositionSystem();
        var name = system.SystemName;
        system.InvokeOnCreate();
        await using var runner = new SystemRunner(system, _fixture.Url);

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

        // Spawn entity with both components via a separate system
        var spawner = new SpawnSystem(
            new TestPosition { X = 0.0f, Y = 0.0f },
            new TestVelocity { Vx = 10.0f, Vy = 5.0f });
        spawner.InvokeOnCreate();
        await using var spawnRunner = new SystemRunner(spawner, _fixture.Url);
        await spawnRunner.ConnectAsync();
        // Run briefly to flush ECB
        using var spawnCts = new CancellationTokenSource();
        var spawnTask = spawnRunner.RunAsync(spawnCts.Token);
        await Task.Delay(500);
        await spawnCts.CancelAsync();
        try { await spawnTask; } catch (OperationCanceledException) { }

        await WaitUntilAsync(async () =>
        {
            var resp = await QueryEntitiesAsync(new[] { posType, velType });
            return resp.Entities.Length > 0;
        }, TimeSpan.FromSeconds(5));

        // Run a system that reads velocity and writes position
        var system = new TickProcessorSystem();
        system.InvokeOnCreate();
        await using var runner = new SystemRunner(system, _fixture.Url);
        await runner.ConnectAsync();
        using var cts = new CancellationTokenSource();
        var runTask = runner.RunAsync(cts.Token);

        await WaitUntilAsync(async () =>
        {
            await Task.CompletedTask;
            return Volatile.Read(ref system.TicksProcessed) > 0;
        }, TimeSpan.FromSeconds(10));

        await cts.CancelAsync();
        try { await runTask; } catch (OperationCanceledException) { }

        Assert.True(system.TicksProcessed > 0, $"Expected ticks, got {system.TicksProcessed}");
    }

    [Fact]
    public async Task RunAsync_MutationsAppliedToWorldState()
    {
        var posType = ComponentTypeId.Of<TestPosition>().TypeName;
        var velType = ComponentTypeId.Of<TestVelocity>().TypeName;

        // Spawn entity with known initial position and velocity
        var spawner = new SpawnSystem(
            new TestPosition { X = 0.0f, Y = 0.0f },
            new TestVelocity { Vx = 10.0f, Vy = 5.0f });
        spawner.InvokeOnCreate();
        await using var spawnRunner = new SystemRunner(spawner, _fixture.Url);
        await spawnRunner.ConnectAsync();
        using var spawnCts = new CancellationTokenSource();
        var spawnTask = spawnRunner.RunAsync(spawnCts.Token);
        await Task.Delay(500);
        await spawnCts.CancelAsync();
        try { await spawnTask; } catch (OperationCanceledException) { }

        await WaitUntilAsync(async () =>
        {
            var resp = await QueryEntitiesAsync(new[] { posType, velType });
            return resp.Entities.Length > 0;
        }, TimeSpan.FromSeconds(5));

        // Run a system that applies velocity to position
        var system = new TickProcessorSystem();
        system.InvokeOnCreate();
        await using var runner = new SystemRunner(system, _fixture.Url);
        await runner.ConnectAsync();
        using var cts = new CancellationTokenSource();
        var runTask = runner.RunAsync(cts.Token);

        // Wait for several ticks so position accumulates
        await WaitUntilAsync(async () =>
        {
            await Task.CompletedTask;
            return Volatile.Read(ref system.TicksProcessed) >= 5;
        }, TimeSpan.FromSeconds(10));

        await cts.CancelAsync();
        try { await runTask; } catch (OperationCanceledException) { }

        // Query the coordinator and verify position actually changed
        var entities = await QueryEntitiesAsync(new[] { posType, velType });
        Assert.NotEmpty(entities.Entities);

        // Find our entity by matching velocity
        EntitySnapshot? match = null;
        foreach (var e in entities.Entities)
        {
            if (!e.Components.ContainsKey(velType)) continue;
            var v = MessagePackSerializer.Deserialize<TestVelocity>(e.Components[velType]);
            if (Math.Abs(v.Vx - 10.0f) < 0.01f && Math.Abs(v.Vy - 5.0f) < 0.01f)
            {
                match = e;
                break;
            }
        }
        Assert.NotNull(match);

        var finalPos = MessagePackSerializer.Deserialize<TestPosition>(match.Components[posType]);
        Assert.True(finalPos.X > 0.0f,
            $"Expected Position.X > 0 after system ticks, got {finalPos.X}");
        Assert.True(finalPos.Y > 0.0f,
            $"Expected Position.Y > 0 after system ticks, got {finalPos.Y}");
    }
}
