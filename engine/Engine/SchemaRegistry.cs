using Ecs.Protocol;
using Ecs.Protocol.V1;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace Engine.Coordinator;

/// <summary>
/// A component type the world knows about at runtime, without having been compiled
/// against it.
/// </summary>
public sealed record RegisteredType(
    uint TypeId,
    string LogicalName,
    ulong SchemaHash,
    ByteString FileDescriptorSet,
    MessageDescriptor Descriptor)
{
    public ComponentTypeRef ToRef() => new() { LogicalName = LogicalName, SchemaHash = SchemaHash };

    public ComponentTypeInfo ToInfo(ulong typeEntity) => new()
    {
        TypeId = TypeId,
        Type = ToRef(),
        FileDescriptorSet = FileDescriptorSet,
        TypeEntity = typeEntity,
    };
}

/// <summary>
/// Binds component schemas to the dense integer ids the rest of the coordinator works
/// with.
/// </summary>
/// <remarks>
/// This is the boundary between the two notions of typing in the engine. Above it,
/// systems deal in <c>movement.v1.Position</c>. Below it, the scheduler, the storage
/// and the lease checker deal only in <c>17</c>. Nothing under this line needs to be
/// rebuilt when a new component type appears.
///
/// Identity is exact: a logical name is bound to one schema hash for the lifetime of
/// the world. Compatible schema evolution is deliberately not modelled — until there
/// is a concrete need for it, a changed schema is a different type and saying so
/// loudly beats guessing.
/// </remarks>
public sealed class SchemaRegistry
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, RegisteredType> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, RegisteredType> _byId = new();
    private uint _nextTypeId = 1;
    private int _version;

    public SchemaRegistry()
    {
        // The coordinator reads exactly two component types itself. Registering them up
        // front means they exist before any system connects, and that a system
        // declaring them gets the same ids back rather than a mismatch.
        SelfRegister(Ecs.V1.ComponentInfo.Descriptor);
        SelfRegister(Ecs.V1.ComponentSchema.Descriptor);
    }

    /// <summary>Type id of <c>ecs.v1.ComponentInfo</c>, which marks an entity as a type entity.</summary>
    public uint ComponentInfoTypeId { get; private set; }

    /// <summary>Type id of <c>ecs.v1.ComponentSchema</c>.</summary>
    public uint ComponentSchemaTypeId { get; private set; }

    /// <summary>Bumped whenever a new type is bound, so watchers can send deltas.</summary>
    public int Version
    {
        get { lock (_gate) return _version; }
    }

    public RegisterSchemasResponse Register(RegisterSchemasRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var response = new RegisterSchemasResponse();
        foreach (var declaration in request.Declarations)
        {
            if (TryBind(declaration, out var binding, out var rejection))
                response.Bindings.Add(binding);
            else
                response.Rejections.Add(rejection);
        }
        return response;
    }

    private bool TryBind(
        ComponentTypeDeclaration declaration,
        out SchemaBinding binding,
        out SchemaRejection rejection)
    {
        binding = null!;
        rejection = null!;

        var declared = declaration.Type ?? new ComponentTypeRef();
        var name = declared.LogicalName;

        if (string.IsNullOrWhiteSpace(name))
        {
            rejection = Reject(declared, SchemaRejectionReason.InvalidDescriptor,
                "Declaration has no logical name.");
            return false;
        }

        MessageDescriptor descriptor;
        try
        {
            descriptor = Descriptors.Resolve(declaration.FileDescriptorSet, name);
        }
        catch (InvalidDescriptorSetException ex)
        {
            rejection = Reject(declared, SchemaRejectionReason.InvalidDescriptor, ex.Message);
            return false;
        }

        // The hash is recomputed rather than trusted: a declaration is only as good as
        // the descriptors that came with it.
        var actualHash = SchemaHash.Of(descriptor);
        if (actualHash != declared.SchemaHash)
        {
            rejection = Reject(declared, SchemaRejectionReason.HashNotDerivedFromDescriptor,
                $"Declared hash 0x{declared.SchemaHash:x16} but the descriptors hash to 0x{actualHash:x16}.");
            return false;
        }

        lock (_gate)
        {
            if (_byName.TryGetValue(name, out var existing))
            {
                if (existing.SchemaHash != actualHash)
                {
                    rejection = Reject(declared, SchemaRejectionReason.HashMismatch,
                        $"'{name}' is already bound to a different schema.");
                    rejection.RegisteredSchemaHash = existing.SchemaHash;
                    return false;
                }

                binding = new SchemaBinding { Type = existing.ToRef(), TypeId = existing.TypeId };
                return true;
            }

            var registered = new RegisteredType(
                _nextTypeId++, name, actualHash, declaration.FileDescriptorSet, descriptor);
            _byName[name] = registered;
            _byId[registered.TypeId] = registered;
            _version++;

            Console.WriteLine(
                $"[Schemas] Bound '{name}' (0x{actualHash:x16}) to type id {registered.TypeId}");

            binding = new SchemaBinding { Type = registered.ToRef(), TypeId = registered.TypeId };
            return true;
        }
    }

    public bool TryGet(uint typeId, out RegisteredType type)
    {
        lock (_gate) return _byId.TryGetValue(typeId, out type!);
    }

    public bool TryGet(string logicalName, out RegisteredType type)
    {
        lock (_gate) return _byName.TryGetValue(logicalName, out type!);
    }

    /// <summary>Resolves logical names to type ids, skipping names not yet registered.</summary>
    public HashSet<uint> ResolveIds(IEnumerable<string> logicalNames)
    {
        var ids = new HashSet<uint>();
        lock (_gate)
        {
            foreach (var name in logicalNames)
            {
                if (_byName.TryGetValue(name, out var type))
                    ids.Add(type.TypeId);
            }
        }
        return ids;
    }

    public List<RegisteredType> All()
    {
        lock (_gate) return [.. _byId.Values];
    }

    public string NameOf(uint typeId) =>
        TryGet(typeId, out var type) ? type.LogicalName : $"<unregistered:{typeId}>";

    private void SelfRegister(MessageDescriptor descriptor)
    {
        var registered = new RegisteredType(
            _nextTypeId++,
            descriptor.FullName,
            SchemaHash.Of(descriptor),
            Descriptors.FileDescriptorSetFor(descriptor),
            descriptor);

        _byName[registered.LogicalName] = registered;
        _byId[registered.TypeId] = registered;
        _version++;

        if (descriptor == Ecs.V1.ComponentInfo.Descriptor) ComponentInfoTypeId = registered.TypeId;
        if (descriptor == Ecs.V1.ComponentSchema.Descriptor) ComponentSchemaTypeId = registered.TypeId;
    }

    private static SchemaRejection Reject(ComponentTypeRef type, SchemaRejectionReason reason, string detail)
    {
        Console.WriteLine($"[Schemas] Rejected '{type.LogicalName}': {detail}");
        return new SchemaRejection { Type = type, Reason = reason, Detail = detail };
    }
}
