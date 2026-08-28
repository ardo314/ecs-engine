using NATS.Client.Core;

namespace Client;

/// <summary>
/// Entry point for the client SDK. Owns the transport connection, hands out worlds,
/// and controls process shutdown.
/// </summary>
/// <example>
/// <code>
/// await using var nats = new NatsConnection(NatsConfig.CreateOpts());
/// var ecs = new ECS(nats);
/// var world = ecs.GetWorld();
/// world.AddSystem(new MovementSystem(dep));
/// await ecs.WaitForShutdownAsync();
/// </code>
/// </example>
public sealed class ECS : IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, World> _worlds = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource _shutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ConsoleCancelEventHandler? _ctrlCHandler;

    public const string DefaultWorldName = "default";

    public ECS(INatsConnection nats)
    {
        ArgumentNullException.ThrowIfNull(nats);
        Nats = nats;
    }

    /// <summary>
    /// Transport connection used by every world created from this instance.
    /// The caller owns it — disposing the ECS does not close it.
    /// </summary>
    public INatsConnection Nats { get; }

    /// <summary>
    /// Returns the world with the given name, creating it on first access.
    /// </summary>
    public World GetWorld(string name = DefaultWorldName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        lock (_gate)
        {
            if (!_worlds.TryGetValue(name, out var world))
            {
                world = new World(this, name);
                _worlds[name] = world;
            }
            return world;
        }
    }

    /// <summary>
    /// Waits until Ctrl+C is pressed, <paramref name="cancellationToken"/> fires, or a
    /// system faults, then stops every system in every world.
    /// </summary>
    public async Task WaitForShutdownAsync(CancellationToken cancellationToken = default)
    {
        HookCtrlC();

        using var registration = cancellationToken.Register(() => _shutdown.TrySetResult());
        await _shutdown.Task;
        await ShutdownAsync();
    }

    /// <summary>
    /// Stops every system in every world.
    /// </summary>
    public async Task ShutdownAsync()
    {
        World[] worlds;
        lock (_gate)
        {
            worlds = _worlds.Values.ToArray();
        }

        await Task.WhenAll(worlds.Select(w => w.ShutdownAsync()));
    }

    public async ValueTask DisposeAsync()
    {
        UnhookCtrlC();
        _shutdown.TrySetResult();

        World[] worlds;
        lock (_gate)
        {
            worlds = _worlds.Values.ToArray();
            _worlds.Clear();
        }

        foreach (var world in worlds)
            await world.DisposeAsync();
    }

    /// <summary>
    /// Requests shutdown; releases anyone awaiting <see cref="WaitForShutdownAsync"/>.
    /// </summary>
    internal void SignalShutdown() => _shutdown.TrySetResult();

    private void HookCtrlC()
    {
        lock (_gate)
        {
            if (_ctrlCHandler is not null) return;
            _ctrlCHandler = (_, e) =>
            {
                e.Cancel = true;
                _shutdown.TrySetResult();
            };
        }

        Console.CancelKeyPress += _ctrlCHandler;
    }

    private void UnhookCtrlC()
    {
        ConsoleCancelEventHandler? handler;
        lock (_gate)
        {
            handler = _ctrlCHandler;
            _ctrlCHandler = null;
        }

        if (handler is not null)
            Console.CancelKeyPress -= handler;
    }
}
