using Ecs.Protocol.V1;
using Engine.Coordinator;
using Google.Protobuf;
using Testing.V1;

namespace Engine.Tests.Unit;

/// <summary>
/// Component types are entities. That is what lets a domain say something about a type
/// — that it is a setting, that it belongs to a category — without the engine having any
/// idea what that means.
/// </summary>
public class ComponentTypeEntityTests : WorldFixture
{
    [Fact]
    public void TypeEntity_CarriesTheTypeNameAndItsSchema()
    {
        var position = Bind(TestPosition.Descriptor);

        var entity = World.GetOrCreateTypeEntity(TypeOf(position));

        var info = Ecs.V1.ComponentInfo.Parser.ParseFrom(
            World.GetComponent(entity, Schemas.ComponentInfoTypeId));
        Assert.Equal("testing.v1.TestPosition", info.TypeName);

        var schema = Ecs.V1.ComponentSchema.Parser.ParseFrom(
            World.GetComponent(entity, Schemas.ComponentSchemaTypeId));
        Assert.Equal(position, schema.TypeId);
        Assert.NotEqual(0ul, schema.SchemaHash);
        Assert.NotEmpty(schema.FileDescriptorSet);
    }

    [Fact]
    public void TypeEntity_IsCreatedOnceAndShownAgain()
    {
        var position = Bind(TestPosition.Descriptor);

        var first = World.GetOrCreateTypeEntity(TypeOf(position));
        var countAfterFirst = World.EntityCount;
        var second = World.GetOrCreateTypeEntity(TypeOf(position));

        Assert.Equal(first, second);
        Assert.Equal(countAfterFirst, World.EntityCount);
        Assert.Equal(first, World.FindTypeEntity(position));
    }

    [Fact]
    public void TypeEntities_ShareTheEntityIdSpace()
    {
        var position = Bind(TestPosition.Descriptor);

        var ordinary = World.AllocateEntity();
        var typeEntity = World.GetOrCreateTypeEntity(TypeOf(position));

        Assert.NotEqual(ordinary, typeEntity);
    }

    [Fact]
    public void TypesTaggedWith_FindsTypesWhoseTypeEntityCarriesTheTag()
    {
        var position = Bind(TestPosition.Descriptor);
        var velocity = Bind(TestVelocity.Descriptor);
        var setting = Bind(TestSetting.Descriptor);

        World.SetComponent(World.GetOrCreateTypeEntity(TypeOf(position)), setting, []);
        World.GetOrCreateTypeEntity(TypeOf(velocity));

        Assert.Equal([position], World.TypesTaggedWith(setting));
    }

    [Fact]
    public void TagJoin_MatchesEntitiesCarryingAnyTaggedComponent()
    {
        var position = Bind(TestPosition.Descriptor);
        var velocity = Bind(TestVelocity.Descriptor);
        var setting = Bind(TestSetting.Descriptor);

        World.SetComponent(World.GetOrCreateTypeEntity(TypeOf(position)), setting, []);

        var tagged = World.AllocateEntity();
        World.SetComponent(tagged, position, []);

        var untagged = World.AllocateEntity();
        World.SetComponent(untagged, velocity, []);

        var query = new QueryDescriptor { Tagged = { new TaggedAccess { TagTypeId = setting } } };
        var resolution = World.ResolveTags([setting]);

        Assert.Equal([tagged], World.MatchQueries([query], resolution));
    }

    [Fact]
    public void TagJoin_MatchesNothingWhenNoTypeCarriesTheTag()
    {
        var position = Bind(TestPosition.Descriptor);
        var setting = Bind(TestSetting.Descriptor);

        var entity = World.AllocateEntity();
        World.SetComponent(entity, position, []);

        var query = new QueryDescriptor { Tagged = { new TaggedAccess { TagTypeId = setting } } };

        Assert.Empty(World.MatchQueries([query], World.ResolveTags([setting])));
    }

    [Fact]
    public void ComponentsOnATypeEntity_AreQueryableLikeAnyOther()
    {
        var position = Bind(TestPosition.Descriptor);
        var category = Bind(TestCategory.Descriptor);

        var typeEntity = World.GetOrCreateTypeEntity(TypeOf(position));
        World.SetComponent(typeEntity, category, new TestCategory { Name = "Control" }.ToByteArray());

        var query = new QueryDescriptor
        {
            Required = { new Ecs.Protocol.V1.ComponentAccess { TypeId = category, Access = Access.Read } },
        };

        Assert.Equal([typeEntity], World.MatchQueries([query], new Dictionary<uint, uint[]>()));
    }

    private RegisteredType TypeOf(uint typeId)
    {
        Assert.True(Schemas.TryGet(typeId, out var type));
        return type;
    }
}
