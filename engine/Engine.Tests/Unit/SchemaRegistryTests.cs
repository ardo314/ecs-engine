using Ecs.Protocol;
using Ecs.Protocol.V1;
using Engine.Coordinator;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Testing.V1;

namespace Engine.Tests.Unit;

/// <summary>
/// Registration is where the world decides what a component type <em>is</em>. It binds
/// a dense id, and it refuses anything it cannot verify from the descriptors it was
/// handed.
/// </summary>
public class SchemaRegistryTests
{
    private static ComponentTypeDeclaration Declare(MessageDescriptor descriptor) => new()
    {
        Type = new ComponentTypeRef
        {
            LogicalName = descriptor.FullName,
            SchemaHash = SchemaHash.Of(descriptor),
        },
        FileDescriptorSet = Descriptors.FileDescriptorSetFor(descriptor),
    };

    private static RegisterSchemasRequest Request(params ComponentTypeDeclaration[] declarations)
    {
        var request = new RegisterSchemasRequest();
        request.Declarations.AddRange(declarations);
        return request;
    }

    [Fact]
    public void TheEngineOwnComponentTypesAreBoundBeforeAnySystemConnects()
    {
        var registry = new SchemaRegistry();

        Assert.True(registry.TryGet("ecs.v1.ComponentInfo", out var info));
        Assert.Equal(registry.ComponentInfoTypeId, info.TypeId);
        Assert.True(registry.TryGet("ecs.v1.ComponentSchema", out _));
    }

    [Fact]
    public void Register_BindsADenseId()
    {
        var registry = new SchemaRegistry();

        var response = registry.Register(Request(Declare(TestPosition.Descriptor)));

        var binding = Assert.Single(response.Bindings);
        Assert.Equal("testing.v1.TestPosition", binding.Type.LogicalName);
        Assert.NotEqual(0u, binding.TypeId);
        Assert.Empty(response.Rejections);
    }

    [Fact]
    public void Register_IsIdempotentForTheSameSchema()
    {
        var registry = new SchemaRegistry();

        var first = registry.Register(Request(Declare(TestPosition.Descriptor)));
        var second = registry.Register(Request(Declare(TestPosition.Descriptor)));

        Assert.Equal(first.Bindings[0].TypeId, second.Bindings[0].TypeId);
    }

    [Fact]
    public void Register_GivesDifferentTypesDifferentIds()
    {
        var registry = new SchemaRegistry();

        var response = registry.Register(Request(
            Declare(TestPosition.Descriptor), Declare(TestVelocity.Descriptor)));

        Assert.Equal(2, response.Bindings.Count);
        Assert.NotEqual(response.Bindings[0].TypeId, response.Bindings[1].TypeId);
    }

    [Fact]
    public void Register_RefusesADeclaredHashTheDescriptorsDoNotProduce()
    {
        var registry = new SchemaRegistry();
        var declaration = Declare(TestPosition.Descriptor);
        declaration.Type.SchemaHash = 0xdeadbeef;

        var response = registry.Register(Request(declaration));

        var rejection = Assert.Single(response.Rejections);
        Assert.Equal(SchemaRejectionReason.HashNotDerivedFromDescriptor, rejection.Reason);
        Assert.Empty(response.Bindings);
    }

    [Fact]
    public void Register_RefusesASecondSchemaForAnAlreadyBoundName()
    {
        var registry = new SchemaRegistry();
        registry.Register(Request(Declare(TestPosition.Descriptor)));

        // Same name, but the descriptors describe a different shape — exactly what a
        // system built against a stale .proto would send.
        var impostor = Declare(TestVelocity.Descriptor);
        impostor.Type.LogicalName = "testing.v1.TestPosition";
        impostor.FileDescriptorSet = Descriptors.FileDescriptorSetFor(TestPosition.Descriptor);

        var response = registry.Register(Request(impostor));

        var rejection = Assert.Single(response.Rejections);
        Assert.Equal(SchemaRejectionReason.HashNotDerivedFromDescriptor, rejection.Reason);
    }

    [Fact]
    public void Register_ReportsTheBoundHashOnAMismatch()
    {
        var registry = new SchemaRegistry();
        registry.Register(Request(Declare(TestPosition.Descriptor)));

        // A type whose descriptors are self-consistent but whose name is already taken.
        var renamed = Declare(TestVelocity.Descriptor);
        var response = registry.Register(Request(renamed));
        Assert.Empty(response.Rejections);

        var conflicting = new ComponentTypeDeclaration
        {
            Type = new ComponentTypeRef
            {
                LogicalName = "testing.v1.TestPosition",
                SchemaHash = SchemaHash.Of(TestPosition.Descriptor),
            },
            FileDescriptorSet = ByteString.Empty,
        };

        var rejected = registry.Register(Request(conflicting));
        Assert.Equal(SchemaRejectionReason.InvalidDescriptor, Assert.Single(rejected.Rejections).Reason);
    }

    [Fact]
    public void Register_RefusesAnUnreadableDescriptorSet()
    {
        var registry = new SchemaRegistry();

        var response = registry.Register(Request(new ComponentTypeDeclaration
        {
            Type = new ComponentTypeRef { LogicalName = "testing.v1.TestPosition", SchemaHash = 1 },
            FileDescriptorSet = ByteString.CopyFrom(0xff, 0xff, 0xff, 0xff),
        }));

        Assert.Equal(SchemaRejectionReason.InvalidDescriptor, Assert.Single(response.Rejections).Reason);
    }

    [Fact]
    public void Version_ChangesOnlyWhenSomethingNewIsBound()
    {
        var registry = new SchemaRegistry();
        var before = registry.Version;

        registry.Register(Request(Declare(TestPosition.Descriptor)));
        var afterFirst = registry.Version;
        registry.Register(Request(Declare(TestPosition.Descriptor)));

        Assert.NotEqual(before, afterFirst);
        Assert.Equal(afterFirst, registry.Version);
    }

    [Fact]
    public void ResolveIds_SkipsNamesItHasNeverSeen()
    {
        var registry = new SchemaRegistry();
        registry.Register(Request(Declare(TestPosition.Descriptor)));

        var ids = registry.ResolveIds(["testing.v1.TestPosition", "testing.v1.Nope"]);

        Assert.Single(ids);
    }
}
