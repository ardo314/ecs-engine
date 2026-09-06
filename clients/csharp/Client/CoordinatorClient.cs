using Ecs.Protocol;
using Ecs.Protocol.V1;
using Engine.Core;
using Google.Protobuf;
using NATS.Client.Core;

namespace Client;

/// <summary>
/// The client half of the control plane: the schema handshake and out-of-band commands.
/// </summary>
/// <remarks>
/// Nothing else in the SDK may talk to the coordinator until schemas are registered.
/// That ordering is the point — the world assigns the dense type ids, so a query, a
/// batch or a command that has not been through here has nothing meaningful to say.
/// </remarks>
internal sealed class CoordinatorClient
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    private readonly INatsConnection _nats;
    private readonly string _label;

    public CoordinatorClient(INatsConnection nats, string label)
    {
        _nats = nats;
        _label = label;
    }

    public SchemaBindings Bindings { get; } = new();

    /// <summary>
    /// Polls the coordinator until it answers, so nothing published afterwards is
    /// dropped into the void.
    /// </summary>
    public async Task WaitForCoordinatorAsync(CancellationToken cancellationToken)
    {
        var announced = false;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attempt.CancelAfter(TimeSpan.FromSeconds(2));

                await _nats.RequestAsync<byte[], byte[]>(
                    Subjects.QuerySystems,
                    new QuerySystemsRequest().ToByteArray(),
                    cancellationToken: attempt.Token);
                return;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                if (!announced)
                {
                    announced = true;
                    Console.WriteLine($"[{_label}] Waiting for coordinator...");
                }
                await Task.Delay(250, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Registers schemas the coordinator has not already bound and records the ids it
    /// assigns. A rejection is fatal: the alternative is a system quietly reading or
    /// writing something other than what it was compiled against.
    /// </summary>
    public async Task RegisterSchemasAsync(
        IEnumerable<ComponentTypeDeclaration> declarations,
        CancellationToken cancellationToken)
    {
        var request = new RegisterSchemasRequest();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var declaration in declarations)
        {
            var name = declaration.Type.LogicalName;
            if (Bindings.TryGetId(name, out _) || !seen.Add(name)) continue;
            request.Declarations.Add(declaration);
        }

        if (request.Declarations.Count == 0) return;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);

        var reply = await _nats.RequestAsync<byte[], byte[]>(
            Subjects.SchemaRegister, request.ToByteArray(), cancellationToken: timeout.Token);

        var response = RegisterSchemasResponse.Parser.ParseFrom(reply.Data ?? []);

        if (response.Rejections.Count > 0)
        {
            var detail = string.Join("; ", response.Rejections.Select(r =>
                $"{r.Type.LogicalName}: {r.Reason} ({r.Detail})"));
            throw new SchemaRegistrationException(
                $"The coordinator refused {response.Rejections.Count} schema(s) — {detail}");
        }

        foreach (var binding in response.Bindings)
            Bindings.Add(binding.Type.LogicalName, binding.TypeId);

        Console.WriteLine(
            $"[{_label}] Registered {response.Bindings.Count} schema(s): " +
            string.Join(", ", response.Bindings.Select(b => $"{b.Type.LogicalName}={b.TypeId}")));
    }

    /// <summary>
    /// Sends everything buffered in <paramref name="commands"/> out of band, registering
    /// any schema it touches first. Used for seeding and one-off edits; a system's own
    /// commands ride back inside its tick result instead.
    /// </summary>
    public async Task SubmitAsync(EntityCommandBuffer commands, CancellationToken cancellationToken)
    {
        if (!commands.HasPendingCommands) return;

        await RegisterSchemasAsync(commands.Schemas, cancellationToken);

        var batch = new CommandBatch();
        batch.Commands.AddRange(commands.Drain());

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);

        await _nats.RequestAsync<byte[], byte[]>(
            Subjects.WorldCommand, batch.ToByteArray(), cancellationToken: timeout.Token);
    }
}

public sealed class SchemaRegistrationException(string message) : Exception(message);
