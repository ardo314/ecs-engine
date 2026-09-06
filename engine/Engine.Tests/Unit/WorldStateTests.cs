using Ecs.Protocol.V1;
using Engine.Coordinator;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Testing.V1;

namespace Engine.Tests.Unit;

/// <summary>
/// Shared setup for tests that need a world with a few types already bound.
/// </summary>
public abstract class WorldFixture
{
    protected SchemaRegistry Schemas { get; } = new();

    protected WorldState World { get; }

    protected WorldFixture()
    {
        World = new WorldState(Schemas);
    }

    protected uint Bind(MessageDescriptor descriptor)
    {
        var request = new RegisterSchemasRequest();
        request.Declarations.Add(new ComponentTypeDeclaration
        {
            Type = new ComponentTypeRef
            {
                LogicalName = descriptor.FullName,
                SchemaHash = SchemaHash.Of(descriptor),
            },
            FileDescriptorSet = Descriptors.FileDescriptorSetFor(descriptor),
        });

        var response = Schemas.Register(request);
        Assert.Empty(response.Rejections);
        return response.Bindings[0].TypeId;
    }
}

public class WorldStateTests : WorldFixture
{
    private static QueryDescriptor Requiring(params uint[] typeIds)
    {
        var query = new QueryDescriptor();
        foreach (var id in typeIds)
            query.Required.Add(new Ecs.Protocol.V1.ComponentAccess { TypeId = id, Access = Access.Read });
        return query;
    }

    [Fact]
    public void AllocateEntity_ReturnsUniqueIdsAndMarksThemAlive()
    {
        var first = World.AllocateEntity();
        var second = World.AllocateEntity();

        Assert.NotEqual(first, second);
        Assert.True(World.IsAlive(first));
        Assert.False(World.IsAlive(9999));
    }

    [Fact]
    public void SetComponent_StoresOpaqueBytesUnderTheDenseId()
    {
        var position = Bind(TestPosition.Descriptor);
        var entity = World.AllocateEntity();
        var payload = new TestPosition { X = 1.5f }.ToByteArray();

        World.SetComponent(entity, position, payload);

        Assert.Equal(payload, World.GetComponent(entity, position));
    }

    [Fact]
    public void SetComponent_OverwritesTheExistingValue()
    {
        var position = Bind(TestPosition.Descriptor);
        var entity = World.AllocateEntity();

        World.SetComponent(entity, position, [1]);
        World.SetComponent(entity, position, [2]);

        Assert.Equal([2], World.GetComponent(entity, position));
    }

    [Fact]
    public void GetComponent_ReturnsNullWhenAbsent()
    {
        var position = Bind(TestPosition.Descriptor);

        Assert.Null(World.GetComponent(World.AllocateEntity(), position));
        Assert.Null(World.GetComponent(9999, position));
    }

    [Fact]
    public void DestroyEntity_RemovesItAndItsComponents()
    {
        var position = Bind(TestPosition.Descriptor);
        var entity = World.AllocateEntity();
        World.SetComponent(entity, position, [1]);

        World.DestroyEntity(entity);

        Assert.False(World.IsAlive(entity));
        Assert.Null(World.GetComponent(entity, position));
    }

    [Fact]
    public void MatchQueries_RequiresEveryDeclaredType()
    {
        var position = Bind(TestPosition.Descriptor);
        var velocity = Bind(TestVelocity.Descriptor);

        var both = World.AllocateEntity();
        World.SetComponent(both, position, []);
        World.SetComponent(both, velocity, []);

        var onlyPosition = World.AllocateEntity();
        World.SetComponent(onlyPosition, position, []);

        var matched = World.MatchQueries([Requiring(position, velocity)], new Dictionary<uint, uint[]>());

        Assert.Equal([both], matched);
    }

    [Fact]
    public void MatchQueries_TreatsOptionalAsAtLeastOne()
    {
        var position = Bind(TestPosition.Descriptor);
        var velocity = Bind(TestVelocity.Descriptor);
        var disabled = Bind(TestDisabled.Descriptor);

        var entity = World.AllocateEntity();
        World.SetComponent(entity, position, []);
        World.SetComponent(entity, velocity, []);

        var query = Requiring(position);
        query.Optional.Add(new Ecs.Protocol.V1.ComponentAccess { TypeId = velocity, Access = Access.Read });
        query.Optional.Add(new Ecs.Protocol.V1.ComponentAccess { TypeId = disabled, Access = Access.Read });

        Assert.Equal([entity], World.MatchQueries([query], new Dictionary<uint, uint[]>()));
    }

    [Fact]
    public void MatchQueries_HonoursExclusions()
    {
        var position = Bind(TestPosition.Descriptor);
        var disabled = Bind(TestDisabled.Descriptor);

        var kept = World.AllocateEntity();
        World.SetComponent(kept, position, []);

        var skipped = World.AllocateEntity();
        World.SetComponent(skipped, position, []);
        World.SetComponent(skipped, disabled, []);

        var query = Requiring(position);
        query.Excluded.Add(disabled);

        Assert.Equal([kept], World.MatchQueries([query], new Dictionary<uint, uint[]>()));
    }

    [Fact]
    public void MatchQueries_UnionsAcrossQueries()
    {
        var position = Bind(TestPosition.Descriptor);
        var velocity = Bind(TestVelocity.Descriptor);

        var a = World.AllocateEntity();
        World.SetComponent(a, position, []);
        var b = World.AllocateEntity();
        World.SetComponent(b, velocity, []);

        var matched = World.MatchQueries(
            [Requiring(position), Requiring(velocity)], new Dictionary<uint, uint[]>());

        Assert.Equal(2, matched.Count);
        Assert.Contains(a, matched);
        Assert.Contains(b, matched);
    }

    [Fact]
    public void Filter_ResolvesNamesAndReturnsNothingForAnUnknownRequirement()
    {
        var position = Bind(TestPosition.Descriptor);
        var entity = World.AllocateEntity();
        World.SetComponent(entity, position, []);

        var byName = World.Filter(new EntityFilter { AllOf = { "testing.v1.TestPosition" } });
        Assert.Equal([entity], byName);

        var unknown = World.Filter(new EntityFilter { AllOf = { "testing.v1.NeverRegistered" } });
        Assert.Empty(unknown);
    }
}
