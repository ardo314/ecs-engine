using Ecs.Protocol;
using Ecs.Protocol.V1;
using Engine.Core;
using Google.Protobuf;
using Testing.V1;

namespace Client.Tests.Unit;

public class EntityQueryTests
{
    private const uint PositionId = 10;
    private const uint VelocityId = 20;
    private const uint DisabledId = 30;
    private const uint SettingId = 40;
    private const uint DescribedId = 50;

    private static SchemaBindings Bindings()
    {
        var bindings = new SchemaBindings();
        bindings.Add(ComponentType<TestPosition>.Name, PositionId);
        bindings.Add(ComponentType<TestVelocity>.Name, VelocityId);
        bindings.Add(ComponentType<TestDisabled>.Name, DisabledId);
        bindings.Add(ComponentType<TestSetting>.Name, SettingId);
        bindings.Add(ComponentType<TestDescribed>.Name, DescribedId);
        return bindings;
    }

    private static EntityQuery Ready(Action<EntityQuery> declare)
    {
        var query = new EntityQuery();
        declare(query);
        query.Freeze();
        query.Bind(Bindings());
        return query;
    }

    /// <summary>Builds the column set a coordinator invocation would carry.</summary>
    private static Dictionary<uint, ComponentColumn> Columns(
        params (uint TypeId, ulong Entity, byte[]? Payload)[] rows)
    {
        return rows
            .GroupBy(r => r.TypeId)
            .ToDictionary(
                g => g.Key,
                g => new ComponentColumn(
                    g.Key,
                    [.. g.Select(r => r.Entity)],
                    [.. g.Select(r => r.Payload)]));
    }

    private static SystemInvocation Invocation(params TagResolution[] tags)
    {
        var invocation = new SystemInvocation { Tick = 1, LeaseId = "lease", DeltaSeconds = 0.05f };
        invocation.Tags.AddRange(tags);
        return invocation;
    }

    private static byte[] Position(float x) => new TestPosition { X = x }.ToByteArray();

    // ── Descriptor ──────────────────────────────────────────────

    [Fact]
    public void Descriptor_CarriesTypeIdsAndAccess()
    {
        var query = Ready(q => q
            .With(Query.Write<TestPosition>())
            .With(Query.Read<TestVelocity>()));

        var descriptor = query.ToDescriptor();

        Assert.Contains(descriptor.Required,
            a => a.TypeId == PositionId && a.Access == Access.Write);
        Assert.Contains(descriptor.Required,
            a => a.TypeId == VelocityId && a.Access == Access.Read);
    }

    [Fact]
    public void Descriptor_RecordsOptionalExcludedAndTaggedSeparately()
    {
        var query = Ready(q =>
        {
            q.With(Query.Read<TestPosition>());
            q.WithAny(Query.Read<TestVelocity>());
            q.Without<TestDisabled>();
            q.WithAnyTagged<TestSetting>();
        });

        var descriptor = query.ToDescriptor();

        Assert.Equal(VelocityId, Assert.Single(descriptor.Optional).TypeId);
        Assert.Equal(DisabledId, Assert.Single(descriptor.Excluded));
        Assert.Equal(SettingId, Assert.Single(descriptor.Tagged).TagTypeId);
    }

    [Fact]
    public void Declaring_AfterFreezeThrows()
    {
        var query = Ready(q => q.With(Query.Read<TestPosition>()));

        var error = Assert.Throws<InvalidOperationException>(() => query.With(Query.Read<TestVelocity>()));
        Assert.Contains("constructor", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Declarations_CoverEveryTypeTheQueryMentions()
    {
        var query = new EntityQuery();
        query.With(Query.Read<TestPosition>()).Without<TestDisabled>().WithAnyTagged<TestSetting>();

        var names = query.Declarations().Select(d => d.Type.LogicalName).ToList();

        Assert.Contains("testing.v1.TestPosition", names);
        Assert.Contains("testing.v1.TestDisabled", names);
        Assert.Contains("testing.v1.TestSetting", names);
    }

    // ── Matching ────────────────────────────────────────────────

    [Fact]
    public void RequiredTypes_MustAllBePresent()
    {
        var query = Ready(q => q.With(Query.Read<TestPosition>()).With(Query.Read<TestVelocity>()));

        query.Populate(Columns(
            (PositionId, 1, Position(1)),
            (PositionId, 2, Position(2)),
            (VelocityId, 1, []),
            (VelocityId, 2, null)), Invocation());

        Assert.Equal([new Entity(1)], query.Entities);
    }

    [Fact]
    public void OptionalTypes_RequireAtLeastOne()
    {
        var query = Ready(q =>
        {
            q.With(Query.Read<TestPosition>());
            q.WithAny(Query.Read<TestVelocity>(), Query.Read<TestDisabled>());
        });

        query.Populate(Columns(
            (PositionId, 1, Position(1)),
            (PositionId, 2, Position(2)),
            (VelocityId, 1, []),
            (VelocityId, 2, null),
            (DisabledId, 1, null),
            (DisabledId, 2, null)), Invocation());

        Assert.Equal([new Entity(1)], query.Entities);
    }

    [Fact]
    public void ExcludedTypes_RemoveTheEntity()
    {
        var query = Ready(q => q.With(Query.Read<TestPosition>()).Without<TestDisabled>());

        query.Populate(Columns(
            (PositionId, 1, Position(1)),
            (PositionId, 2, Position(2)),
            (DisabledId, 1, null),
            (DisabledId, 2, [])), Invocation());

        Assert.Equal([new Entity(1)], query.Entities);
    }

    [Fact]
    public void AZeroLengthPayloadIsPresent_ANullPayloadIsAbsent()
    {
        // An all-default protobuf message is zero bytes, so a marker component and a
        // missing component would be indistinguishable if length carried absence.
        Assert.Empty(new TestDisabled().ToByteArray());

        var query = Ready(q => q.With(Query.Read<TestDisabled>()));

        query.Populate(Columns(
            (DisabledId, 1, []),
            (DisabledId, 2, null)), Invocation());

        Assert.Equal([new Entity(1)], query.Entities);
    }

    [Fact]
    public void AnEmptyInvocationMatchesNothing()
    {
        var query = Ready(q => q.With(Query.Read<TestPosition>()));

        query.Populate(new Dictionary<uint, ComponentColumn>(), Invocation());

        Assert.Empty(query.Entities);
    }

    // ── Reading and writing ─────────────────────────────────────

    [Fact]
    public void Get_ReturnsTheDecodedComponent()
    {
        var query = Ready(q => q.With(Query.Read<TestPosition>()));
        query.Populate(Columns((PositionId, 1, Position(2.5f))), Invocation());

        Assert.Equal(2.5f, query.Get<TestPosition>(new Entity(1)).X);
        Assert.True(query.Has<TestPosition>(new Entity(1)));
    }

    [Fact]
    public void Get_ThrowsForAnEntityItDoesNotHold()
    {
        var query = Ready(q => q.With(Query.Read<TestPosition>()));
        query.Populate(Columns((PositionId, 1, Position(1))), Invocation());

        Assert.Throws<KeyNotFoundException>(() => query.Get<TestPosition>(new Entity(99)));
        Assert.False(query.TryGet<TestPosition>(new Entity(99), out _));
    }

    [Fact]
    public void Set_BuffersAWriteAsABatch()
    {
        var query = Ready(q => q.With(Query.Write<TestPosition>()));
        query.Populate(Columns((PositionId, 1, Position(1))), Invocation());

        query.Set(new Entity(1), new TestPosition { X = 9f });
        var batch = Assert.Single(query.FlushWrites());

        Assert.Equal(PositionId, batch.TypeId);
        var column = ComponentBatchCodecs.Decode(batch);
        Assert.Equal([1ul], column.Entities);
        Assert.Equal(9f, TestPosition.Parser.ParseFrom(column.Rows[0]).X);
    }

    [Fact]
    public void Flush_EmptiesTheBuffer()
    {
        var query = Ready(q => q.With(Query.Write<TestPosition>()));
        query.Populate(Columns((PositionId, 1, Position(1))), Invocation());
        query.Set(new Entity(1), new TestPosition { X = 9f });

        Assert.Single(query.FlushWrites());
        Assert.Empty(query.FlushWrites());
    }

    [Fact]
    public void Set_OnAReadOnlyTypeThrowsBeforeItReachesTheNetwork()
    {
        var query = Ready(q => q.With(Query.Read<TestPosition>()));
        query.Populate(Columns((PositionId, 1, Position(1))), Invocation());

        var error = Assert.Throws<InvalidOperationException>(
            () => query.Set(new Entity(1), new TestPosition { X = 1f }));

        Assert.Contains("read-only", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Each_YieldsTuplesForEntitiesHoldingEveryComponent()
    {
        var query = Ready(q => q.With(Query.Read<TestPosition>()).With(Query.Read<TestVelocity>()));

        query.Populate(Columns(
            (PositionId, 1, Position(1)),
            (VelocityId, 1, new TestVelocity { Vx = 5f }.ToByteArray())), Invocation());

        var (entity, position, velocity) = Assert.Single(query.Each<TestPosition, TestVelocity>());
        Assert.Equal(new Entity(1), entity);
        Assert.Equal(1f, position.X);
        Assert.Equal(5f, velocity.Vx);
    }

    // ── Tag joins ───────────────────────────────────────────────

    [Fact]
    public void TagJoin_MatchesEntitiesCarryingAResolvedType()
    {
        var query = Ready(q => q.With(Query.Read<TestPosition>()).WithAnyTagged<TestSetting>());

        var tags = new TagResolution { TagTypeId = SettingId, TypeIds = { DescribedId } };
        query.Populate(Columns(
            (PositionId, 1, Position(1)),
            (PositionId, 2, Position(2)),
            (DescribedId, 1, new TestDescribed { Value = 7 }.ToByteArray()),
            (DescribedId, 2, null)), Invocation(tags));

        Assert.Equal([new Entity(1)], query.Entities);
        Assert.Equal(["testing.v1.TestDescribed"], query.TaggedTypeNames<TestSetting>());
    }

    [Fact]
    public void TagJoin_MatchesNothingWhenNoTypeCarriesTheTag()
    {
        var query = Ready(q => q.With(Query.Read<TestPosition>()).WithAnyTagged<TestSetting>());

        query.Populate(Columns((PositionId, 1, Position(1))),
            Invocation(new TagResolution { TagTypeId = SettingId }));

        Assert.Empty(query.Entities);
    }

    [Fact]
    public void GetTagged_HandsBackRawPayloadsThatCanBeDecodedOnceRecognised()
    {
        var query = Ready(q => q.With(Query.Read<TestPosition>()).WithAnyTagged<TestSetting>());

        var tags = new TagResolution { TagTypeId = SettingId, TypeIds = { DescribedId } };
        query.Populate(Columns(
            (PositionId, 1, Position(1)),
            (DescribedId, 1, new TestDescribed { Value = 7 }.ToByteArray())), Invocation(tags));

        var tagged = Assert.Single(query.GetTagged<TestSetting>(new Entity(1)));
        Assert.Equal("testing.v1.TestDescribed", tagged.TypeName);
        Assert.Equal(7, tagged.As<TestDescribed>()!.Value);
        Assert.Null(tagged.As<TestPosition>());
    }

    [Fact]
    public void Set_OnATagJoinedTypeThrows()
    {
        var query = Ready(q => q.With(Query.Read<TestPosition>()).WithAnyTagged<TestSetting>());

        var tags = new TagResolution { TagTypeId = SettingId, TypeIds = { DescribedId } };
        query.Populate(Columns(
            (PositionId, 1, Position(1)),
            (DescribedId, 1, new TestDescribed { Value = 7 }.ToByteArray())), Invocation(tags));

        Assert.Throws<InvalidOperationException>(
            () => query.Set(new Entity(1), new TestDescribed { Value = 8 }));
    }
}
