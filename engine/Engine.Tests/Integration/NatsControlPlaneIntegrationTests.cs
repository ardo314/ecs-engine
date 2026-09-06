using Ecs.Protocol;
using Ecs.Protocol.V1;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using NATS.Client.Core;

namespace Engine.Tests.Integration;

/// <summary>
/// The control plane end to end over NATS: register a schema, register a system, submit
/// commands, ask the world what it knows.
/// </summary>
[Collection("NATS")]
public class NatsControlPlaneIntegrationTests(NatsFixture fixture)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static ComponentTypeDeclaration Declare(MessageDescriptor descriptor) => new()
    {
        Type = new ComponentTypeRef
        {
            LogicalName = descriptor.FullName,
            SchemaHash = SchemaHash.Of(descriptor),
        },
        FileDescriptorSet = Descriptors.FileDescriptorSetFor(descriptor),
    };

    private async Task<TResponse> RequestAsync<TResponse>(
        NatsConnection nats, string subject, IMessage request, MessageParser<TResponse> parser)
        where TResponse : IMessage<TResponse>
    {
        var reply = await nats.RequestAsync<byte[], byte[]>(
            subject, request.ToByteArray(), replyOpts: new NatsSubOpts { Timeout = Timeout });

        // An empty protobuf message is zero bytes, which NATS delivers as no payload.
        return parser.ParseFrom(reply.Data ?? []);
    }

    [Fact]
    public async Task RegisteringASchema_BindsADenseIdTheWorldRemembers()
    {
        fixture.EnsureAvailable();
        await using var nats = await fixture.ConnectAsync();

        var request = new RegisterSchemasRequest();
        request.Declarations.Add(Declare(Testing.V1.TestPosition.Descriptor));

        var response = await RequestAsync(
            nats, Subjects.SchemaRegister, request, RegisterSchemasResponse.Parser);

        Assert.Empty(response.Rejections);
        var binding = Assert.Single(response.Bindings);
        Assert.NotEqual(0u, binding.TypeId);

        Assert.True(fixture.Schemas.TryGet("testing.v1.TestPosition", out var bound));
        Assert.Equal(binding.TypeId, bound.TypeId);
    }

    [Fact]
    public async Task RegisteringASchemaTwice_ReturnsTheSameId()
    {
        fixture.EnsureAvailable();
        await using var nats = await fixture.ConnectAsync();

        var request = new RegisterSchemasRequest();
        request.Declarations.Add(Declare(Testing.V1.TestVelocity.Descriptor));

        var first = await RequestAsync(nats, Subjects.SchemaRegister, request, RegisterSchemasResponse.Parser);
        var second = await RequestAsync(nats, Subjects.SchemaRegister, request, RegisterSchemasResponse.Parser);

        Assert.Equal(first.Bindings[0].TypeId, second.Bindings[0].TypeId);
    }

    [Fact]
    public async Task AMisdeclaredSchemaIsRefusedRatherThanBound()
    {
        fixture.EnsureAvailable();
        await using var nats = await fixture.ConnectAsync();

        var declaration = Declare(Testing.V1.TestDisabled.Descriptor);
        declaration.Type.SchemaHash = 0xfeedface;

        var request = new RegisterSchemasRequest();
        request.Declarations.Add(declaration);

        var response = await RequestAsync(
            nats, Subjects.SchemaRegister, request, RegisterSchemasResponse.Parser);

        Assert.Empty(response.Bindings);
        Assert.Equal(
            SchemaRejectionReason.HashNotDerivedFromDescriptor,
            Assert.Single(response.Rejections).Reason);
    }

    [Fact]
    public async Task RegisteringASystem_MakesItVisibleToTheScheduler()
    {
        fixture.EnsureAvailable();
        await using var nats = await fixture.ConnectAsync();

        var registration = new SystemRegistration
        {
            Name = $"Probe{Guid.NewGuid():N}",
            InstanceId = Guid.NewGuid().ToString("N"),
            Queries =
            {
                new QueryDescriptor
                {
                    Required =
                    {
                        new Ecs.Protocol.V1.ComponentAccess { TypeId = 17, Access = Access.Read },
                        new Ecs.Protocol.V1.ComponentAccess { TypeId = 22, Access = Access.Write },
                    },
                },
            },
        };

        await nats.PublishAsync(Subjects.SystemRegister, registration.ToByteArray());
        await WaitUntil(() => fixture.Registry.SystemNames().Contains(registration.Name));

        var response = await RequestAsync(
            nats, Subjects.QuerySystems, new QuerySystemsRequest(), QuerySystemsResponse.Parser);

        var system = Assert.Single(response.Systems, s => s.Name == registration.Name);
        Assert.Contains(17u, system.Reads);
        Assert.Contains(22u, system.Writes);
        Assert.Contains(response.Stages, stage => stage.Systems.Contains(registration.Name));
    }

    [Fact]
    public async Task UnregisteringASystem_RemovesIt()
    {
        fixture.EnsureAvailable();
        await using var nats = await fixture.ConnectAsync();

        var name = $"Probe{Guid.NewGuid():N}";
        var instance = Guid.NewGuid().ToString("N");

        await nats.PublishAsync(Subjects.SystemRegister,
            new SystemRegistration { Name = name, InstanceId = instance }.ToByteArray());
        await WaitUntil(() => fixture.Registry.SystemNames().Contains(name));

        await nats.PublishAsync(Subjects.SystemUnregister,
            new SystemUnregistration { Name = name, InstanceId = instance }.ToByteArray());
        await WaitUntil(() => !fixture.Registry.SystemNames().Contains(name));
    }

    [Fact]
    public async Task CommandsAreBufferedForTheSynchronisationPointRatherThanAppliedOnArrival()
    {
        fixture.EnsureAvailable();
        await using var nats = await fixture.ConnectAsync();

        var schemas = new RegisterSchemasRequest();
        schemas.Declarations.Add(Declare(Testing.V1.TestPosition.Descriptor));
        var bound = await RequestAsync(
            nats, Subjects.SchemaRegister, schemas, RegisterSchemasResponse.Parser);
        var typeId = bound.Bindings[0].TypeId;

        var batch = new CommandBatch
        {
            Commands =
            {
                new StructuralCommand
                {
                    Spawn = new SpawnEntity
                    {
                        Components =
                        {
                            new ComponentValue
                            {
                                Type = bound.Bindings[0].Type,
                                Payload = new Testing.V1.TestPosition { X = 4.25f }.ToByteString(),
                            },
                        },
                    },
                },
            },
        };

        var before = fixture.World.EntityCount;
        await RequestAsync(nats, Subjects.WorldCommand, batch, CommandBatchAck.Parser);

        // Nothing has changed yet: the world only mutates at its synchronisation point.
        Assert.Equal(before, fixture.World.EntityCount);

        var applier = new Engine.Coordinator.CommandApplier(fixture.World, fixture.Schemas);
        var spawned = applier.Apply(fixture.Handlers.DrainCommands());

        Assert.Contains(spawned, fixture.World.IsAlive);
        Assert.Contains(spawned, id =>
            fixture.World.GetComponent(id, typeId) is { } payload &&
            Math.Abs(Testing.V1.TestPosition.Parser.ParseFrom(payload).X - 4.25f) < 0.001f);
    }

    [Fact]
    public async Task QueryEntities_ReturnsPayloadsAndTheSchemasToDecodeThem()
    {
        fixture.EnsureAvailable();
        await using var nats = await fixture.ConnectAsync();

        var schemas = new RegisterSchemasRequest();
        schemas.Declarations.Add(Declare(Testing.V1.TestPosition.Descriptor));
        var bound = await RequestAsync(
            nats, Subjects.SchemaRegister, schemas, RegisterSchemasResponse.Parser);
        var typeId = bound.Bindings[0].TypeId;

        var entity = fixture.World.AllocateEntity();
        fixture.World.SetComponent(
            entity, typeId, new Testing.V1.TestPosition { X = 9f }.ToByteArray());

        var response = await RequestAsync(
            nats, Subjects.QueryEntities, new QueryEntitiesRequest(), QueryEntitiesResponse.Parser);

        var snapshot = Assert.Single(response.Entities, e => e.Entity == entity);
        var binding = Assert.Single(snapshot.Components, c => c.TypeId == typeId);
        Assert.Equal(9f, Testing.V1.TestPosition.Parser.ParseFrom(binding.Payload).X);

        // An observer must be able to decode this without having compiled the type.
        var info = Assert.Single(response.ComponentTypes, t => t.TypeId == typeId);
        Assert.NotEmpty(info.FileDescriptorSet);
    }

    [Fact]
    public async Task QueryEntities_HonoursAComponentFilter()
    {
        fixture.EnsureAvailable();
        await using var nats = await fixture.ConnectAsync();

        var schemas = new RegisterSchemasRequest();
        schemas.Declarations.Add(Declare(Testing.V1.TestDisabled.Descriptor));
        var bound = await RequestAsync(
            nats, Subjects.SchemaRegister, schemas, RegisterSchemasResponse.Parser);

        var entity = fixture.World.AllocateEntity();
        fixture.World.SetComponent(entity, bound.Bindings[0].TypeId, []);

        var request = new QueryEntitiesRequest
        {
            Filter = new EntityFilter { AllOf = { "testing.v1.TestDisabled" } },
        };
        var response = await RequestAsync(
            nats, Subjects.QueryEntities, request, QueryEntitiesResponse.Parser);

        Assert.Contains(response.Entities, e => e.Entity == entity);
        Assert.All(response.Entities, e =>
            Assert.Contains(e.Components, c => c.TypeId == bound.Bindings[0].TypeId));
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(50);
        }

        Assert.Fail("Condition was not met within the timeout.");
    }
}
