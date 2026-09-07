using Ecs.Protocol;
using Ecs.Protocol.V1;
using Engine.Coordinator;
using Google.Protobuf;
using NATS.Client.Core;

namespace Engine.Tests.Integration;

[Collection("NATS")]
public class NatsWatchIntegrationTests(NatsFixture fixture)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private async Task<WatchResponse> Subscribe(NatsConnection nats, WatchRequest request)
    {
        var reply = await nats.RequestAsync<byte[], byte[]>(
            Subjects.WatchSubscribe,
            request.ToByteArray(),
            replyOpts: new NatsSubOpts { Timeout = Timeout });

        return WatchResponse.Parser.ParseFrom(reply.Data!);
    }

    [Fact]
    public async Task Subscribing_AnswersWithASubjectAndRegistersTheWatch()
    {
        fixture.EnsureAvailable();
        await using var nats = await fixture.ConnectAsync();

        var watchId = Guid.NewGuid().ToString("N");
        var response = await Subscribe(nats, new WatchRequest
        {
            WatchId = watchId,
            IncludeSystems = true,
            IncludeEntities = true,
        });

        Assert.Equal(watchId, response.WatchId);
        Assert.Equal($"engine.world.watch.data.{watchId}", response.DataSubject);
        Assert.Contains(fixture.WatchManager.ActiveWatches(), w => w.WatchId == watchId);
    }

    [Fact]
    public async Task Cancelling_RemovesTheWatch()
    {
        fixture.EnsureAvailable();
        await using var nats = await fixture.ConnectAsync();

        var watchId = Guid.NewGuid().ToString("N");
        await Subscribe(nats, new WatchRequest { WatchId = watchId, IncludeEntities = true });

        await nats.PublishAsync(
            Subjects.WatchCancel, new WatchCancel { WatchId = watchId }.ToByteArray());

        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline &&
               fixture.WatchManager.ActiveWatches().Any(w => w.WatchId == watchId))
        {
            await Task.Delay(50);
        }

        Assert.DoesNotContain(fixture.WatchManager.ActiveWatches(), w => w.WatchId == watchId);
    }

    [Fact]
    public async Task WatchData_CarriesEntitiesAndTheSchemasToDecodeThem()
    {
        fixture.EnsureAvailable();
        await using var nats = await fixture.ConnectAsync();

        var declaration = new ComponentTypeDeclaration
        {
            Type = new ComponentTypeRef
            {
                LogicalName = Testing.V1.TestPosition.Descriptor.FullName,
                SchemaHash = SchemaHash.Of(Testing.V1.TestPosition.Descriptor),
            },
            FileDescriptorSet = Descriptors.FileDescriptorSetFor(Testing.V1.TestPosition.Descriptor),
        };

        var schemas = new RegisterSchemasRequest { Declarations = { declaration } };
        var bound = RegisterSchemasResponse.Parser.ParseFrom(
            (await nats.RequestAsync<byte[], byte[]>(
                Subjects.SchemaRegister,
                schemas.ToByteArray(),
                replyOpts: new NatsSubOpts { Timeout = Timeout })).Data!);

        var typeId = bound.Bindings[0].TypeId;
        var entity = fixture.World.AllocateEntity();
        fixture.World.SetComponent(
            entity, typeId, new Testing.V1.TestPosition { X = 3.5f }.ToByteArray());

        var watchId = Guid.NewGuid().ToString("N");
        var response = await Subscribe(nats, new WatchRequest
        {
            WatchId = watchId,
            IncludeSystems = true,
            IncludeEntities = true,
        });

        var subscription = await nats.SubscribeCoreAsync<byte[]>(response.DataSubject);
        try
        {
            // The tick loop is not running in this fixture, so push one frame by hand —
            // the same call the loop makes.
            var spec = fixture.WatchManager.ActiveWatches().Single(w => w.WatchId == watchId);
            var data = new WatchData { WatchId = watchId, Tick = 42 };
            if (fixture.WatchManager.ClaimSchemas(spec, fixture.Schemas.Version))
                data.ComponentTypes.AddRange(fixture.Handlers.DescribeTypes());
            data.Entities.AddRange(fixture.Handlers.BuildEntitiesResponse(null, false).Entities);

            await nats.PublishAsync(response.DataSubject, data.ToByteArray());

            using var cts = new CancellationTokenSource(Timeout);
            var message = await subscription.Msgs.ReadAsync(cts.Token);
            var received = WatchData.Parser.ParseFrom(message.Data!);

            Assert.Equal(42ul, received.Tick);
            var snapshot = Assert.Single(received.Entities, e => e.Entity == entity);
            var binding = Assert.Single(snapshot.Components, c => c.TypeId == typeId);
            Assert.Equal(3.5f, Testing.V1.TestPosition.Parser.ParseFrom(binding.Payload).X);
            Assert.Contains(received.ComponentTypes, t => t.TypeId == typeId);
        }
        finally
        {
            await subscription.UnsubscribeAsync();
        }
    }

    [Fact]
    public async Task SystemsAreSentOnceUntilTheyChange()
    {
        fixture.EnsureAvailable();
        await using var nats = await fixture.ConnectAsync();

        var watchId = Guid.NewGuid().ToString("N");
        await Subscribe(nats, new WatchRequest { WatchId = watchId, IncludeSystems = true });

        var spec = fixture.WatchManager.ActiveWatches().Single(w => w.WatchId == watchId);

        Assert.True(fixture.WatchManager.ClaimSystems(spec));
        Assert.False(fixture.WatchManager.ClaimSystems(spec));

        fixture.WatchManager.NotifySystemsChanged();
        Assert.True(fixture.WatchManager.ClaimSystems(spec));
    }
}
