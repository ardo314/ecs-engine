using Ecs.Protocol;
using Ecs.Protocol.V1;
using Engine.Core;
using Google.Protobuf;
using NATS.Client.Core;

namespace Client;

/// <summary>
/// Connects a <see cref="SystemBase"/> to the coordinator and runs its tick loop.
/// </summary>
/// <remarks>
/// Startup is ordered: register schemas, bind queries to the ids that came back, then
/// announce the system. Only after that does the coordinator have a system it can
/// schedule, and only then can a query descriptor mean anything.
///
/// Each tick is a lease. The invocation says which entities and which writable types the
/// lease covers; the result quotes the lease back. Anything late, duplicated or outside
/// the slice is refused by the world rather than silently applied.
/// </remarks>
public sealed class SystemRunner
{
    private readonly SystemBase _system;
    private readonly INatsConnection _nats;
    private readonly CoordinatorClient _coordinator;

    public SystemRunner(SystemBase system, INatsConnection nats)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(nats);

        _system = system;
        _nats = nats;
        _coordinator = new CoordinatorClient(nats, system.SystemName);
    }

    public string InstanceId { get; } = Guid.NewGuid().ToString("N");

    public string SystemName => _system.SystemName;

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        await _coordinator.WaitForCoordinatorAsync(cancellationToken);
        await _coordinator.RegisterSchemasAsync(_system.Declarations(), cancellationToken);
        _system.BindQueries(_coordinator.Bindings);

        // Subscribe before announcing, so the first invocation cannot arrive unheard.
        var invocations = await _nats.SubscribeCoreAsync<byte[]>(
            Subjects.SystemInvoke(SystemName),
            queueGroup: SystemName,
            cancellationToken: cancellationToken);

        var rejections = await _nats.SubscribeCoreAsync<byte[]>(
            Subjects.SystemRejected(InstanceId), cancellationToken: cancellationToken);

        using var announcing = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var announce = AnnounceAsync(announcing.Token);
        var reporting = ReportRejectionsAsync(rejections, cancellationToken);

        // OnAdd commands describe types and seed data. They go out of band because a
        // system with no matching entities is never invoked, so there may be no tick to
        // carry them.
        await _coordinator.SubmitAsync(_system.Commands, cancellationToken);

        var announced = false;

        try
        {
            await foreach (var message in invocations.Msgs.ReadAllAsync(cancellationToken))
            {
                if (message.Data is null) continue;

                if (!announced)
                {
                    announced = true;
                    await announcing.CancelAsync();
                    Console.WriteLine($"[{SystemName}] Registered — receiving ticks.");
                }

                await ExecuteTick(SystemInvocation.Parser.ParseFrom(message.Data), cancellationToken);
            }
        }
        finally
        {
            if (!announcing.IsCancellationRequested) await announcing.CancelAsync();
            await Suppress(announce);
            await Suppress(reporting);

            await Unregister();
            await invocations.UnsubscribeAsync();
            await rejections.UnsubscribeAsync();
            Console.WriteLine($"[{SystemName}] Shut down.");
        }
    }

    private async Task ExecuteTick(SystemInvocation invocation, CancellationToken cancellationToken)
    {
        var columns = new Dictionary<uint, ComponentColumn>(invocation.Components.Count);
        foreach (var batch in invocation.Components)
            columns[batch.TypeId] = ComponentBatchCodecs.Decode(batch);

        foreach (var query in _system.GetQueries())
            query.Populate(columns, invocation);

        _system.DeltaTime = invocation.DeltaSeconds;
        _system.TickId = invocation.Tick;

        await _system.InvokeOnUpdateAsync();

        var result = new SystemResult
        {
            Tick = invocation.Tick,
            LeaseId = invocation.LeaseId,
            InstanceId = InstanceId,
        };

        foreach (var query in _system.GetQueries())
            result.Writes.AddRange(query.FlushWrites());

        // A system may add a component of a type nobody has registered yet. Bind it
        // before the command that needs it goes anywhere.
        if (_system.Commands.HasPendingCommands)
        {
            await _coordinator.RegisterSchemasAsync(_system.Commands.Schemas, cancellationToken);
            result.Commands.AddRange(_system.Commands.Drain());
        }

        await _nats.PublishAsync(
            Subjects.SystemResult, result.ToByteArray(), cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Republishes the registration until the first invocation arrives. The coordinator
    /// holds no durable system list, so a system that starts first must keep saying so.
    /// </summary>
    private async Task AnnounceAsync(CancellationToken cancellationToken)
    {
        var registration = new SystemRegistration
        {
            Name = SystemName,
            InstanceId = InstanceId,
        };
        registration.Queries.AddRange(_system.QueryDescriptors());
        var payload = registration.ToByteArray();

        Console.WriteLine(
            $"[{SystemName}] Registering (instance {InstanceId}). " +
            $"Reads: [{string.Join(", ", _system.ReadNames())}], " +
            $"Writes: [{string.Join(", ", _system.WriteNames())}]");

        while (!cancellationToken.IsCancellationRequested)
        {
            await _nats.PublishAsync(
                Subjects.SystemRegister, payload, cancellationToken: cancellationToken);

            try { await Task.Delay(1000, cancellationToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private static async Task ReportRejectionsAsync(
        INatsSub<byte[]> subscription, CancellationToken cancellationToken)
    {
        await foreach (var message in subscription.Msgs.ReadAllAsync(cancellationToken))
        {
            if (message.Data is null) continue;

            var rejected = ResultRejected.Parser.ParseFrom(message.Data);
            Console.Error.WriteLine(
                $"[System] Tick {rejected.Tick} result refused: {rejected.Reason} — {rejected.Detail}");
        }
    }

    private async Task Unregister()
    {
        var message = new SystemUnregistration { Name = SystemName, InstanceId = InstanceId };

        try
        {
            await _nats.PublishAsync(
                Subjects.SystemUnregister,
                message.ToByteArray(),
                cancellationToken: CancellationToken.None);
        }
        catch
        {
            // Best effort: the coordinator drops unresponsive instances anyway.
        }
    }

    private static async Task Suppress(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { }
    }
}
