using Client;
using Engine.Core;
using Engine.Core.Messages;
using Google.Protobuf;
using Testing.V1;

namespace Client.Tests.Unit;

[Trait("Category", "Unit")]
public class EntityQueryTests
{
    public EntityQueryTests()
    {
        Serialization.Initialize();
    }

    private static Dictionary<string, (ulong[] Entities, byte[][] Data)> MakeShards(
        params (string TypeName, (ulong Id, byte[] Data)[])[] entries)
    {
        var shards = new Dictionary<string, (ulong[], byte[][])>();
        foreach (var (typeName, items) in entries)
        {
            shards[typeName] = (
                items.Select(i => i.Id).ToArray(),
                items.Select(i => i.Data).ToArray());
        }
        return shards;
    }

    private static byte[] Ser<T>(T value) where T : IMessage<T>, new() => value.ToByteArray();

    // ── Builder → Descriptor ───────────────────────────────────

    [Fact]
    public void ToDescriptor_ReturnsCorrectReadWriteTypes()
    {
        var query = new EntityQuery()
            .With(Query.ReadOnly<TestPosition>())
            .With(Query.ReadWrite<TestVelocity>());
        query.Freeze();

        var desc = query.ToDescriptor();
        Assert.Contains(ComponentTypeId.Of<TestPosition>().TypeName, desc.ReadTypes);
        Assert.Contains(ComponentTypeId.Of<TestVelocity>().TypeName, desc.WriteTypes);
        Assert.Contains(ComponentTypeId.Of<TestPosition>().TypeName, desc.RequiredTypes);
        Assert.Contains(ComponentTypeId.Of<TestVelocity>().TypeName, desc.RequiredTypes);
    }

    [Fact]
    public void ToDescriptor_WithAny_SetsOptionalTypes()
    {
        var query = new EntityQuery()
            .With(Query.ReadOnly<TestPosition>())
            .WithAny(Query.ReadOnly<TestVelocity>());
        query.Freeze();

        var desc = query.ToDescriptor();
        Assert.Contains(ComponentTypeId.Of<TestVelocity>().TypeName, desc.OptionalTypes);
    }

    [Fact]
    public void ToDescriptor_Without_SetsExcludedTypes()
    {
        var query = new EntityQuery()
            .With(Query.ReadOnly<TestPosition>())
            .Without<TestDisabled>();
        query.Freeze();

        var desc = query.ToDescriptor();
        Assert.Contains(ComponentTypeId.Of<TestDisabled>().TypeName, desc.ExcludedTypes);
    }

    // ── Populate + Entities ────────────────────────────────────

    [Fact]
    public void Entities_ReturnsMatchingEntities()
    {
        var query = new EntityQuery()
            .With(Query.ReadOnly<TestPosition>())
            .With(Query.ReadOnly<TestVelocity>());
        query.Freeze();

        var posType = ComponentTypeId.Of<TestPosition>().TypeName;
        var velType = ComponentTypeId.Of<TestVelocity>().TypeName;

        var shards = MakeShards(
            (posType, [(1, Ser(new TestPosition { X = 1f, Y = 2f })), (2, Ser(new TestPosition { X = 3f, Y = 4f }))]),
            (velType, [(1, Ser(new TestVelocity { Vx = 5f, Vy = 6f }))]));
        // Entity 2 only has Position → should not match

        query.Populate(shards, tickId: 1);

        Assert.Single(query.Entities);
        Assert.Equal(1UL, query.Entities[0].Id);
    }

    [Fact]
    public void Entities_WithAny_FiltersCorrectly()
    {
        var query = new EntityQuery()
            .With(Query.ReadOnly<TestPosition>())
            .WithAny(Query.ReadOnly<TestVelocity>());
        query.Freeze();

        var posType = ComponentTypeId.Of<TestPosition>().TypeName;
        var velType = ComponentTypeId.Of<TestVelocity>().TypeName;

        // Entity 1 has both, entity 2 has only Position
        var shards = MakeShards(
            (posType, [(1, Ser(new TestPosition { X = 1f, Y = 2f })), (2, Ser(new TestPosition { X = 3f, Y = 4f }))]),
            (velType, [(1, Ser(new TestVelocity { Vx = 5f, Vy = 6f }))]));

        query.Populate(shards, tickId: 1);

        // Only entity 1 has the optional type
        Assert.Single(query.Entities);
        Assert.Equal(1UL, query.Entities[0].Id);
    }

    [Fact]
    public void Entities_Without_ExcludesCorrectly()
    {
        var query = new EntityQuery()
            .With(Query.ReadOnly<TestPosition>())
            .Without<TestDisabled>();
        query.Freeze();

        var posType = ComponentTypeId.Of<TestPosition>().TypeName;
        var disabledType = ComponentTypeId.Of<TestDisabled>().TypeName;

        // Entity 1 has Position only, entity 2 has Position + Disabled
        var shards = MakeShards(
            (posType, [(1, Ser(new TestPosition { X = 1f, Y = 2f })), (2, Ser(new TestPosition { X = 3f, Y = 4f }))]),
            (disabledType, [(2, Ser(new TestDisabled()))]));

        query.Populate(shards, tickId: 1);

        Assert.Single(query.Entities);
        Assert.Equal(1UL, query.Entities[0].Id);
    }

    [Fact]
    public void Entities_EmptyShards_ReturnsEmpty()
    {
        var query = new EntityQuery()
            .With(Query.ReadOnly<TestPosition>());
        query.Freeze();

        query.Populate(new Dictionary<string, (ulong[], byte[][])>(), tickId: 1);

        Assert.Empty(query.Entities);
    }

    // ── Get / TryGet / Has ─────────────────────────────────────

    [Fact]
    public void Get_ReturnsDeserializedComponent()
    {
        var query = new EntityQuery()
            .With(Query.ReadOnly<TestPosition>());
        query.Freeze();

        var posType = ComponentTypeId.Of<TestPosition>().TypeName;
        var shards = MakeShards(
            (posType, [(1, Ser(new TestPosition { X = 10f, Y = 20f }))]));
        query.Populate(shards, tickId: 1);

        var pos = query.Get<TestPosition>(new Entity(1));
        Assert.Equal(10f, pos.X);
        Assert.Equal(20f, pos.Y);
    }

    [Fact]
    public void Get_ThrowsOnMissingEntity()
    {
        var query = new EntityQuery()
            .With(Query.ReadOnly<TestPosition>());
        query.Freeze();
        query.Populate(new Dictionary<string, (ulong[], byte[][])>(), tickId: 1);

        Assert.Throws<KeyNotFoundException>(() => query.Get<TestPosition>(new Entity(999)));
    }

    [Fact]
    public void TryGet_ReturnsFalseOnMissing()
    {
        var query = new EntityQuery()
            .With(Query.ReadOnly<TestPosition>());
        query.Freeze();
        query.Populate(new Dictionary<string, (ulong[], byte[][])>(), tickId: 1);

        Assert.False(query.TryGet<TestPosition>(new Entity(999), out _));
    }

    [Fact]
    public void TryGet_ReturnsTrueAndValue()
    {
        var query = new EntityQuery()
            .With(Query.ReadOnly<TestPosition>());
        query.Freeze();

        var posType = ComponentTypeId.Of<TestPosition>().TypeName;
        var shards = MakeShards(
            (posType, [(1, Ser(new TestPosition { X = 7f, Y = 8f }))]));
        query.Populate(shards, tickId: 1);

        Assert.True(query.TryGet<TestPosition>(new Entity(1), out var pos));
        Assert.Equal(7f, pos.X);
    }

    [Fact]
    public void Has_ReturnsTrueWhenPresent()
    {
        var query = new EntityQuery()
            .With(Query.ReadOnly<TestPosition>());
        query.Freeze();

        var posType = ComponentTypeId.Of<TestPosition>().TypeName;
        var shards = MakeShards((posType, [(1, Ser(new TestPosition { X = 1f, Y = 2f }))]));
        query.Populate(shards, tickId: 1);

        Assert.True(query.Has<TestPosition>(new Entity(1)));
        Assert.False(query.Has<TestPosition>(new Entity(999)));
    }

    // ── Set ────────────────────────────────────────────────────

    [Fact]
    public void Set_ReadWrite_BuffersMutation()
    {
        var query = new EntityQuery()
            .With(Query.ReadWrite<TestPosition>());
        query.Freeze();

        var posType = ComponentTypeId.Of<TestPosition>().TypeName;
        var shards = MakeShards((posType, [(1, Ser(new TestPosition { X = 1f, Y = 2f }))]));
        query.Populate(shards, tickId: 42);

        query.Set(new Entity(1), new TestPosition { X = 99f, Y = 100f });

        var mutations = query.FlushMutations();
        Assert.Single(mutations);
        Assert.Equal(posType, mutations[0].ComponentType);
        Assert.Equal(42UL, mutations[0].TickId);
        Assert.Equal(1UL, mutations[0].Entities[0]);
    }

    [Fact]
    public void Set_ReadOnly_Throws()
    {
        var query = new EntityQuery()
            .With(Query.ReadOnly<TestPosition>());
        query.Freeze();

        var posType = ComponentTypeId.Of<TestPosition>().TypeName;
        var shards = MakeShards((posType, [(1, Ser(new TestPosition { X = 1f, Y = 2f }))]));
        query.Populate(shards, tickId: 1);

        Assert.Throws<InvalidOperationException>(() =>
            query.Set(new Entity(1), new TestPosition { X = 99f, Y = 100f }));
    }

    [Fact]
    public void FlushMutations_ClearsBuffer()
    {
        var query = new EntityQuery()
            .With(Query.ReadWrite<TestPosition>());
        query.Freeze();

        var posType = ComponentTypeId.Of<TestPosition>().TypeName;
        var shards = MakeShards((posType, [(1, Ser(new TestPosition { X = 1f, Y = 2f }))]));
        query.Populate(shards, tickId: 1);

        query.Set(new Entity(1), new TestPosition { X = 5f, Y = 6f });
        Assert.Single(query.FlushMutations());
        Assert.Empty(query.FlushMutations());
    }

    // ── Each ───────────────────────────────────────────────────

    [Fact]
    public void Each_ReturnsTuplesWithEntity()
    {
        var query = new EntityQuery()
            .With(Query.ReadOnly<TestPosition>())
            .With(Query.ReadOnly<TestVelocity>());
        query.Freeze();

        var posType = ComponentTypeId.Of<TestPosition>().TypeName;
        var velType = ComponentTypeId.Of<TestVelocity>().TypeName;

        var shards = MakeShards(
            (posType, [(1, Ser(new TestPosition { X = 1f, Y = 2f }))]),
            (velType, [(1, Ser(new TestVelocity { Vx = 3f, Vy = 4f }))]));
        query.Populate(shards, tickId: 1);

        var results = query.Each<TestPosition, TestVelocity>().ToList();
        Assert.Single(results);
        Assert.Equal(1UL, results[0].Entity.Id);
        Assert.Equal(1f, results[0].C1.X);
        Assert.Equal(3f, results[0].C2.Vx);
    }

    // ── Tag joins ──────────────────────────────────────────────

    [Fact]
    public void ToDescriptor_WithAnyTagged_SetsTaggedTypes()
    {
        var query = new EntityQuery()
            .With(Query.ReadOnly<TestPosition>())
            .WithAnyTagged<TestSetting>();
        query.Freeze();

        var desc = query.ToDescriptor();
        Assert.Contains(ComponentTypeId.Of<TestSetting>().TypeName, desc.TaggedTypes);
    }

    [Fact]
    public void WithAnyTagged_MatchesEntitiesCarryingAResolvedType()
    {
        var query = new EntityQuery()
            .With(Query.ReadOnly<TestPosition>())
            .WithAnyTagged<TestSetting>();
        query.Freeze();

        var posType = ComponentTypeId.Of<TestPosition>().TypeName;
        var velType = ComponentTypeId.Of<TestVelocity>().TypeName;

        // Entity 1 and 2 both have Position, but only entity 1 carries the tagged type
        var shards = MakeShards(
            (posType, [(1, Ser(new TestPosition { X = 1f, Y = 2f })), (2, Ser(new TestPosition { X = 3f, Y = 4f }))]),
            (velType, [(1, Ser(new TestVelocity { Vx = 5f, Vy = 6f }))]));

        query.Populate(shards, tickId: 1, TagResolution(velType));

        Assert.Single(query.Entities);
        Assert.Equal(1UL, query.Entities[0].Id);
    }

    [Fact]
    public void WithAnyTagged_NoResolvedTypes_MatchesNothing()
    {
        var query = new EntityQuery()
            .With(Query.ReadOnly<TestPosition>())
            .WithAnyTagged<TestSetting>();
        query.Freeze();

        var posType = ComponentTypeId.Of<TestPosition>().TypeName;
        var shards = MakeShards((posType, [(1, Ser(new TestPosition { X = 1f, Y = 2f }))]));

        query.Populate(shards, tickId: 1, TagResolution());

        Assert.Empty(query.Entities);
    }

    [Fact]
    public void GetTagged_ReturnsMatchedComponentsByTypeName()
    {
        var query = new EntityQuery()
            .With(Query.ReadOnly<TestPosition>())
            .WithAnyTagged<TestSetting>();
        query.Freeze();

        var posType = ComponentTypeId.Of<TestPosition>().TypeName;
        var velType = ComponentTypeId.Of<TestVelocity>().TypeName;

        var shards = MakeShards(
            (posType, [(1, Ser(new TestPosition { X = 1f, Y = 2f }))]),
            (velType, [(1, Ser(new TestVelocity { Vx = 5f, Vy = 6f }))]));
        query.Populate(shards, tickId: 1, TagResolution(velType));

        var tagged = query.GetTagged<TestSetting>(new Entity(1)).ToList();

        Assert.Single(tagged);
        Assert.True(tagged[0].Is<TestVelocity>());
        Assert.Equal(5f, tagged[0].As<TestVelocity>().Vx);
    }

    [Fact]
    public void Set_TaggedType_Throws()
    {
        var query = new EntityQuery()
            .With(Query.ReadOnly<TestPosition>())
            .WithAnyTagged<TestSetting>();
        query.Freeze();

        var posType = ComponentTypeId.Of<TestPosition>().TypeName;
        var velType = ComponentTypeId.Of<TestVelocity>().TypeName;

        var shards = MakeShards(
            (posType, [(1, Ser(new TestPosition { X = 1f, Y = 2f }))]),
            (velType, [(1, Ser(new TestVelocity { Vx = 5f, Vy = 6f }))]));
        query.Populate(shards, tickId: 1, TagResolution(velType));

        Assert.Throws<InvalidOperationException>(() =>
            query.Set(new Entity(1), new TestVelocity { Vx = 9f, Vy = 9f }));
    }

    private static Dictionary<string, string[]> TagResolution(params string[] typeNames) =>
        new() { [ComponentTypeId.Of<TestSetting>().TypeName] = typeNames };

    // ── Freeze ─────────────────────────────────────────────────

    [Fact]
    public void With_AfterFreeze_Throws()
    {
        var query = new EntityQuery()
            .With(Query.ReadOnly<TestPosition>());
        query.Freeze();

        Assert.Throws<InvalidOperationException>(() =>
            query.With(Query.ReadOnly<TestVelocity>()));
    }
}
