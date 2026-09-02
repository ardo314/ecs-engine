using Ecs.V1;
using Engine.Core;
using Engine.Core.Messages;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Testing.V1;

namespace Client.Tests.Unit;

internal class DescribingSystem : SystemBase
{
    public DescribingSystem() => NewQuery()
        .With(Query.ReadWrite<TestDescribed>())
        .With(Query.ReadOnly<TestUndescribed>());

    protected override Task OnUpdateAsync() => Task.CompletedTask;
}

internal class DuplicateQuerySystem : SystemBase
{
    public DuplicateQuerySystem()
    {
        NewQuery().With(Query.ReadOnly<TestDescribed>());
        NewQuery().With(Query.ReadWrite<TestDescribed>());
    }

    protected override Task OnUpdateAsync() => Task.CompletedTask;
}

internal class SpawningSystem : SystemBase
{
    protected override void OnAdd() => Commands.CreateEntity(new TestUndescribed { Value = 1 });

    protected override Task OnUpdateAsync() => Task.CompletedTask;
}

[Trait("Category", "Unit")]
public class ComponentDescriptionTests
{
    public ComponentDescriptionTests() => Serialization.Initialize();

    private static ComponentAddRequest[] AddsFor<T>(SystemBase system, string componentType)
        where T : IMessage<T>, new() =>
        system.Commands.Adds
            .Where(a => a.Target.ComponentType == ComponentTypeId.Of<T>().TypeName
                        && a.ComponentType == componentType)
            .ToArray();

    private static ComponentAddRequest[] InfoFor<T>(SystemBase system) where T : IMessage<T>, new() =>
        AddsFor<T>(system, ComponentTypes.Info);

    [Fact]
    public void EveryQueriedType_GetsATypeEntity()
    {
        var system = new DescribingSystem();
        system.InvokeOnAdd();

        Assert.Single(InfoFor<TestDescribed>(system));
        Assert.Single(InfoFor<TestUndescribed>(system));
    }

    [Fact]
    public void TypeInfo_CarriesTheProtoFullName()
    {
        var system = new DescribingSystem();
        system.InvokeOnAdd();

        var info = ComponentInfo.Parser.ParseFrom(InfoFor<TestUndescribed>(system).Single().Data);

        Assert.Equal("testing.v1.TestUndescribed", info.TypeName);
    }

    [Fact]
    public void EveryQueriedType_CarriesItsOwnSchema()
    {
        var system = new DescribingSystem();
        system.InvokeOnAdd();

        var set = SchemaFor<TestUndescribed>(system);

        // Transitively closed: the type's own file plus everything it imports.
        Assert.Contains(set.File, f => f.Name == "testing/v1/testing.proto");
        Assert.Contains(set.File, f => f.Name == "ecs/v1/component.proto");
        Assert.Contains(set.File, f => f.Name == "google/protobuf/any.proto");
    }

    [Fact]
    public void Schema_DescribesTheTypesFieldsForConsumersWithoutTheGeneratedCode()
    {
        var system = new DescribingSystem();
        system.InvokeOnAdd();

        var set = SchemaFor<TestUndescribed>(system);
        var message = set.File
            .Single(f => f.Name == "testing/v1/testing.proto")
            .MessageType.Single(m => m.Name == "TestUndescribed");

        var field = message.Field.Single();
        Assert.Equal("value", field.Name);
        Assert.Equal(1, field.Number);
        Assert.Equal(FieldDescriptorProto.Types.Type.Int32, field.Type);
    }

    [Fact]
    public void DescribedType_AlsoGetsItsAttachments()
    {
        var system = new DescribingSystem();
        system.InvokeOnAdd();

        var category = AddsFor<TestDescribed>(system, "testing.v1.TestCategory").Single();

        Assert.Equal("Control", TestCategory.Parser.ParseFrom(category.Data).Name);
        Assert.Single(AddsFor<TestDescribed>(system, "testing.v1.TestSetting"));
    }

    [Fact]
    public void AttachedComponentTypes_GetTypeEntitiesToo()
    {
        var system = new DescribingSystem();
        system.InvokeOnAdd();

        Assert.Single(InfoFor<TestCategory>(system));
        Assert.Single(InfoFor<TestSetting>(system));
    }

    [Fact]
    public void TypeUsedByMultipleQueries_IsDescribedOnce()
    {
        var system = new DuplicateQuerySystem();
        system.InvokeOnAdd();

        Assert.Single(InfoFor<TestDescribed>(system));
    }

    [Fact]
    public void SpawnedComponentTypes_GetTypeEntities()
    {
        var system = new SpawningSystem();
        system.InvokeOnAdd();

        Assert.Single(InfoFor<TestUndescribed>(system));
    }

    private static FileDescriptorSet SchemaFor<T>(SystemBase system) where T : IMessage<T>, new()
    {
        var schema = ComponentSchema.Parser.ParseFrom(
            AddsFor<T>(system, ComponentTypes.Schema).Single().Data);
        return FileDescriptorSet.Parser.ParseFrom(schema.FileDescriptorSet);
    }
}
