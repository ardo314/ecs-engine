using Ecs.Protocol;
using Ecs.Protocol.V1;
using Engine.Coordinator;
using Engine.Core;
using Google.Protobuf;
using NATS.Client.Core;
using Testing.V1;

namespace Client.Tests.Integration;

internal sealed class ReadOnlySystem : SystemBase
{
    private readonly EntityQuery _query = null!;

    public ReadOnlySystem() => _query = NewQuery().With(Query.Read<TestPosition>());

    public int Ticks { get; private set; }
    public int LastSeen { get; private set; }

    protected override Task OnUpdateAsync()
    {
        Ticks++;
        LastSeen = _query.Entities.Count;
        return Task.CompletedTask;
    }
}

internal sealed class IntegratingSystem : SystemBase
{
    private readonly EntityQuery _query;

    public IntegratingSystem() =>
        _query = NewQuery().With(Query.Write<TestPosition>()).With(Query.Read<TestVelocity>());

    public int Ticks { get; private set; }

    protected override Task OnUpdateAsync()
    {
        Ticks++;

        foreach (var (entity, position, velocity) in _query.Each<TestPosition, TestVelocity>())
        {
            _query.Set(entity, new TestPosition
            {
                X = position.X + (velocity.Vx * DeltaTime),
                Y = position.Y + (velocity.Vy * DeltaTime),
            });
        }

        return Task.CompletedTask;
    }
}

internal sealed class SmugglerSystem : SystemBase
{
    private readonly EntityQuery _query;

    public SmugglerSystem() => _query = NewQuery().With(Query.Read<TestPosition>());

    protected override Task OnUpdateAsync()
    {
        // Declared read-only, so the SDK refuses before anything reaches the wire.
        Failed = Record.Exception(
            () => _query.Set(_query.Entities.FirstOrDefault(), new TestPosition { X = 1f }));
        return Task.CompletedTask;
    }

    public Exception? Failed { get; private set; }
}

/// <summary>
/// The redesigned loop end to end: schema handshake, registration, lease-scoped
/// invocation, validated result.
/// </summary>
[Collection("NATS")]
public class SystemRunnerIntegrationTests(NatsClientFixture fixture) : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private NatsConnection _coordinatorNats = null!;
    private CancellationTokenSource _cts = null!;
    private Task _coordinator = Task.CompletedTask;

    private SchemaRegistry Schemas { get; } = new();
    private WorldState World { get; set; } = null!;

    public async Task InitializeAsync()
    {
        if (!fixture.Available) return;

        _coordinatorNats = new NatsConnection(new NatsOpts { Url = fixture.Url });
        await _coordinatorNats.ConnectAsync();

        World = new WorldState(Schemas);
        var systems = new SystemRegistry();
        var watches = new WatchManager();
        var handlers = new NatsHandlers(_coordinatorNats, Schemas, systems, World, watches);

        _cts = new CancellationTokenSource();
        _ = Task.Run(() => handlers.StartAsync(_cts.Token));
        await handlers.Ready;

        var tickLoop = new TickLoop(_coordinatorNats, Schemas, systems, World, watches, handlers, 50);
        _coordinator = Task.Run(() => tickLoop.RunAsync(_cts.Token));

        await Task.Delay(200);
    }

    public async Task DisposeAsync()
    {
        if (!fixture.Available) return;

        await _cts.CancelAsync();
        try { await _coordinator; } catch (OperationCanceledException) { }
        await _coordinatorNats.DisposeAsync();
        _cts.Dispose();
    }

    private async Task<ECS> ConnectAsync()
    {
        var nats = new NatsConnection(new NatsOpts { Url = fixture.Url });
        await nats.ConnectAsync();
        return new ECS(nats);
    }

    private async Task WaitUntil(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(25);
        }

        Assert.Fail(because);
    }

    private uint TypeId<T>() where T : IMessage<T>, new()
    {
        Assert.True(Schemas.TryGet(ComponentType<T>.Name, out var type), $"{ComponentType<T>.Name} unbound");
        return type.TypeId;
    }

    [Fact]
    public void Constructor_RejectsAMissingConnection()
    {
        Assert.Throws<ArgumentNullException>(() => new SystemRunner(new ReadOnlySystem(), null!));
    }

    [Fact]
    public async Task SeedingAWorld_RegistersSchemasAndCreatesEntities()
    {
        fixture.EnsureAvailable();
        await using var ecs = await ConnectAsync();
        var world = ecs.GetWorld(Guid.NewGuid().ToString("N"));

        world.Commands.CreateEntity(new TestPosition { X = 1f }, new TestVelocity { Vx = 2f });
        await world.FlushAsync();

        await WaitUntil(
            () => Schemas.TryGet(ComponentType<TestPosition>.Name, out _),
            "the coordinator never bound testing.v1.TestPosition");

        var position = TypeId<TestPosition>();
        await WaitUntil(
            () => World.AllEntities.Any(e => World.GetComponent(e, position) is { } p &&
                                             Math.Abs(TestPosition.Parser.ParseFrom(p).X - 1f) < 0.001f),
            "the seeded entity never reached the world");
    }

    [Fact]
    public async Task ASystemIsInvokedAndItsWritesLandInTheWorld()
    {
        fixture.EnsureAvailable();
        await using var ecs = await ConnectAsync();
        var world = ecs.GetWorld(Guid.NewGuid().ToString("N"));

        world.Commands.CreateEntity(new TestPosition(), new TestVelocity { Vx = 10f });
        await world.FlushAsync();

        var system = new IntegratingSystem();
        world.AddSystem(system);

        await WaitUntil(() => system.Ticks > 3, "the system was never invoked");

        var position = TypeId<TestPosition>();
        await WaitUntil(
            () => World.AllEntities.Any(e => World.GetComponent(e, position) is { } p &&
                                             TestPosition.Parser.ParseFrom(p).X > 0f),
            "the system's writes never reached the world");

        await world.ShutdownAsync();
    }

    [Fact]
    public async Task ARunningSystemIsVisibleToTheScheduler_AndDisappearsWhenItStops()
    {
        fixture.EnsureAvailable();
        await using var ecs = await ConnectAsync();
        var world = ecs.GetWorld(Guid.NewGuid().ToString("N"));

        // A system is only invoked when something matches its query.
        world.Commands.CreateEntity(new TestPosition());
        await world.FlushAsync();

        var system = new ReadOnlySystem();
        world.AddSystem(system);

        await WaitUntil(() => system.Ticks > 0, "the system never received an invocation");

        await using var probe = new NatsConnection(new NatsOpts { Url = fixture.Url });
        await probe.ConnectAsync();

        var response = await QuerySystems(probe);
        var info = Assert.Single(response.Systems, s => s.Name == "ReadOnly");
        Assert.Contains(TypeId<TestPosition>(), info.Reads);

        await world.RemoveSystemAsync(system);

        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            if ((await QuerySystems(probe)).Systems.All(s => s.Name != "ReadOnly")) return;
            await Task.Delay(50);
        }

        Assert.Fail("the system never unregistered");
    }

    private static async Task<QuerySystemsResponse> QuerySystems(NatsConnection nats)
    {
        var reply = await nats.RequestAsync<byte[], byte[]>(
            Subjects.QuerySystems,
            new QuerySystemsRequest().ToByteArray(),
            replyOpts: new NatsSubOpts { Timeout = Timeout });

        // With no systems registered the response is an empty message, which is zero
        // bytes, which NATS delivers as no payload at all.
        return QuerySystemsResponse.Parser.ParseFrom(reply.Data ?? []);
    }

    [Fact]
    public async Task WritingATypeTheQueryDeclaredReadOnlyIsRefusedByTheSdk()
    {
        fixture.EnsureAvailable();
        await using var ecs = await ConnectAsync();
        var world = ecs.GetWorld(Guid.NewGuid().ToString("N"));

        world.Commands.CreateEntity(new TestPosition());
        await world.FlushAsync();

        var system = new SmugglerSystem();
        world.AddSystem(system);

        await WaitUntil(() => system.Failed is not null, "the system was never invoked");

        Assert.IsType<InvalidOperationException>(system.Failed);
        await world.ShutdownAsync();
    }

    [Fact]
    public async Task InstanceIdsAreUnique()
    {
        await using var nats = new NatsConnection(new NatsOpts { Url = fixture.Url });

        var first = new SystemRunner(new ReadOnlySystem(), nats);
        var second = new SystemRunner(new ReadOnlySystem(), nats);

        Assert.NotEqual(first.InstanceId, second.InstanceId);
    }
}
