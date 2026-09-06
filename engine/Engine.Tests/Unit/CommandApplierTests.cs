using Ecs.Protocol.V1;
using Engine.Coordinator;
using Google.Protobuf;
using Testing.V1;

namespace Engine.Tests.Unit;

/// <summary>
/// Structural commands are the only way the world's topology changes, and the applier
/// is where a command meets the schema registry.
/// </summary>
public class CommandApplierTests : WorldFixture
{
    private CommandApplier Applier => new(World, Schemas);

    private static ComponentValue Value(IMessage component, ulong hash) => new()
    {
        Type = new ComponentTypeRef { LogicalName = component.Descriptor.FullName, SchemaHash = hash },
        Payload = component.ToByteString(),
    };

    private ComponentValue Value(IMessage component)
    {
        Assert.True(Schemas.TryGet(component.Descriptor.FullName, out var type));
        return Value(component, type.SchemaHash);
    }

    [Fact]
    public void Spawn_CreatesAnEntityCarryingItsComponents()
    {
        var position = Bind(TestPosition.Descriptor);

        var spawned = Applier.Apply([new StructuralCommand
        {
            Spawn = new SpawnEntity { Components = { Value(new TestPosition { X = 1.5f }) } },
        }]);

        var entity = Assert.Single(spawned);
        Assert.True(World.IsAlive(entity));
        Assert.Equal(1.5f, TestPosition.Parser.ParseFrom(World.GetComponent(entity, position)).X);
    }

    [Fact]
    public void Despawn_RemovesTheEntity()
    {
        var entity = World.AllocateEntity();

        Applier.Apply([new StructuralCommand { Despawn = new DespawnEntity { Entity = entity } }]);

        Assert.False(World.IsAlive(entity));
    }

    [Fact]
    public void Add_AttachesToAnEntity()
    {
        var position = Bind(TestPosition.Descriptor);
        var entity = World.AllocateEntity();

        Applier.Apply([new StructuralCommand
        {
            Add = new AddComponent
            {
                Target = new CommandTarget { Entity = entity },
                Component = Value(new TestPosition { X = 2f }),
            },
        }]);

        Assert.NotNull(World.GetComponent(entity, position));
    }

    [Fact]
    public void Add_TargetingAComponentTypeCreatesTheTypeEntity()
    {
        var position = Bind(TestPosition.Descriptor);
        var setting = Bind(TestSetting.Descriptor);

        Applier.Apply([new StructuralCommand
        {
            Add = new AddComponent
            {
                Target = new CommandTarget { ComponentType = "testing.v1.TestPosition" },
                Component = Value(new TestSetting()),
            },
        }]);

        var typeEntity = World.FindTypeEntity(position);
        Assert.NotNull(typeEntity);
        Assert.True(World.Has(typeEntity.Value, setting));
    }

    [Fact]
    public void Remove_DetachesTheComponent()
    {
        var position = Bind(TestPosition.Descriptor);
        var entity = World.AllocateEntity();
        World.SetComponent(entity, position, []);

        Assert.True(Schemas.TryGet(position, out var type));
        Applier.Apply([new StructuralCommand
        {
            Remove = new RemoveComponent
            {
                Target = new CommandTarget { Entity = entity },
                Type = type.ToRef(),
            },
        }]);

        Assert.False(World.Has(entity, position));
    }

    [Fact]
    public void ACommandForAnUnregisteredTypeIsIgnored()
    {
        var spawned = Applier.Apply([new StructuralCommand
        {
            Spawn = new SpawnEntity { Components = { Value(new TestPosition { X = 1f }, hash: 1234) } },
        }]);

        // The entity is still created; only the component the world cannot vouch for
        // is dropped.
        var entity = Assert.Single(spawned);
        Assert.Empty(World.ComponentsOf(entity));
    }

    [Fact]
    public void ACommandQuotingTheWrongSchemaHashIsIgnored()
    {
        var position = Bind(TestPosition.Descriptor);
        var entity = World.AllocateEntity();

        Applier.Apply([new StructuralCommand
        {
            Add = new AddComponent
            {
                Target = new CommandTarget { Entity = entity },
                Component = Value(new TestPosition { X = 1f }, hash: 0xbadbadbad),
            },
        }]);

        Assert.False(World.Has(entity, position));
    }

    [Fact]
    public void APayloadThatIsNotTheDeclaredTypeIsIgnored()
    {
        var position = Bind(TestPosition.Descriptor);
        var entity = World.AllocateEntity();
        Assert.True(Schemas.TryGet(position, out var type));

        Applier.Apply([new StructuralCommand
        {
            Add = new AddComponent
            {
                Target = new CommandTarget { Entity = entity },
                Component = new ComponentValue
                {
                    Type = type.ToRef(),
                    // Field 1 of TestPosition is a float, so a varint here is not it.
                    Payload = ByteString.CopyFrom(0x08, 0x01),
                },
            },
        }]);

        Assert.False(World.Has(entity, position));
    }

    [Fact]
    public void ADespawnOfSomethingAlreadyGoneIsHarmless()
    {
        Applier.Apply([new StructuralCommand { Despawn = new DespawnEntity { Entity = 4242 } }]);
    }
}
