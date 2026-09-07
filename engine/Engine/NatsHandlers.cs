using System.Threading.Channels;
using Ecs.Protocol.V1;
using Google.Protobuf;
using NATS.Client.Core;

namespace Engine.Coordinator;

/// <summary>
/// Owns every coordinator subscription except the tick loop's own publishing.
/// </summary>
/// <remarks>
/// Results arrive on a single long-lived subscription and are handed to the tick loop
/// through a channel rather than by subscribing and unsubscribing per stage. A result
/// that misses its stage is then still received and explicitly refused, instead of
/// vanishing into an unsubscribed subject.
/// </remarks>
public sealed class NatsHandlers
{
    private readonly NatsConnection _nats;
    private readonly SchemaRegistry _schemas;
    private readonly SystemRegistry _systems;
    private readonly WorldState _world;
    private readonly WatchManager _watches;

    private readonly Channel<SystemResult> _results =
        Channel.CreateUnbounded<SystemResult>(new UnboundedChannelOptions { SingleReader = true });

    private readonly Queue<StructuralCommand> _pending = new();
    private readonly Lock _pendingGate = new();

    private readonly TaskCompletionSource _ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public NatsHandlers(
        NatsConnection nats,
        SchemaRegistry schemas,
        SystemRegistry systems,
        WorldState world,
        WatchManager watches)
    {
        _nats = nats;
        _schemas = schemas;
        _systems = systems;
        _world = world;
        _watches = watches;
    }

    /// <summary>Completes once every subscription is live.</summary>
    public Task Ready => _ready.Task;

    /// <summary>Results returned by systems, drained by the tick loop.</summary>
    public ChannelReader<SystemResult> Results => _results.Reader;

    /// <summary>
    /// Takes everything buffered since the last call. Commands are applied at the tick's
    /// synchronisation point, never as they arrive.
    /// </summary>
    public List<StructuralCommand> DrainCommands()
    {
        lock (_pendingGate)
        {
            var drained = new List<StructuralCommand>(_pending);
            _pending.Clear();
            return drained;
        }
    }

    public void EnqueueCommands(IEnumerable<StructuralCommand> commands)
    {
        lock (_pendingGate)
        {
            foreach (var command in commands) _pending.Enqueue(command);
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var subscriptions = new[]
        {
            Reply<RegisterSchemasRequest>(Subjects.SchemaRegister, RegisterSchemasRequest.Parser,
                request => RegisterSchemas(request), cancellationToken),

            Listen(Subjects.SystemRegister, SystemRegistration.Parser, registration =>
            {
                _systems.Register(registration);
                _watches.NotifySystemsChanged();
            }, cancellationToken),

            Listen(Subjects.SystemUnregister, SystemUnregistration.Parser, unregistration =>
            {
                _systems.Unregister(unregistration);
                _watches.NotifySystemsChanged();
            }, cancellationToken),

            Listen(Subjects.SystemResult, SystemResult.Parser,
                result => _results.Writer.TryWrite(result), cancellationToken),

            Reply<CommandBatch>(Subjects.WorldCommand, CommandBatch.Parser, batch =>
            {
                EnqueueCommands(batch.Commands);
                return new CommandBatchAck();
            }, cancellationToken),

            Reply<QuerySystemsRequest>(Subjects.QuerySystems, QuerySystemsRequest.Parser,
                _ => BuildSystemsResponse(), cancellationToken),

            Reply<QueryEntitiesRequest>(Subjects.QueryEntities, QueryEntitiesRequest.Parser,
                request => BuildEntitiesResponse(request.Filter, includeTypes: true), cancellationToken),

            Reply<WatchRequest>(Subjects.WatchSubscribe, WatchRequest.Parser,
                request => _watches.Register(request), cancellationToken),

            Listen(Subjects.WatchCancel, WatchCancel.Parser,
                cancel => _watches.Cancel(cancel.WatchId), cancellationToken),
        };

        _ready.TrySetResult();
        await Task.WhenAll(subscriptions);
    }

    private RegisterSchemasResponse RegisterSchemas(RegisterSchemasRequest request)
    {
        var response = _schemas.Register(request);

        // A type's declared description is replayed as ordinary AddComponent commands on
        // its type entity. The engine never interprets them — that is what lets a domain
        // attach an open set of contracts (a `Setting` marker, a `Category`, ...) to a
        // component type without the engine knowing what any of them mean. The type
        // entity itself is created by the tick loop's synchronisation point.
        foreach (var declaration in request.Declarations)
        {
            if (declaration.Type is null || declaration.Description.Count == 0) continue;

            EnqueueCommands(declaration.Description.Select(value => new StructuralCommand
            {
                Add = new AddComponent
                {
                    Target = new CommandTarget { ComponentType = declaration.Type.LogicalName },
                    Component = value,
                },
            }));
        }

        return response;
    }

    public QuerySystemsResponse BuildSystemsResponse()
    {
        var response = new QuerySystemsResponse();

        foreach (var system in _systems.UniqueSystems())
        {
            var info = new SystemInfo
            {
                Name = system.Name,
                InstanceId = system.InstanceId,
            };
            info.Queries.AddRange(system.Queries);
            info.Reads.AddRange(SystemRegistry.ReadsOf(system));
            info.Writes.AddRange(SystemRegistry.WritesOf(system));
            response.Systems.Add(info);
        }

        var tags = _world.ResolveTags(_systems.UniqueSystems().SelectMany(SystemRegistry.TagsOf));
        foreach (var stage in _systems.ComputeStages(tags))
        {
            var encoded = new Stage();
            encoded.Systems.AddRange(stage.Select(s => s.Name));
            response.Stages.Add(encoded);
        }

        return response;
    }

    public QueryEntitiesResponse BuildEntitiesResponse(EntityFilter? filter, bool includeTypes)
    {
        var response = new QueryEntitiesResponse();

        foreach (var entityId in _world.Filter(filter))
        {
            var snapshot = new EntitySnapshot { Entity = entityId };
            foreach (var (typeId, payload) in _world.ComponentsOf(entityId))
            {
                snapshot.Components.Add(new ComponentBinding
                {
                    TypeId = typeId,
                    Payload = ByteString.CopyFrom(payload),
                });
            }
            response.Entities.Add(snapshot);
        }

        if (includeTypes)
            response.ComponentTypes.AddRange(DescribeTypes());

        return response;
    }

    /// <summary>
    /// The whole schema registry, as an observer needs it: dense id, exact identity and
    /// the descriptors to decode payloads it was never compiled against.
    /// </summary>
    public List<ComponentTypeInfo> DescribeTypes() =>
        [.. _schemas.All().Select(type => type.ToInfo(_world.FindTypeEntity(type.TypeId) ?? 0))];

    // ── Subscription plumbing ───────────────────────────────────

    private async Task Listen<T>(
        string subject,
        MessageParser<T> parser,
        Action<T> handle,
        CancellationToken cancellationToken)
        where T : IMessage<T>
    {
        await foreach (var message in _nats.SubscribeAsync<byte[]>(
            subject, cancellationToken: cancellationToken))
        {
            if (message.Data is null) continue;

            try
            {
                handle(parser.ParseFrom(message.Data));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Nats] {subject}: {ex.Message}");
            }
        }
    }

    private async Task Reply<T>(
        string subject,
        MessageParser<T> parser,
        Func<T, IMessage> handle,
        CancellationToken cancellationToken)
        where T : IMessage<T>
    {
        await foreach (var message in _nats.SubscribeAsync<byte[]>(
            subject, cancellationToken: cancellationToken))
        {
            try
            {
                // An empty request body is a valid "no arguments" call.
                var request = parser.ParseFrom(message.Data ?? []);
                await message.ReplyAsync(handle(request).ToByteArray(),
                    cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Nats] {subject}: {ex.Message}");
            }
        }
    }
}
