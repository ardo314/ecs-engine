namespace Client;

/// <summary>
/// Hosts the systems of a world. Add a constructed system to start it,
/// remove it to shut it down. Obtain one via <see cref="ECS.GetWorld"/>.
/// </summary>
/// <example>
/// <code>
/// var movement = new MovementSystem(dep);
/// world.AddSystem(movement);
/// await ecs.WaitForShutdownAsync();
/// world.RemoveSystem(movement);
/// </code>
/// </example>
public sealed class World : IAsyncDisposable
{
    private sealed record Entry(SystemRunner Runner, CancellationTokenSource Cts, Task Task);

    private readonly ECS _ecs;
    private readonly Lock _gate = new();
    private readonly Dictionary<SystemBase, Entry> _systems = new();

    internal World(ECS ecs, string name)
    {
        _ecs = ecs;
        Name = name;
    }

    public string Name { get; }

    public string NatsUrl => _ecs.NatsUrl;

    /// <summary>
    /// Runs <paramref name="system"/>: invokes OnAdd, connects to the transport,
    /// and starts its tick loop in the background.
    /// </summary>
    public void AddSystem(SystemBase system)
    {
        ArgumentNullException.ThrowIfNull(system);

        var cts = new CancellationTokenSource();
        var runner = new SystemRunner(system, NatsUrl);

        lock (_gate)
        {
            if (_systems.ContainsKey(system))
                throw new InvalidOperationException($"System '{system.SystemName}' has already been added.");

            // OnAdd runs here so authoring errors surface from AddSystem itself.
            system.InvokeOnAdd();
            _systems[system] = new Entry(runner, cts, RunSystemAsync(system, runner, cts.Token));
        }
    }

    /// <summary>
    /// Stops <paramref name="system"/> without waiting for it to finish shutting down.
    /// </summary>
    public void RemoveSystem(SystemBase system)
    {
        _ = RemoveSystemAsync(system).ContinueWith(
            t => Console.Error.WriteLine($"[{system.SystemName}] Shutdown error: {t.Exception}"),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    /// <summary>
    /// Stops <paramref name="system"/> and waits until it has unregistered and disposed.
    /// </summary>
    public async Task RemoveSystemAsync(SystemBase system)
    {
        ArgumentNullException.ThrowIfNull(system);

        Entry? entry;
        lock (_gate)
        {
            if (!_systems.Remove(system, out entry))
                return;
        }

        await StopAsync(entry);
    }

    /// <summary>
    /// Stops every system currently added to this world.
    /// </summary>
    public async Task ShutdownAsync()
    {
        Entry[] entries;
        lock (_gate)
        {
            entries = _systems.Values.ToArray();
            _systems.Clear();
        }

        await Task.WhenAll(entries.Select(StopAsync));
    }

    public ValueTask DisposeAsync() => new(ShutdownAsync());

    private static async Task StopAsync(Entry entry)
    {
        try
        {
            await entry.Cts.CancelAsync();
            await entry.Task;
        }
        catch (OperationCanceledException) { /* graceful shutdown */ }
        finally
        {
            entry.Cts.Dispose();
        }
    }

    private async Task RunSystemAsync(SystemBase system, SystemRunner runner, CancellationToken ct)
    {
        try
        {
            await runner.ConnectAsync(ct);
            Console.WriteLine($"[{system.SystemName}] Starting...");
            await runner.RunAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* graceful shutdown */ }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[{system.SystemName}] Faulted: {ex}");
            _ecs.SignalShutdown();
            throw;
        }
        finally
        {
            system.InvokeOnRemove();
            await runner.DisposeAsync();
        }
    }
}
