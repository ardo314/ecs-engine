using Ecs.Protocol.V1;
using Google.Protobuf;

namespace Engine.Core;

/// <summary>
/// A component type a query touches, and how. Produced by <see cref="Query"/>.
/// </summary>
/// <remarks>
/// This is the bridge between the two notions of typing. Authoring code writes
/// <c>Query.Read&lt;Position&gt;()</c> and gets compile-time safety from the generated
/// C# type; the SDK turns it into <c>{ component, schema_hash, access }</c>, which is
/// all the world ever sees. Nothing needs the world to have compiled <c>Position</c>.
/// </remarks>
public readonly record struct ComponentAccess(
    ComponentTypeRef Type,
    Access Access,
    ComponentTypeDeclaration Declaration)
{
    public string Name => Type.LogicalName;

    public bool IsWrite => Access == Access.Write;
}

/// <summary>
/// Declares component access in a query builder.
/// </summary>
public static class Query
{
    /// <summary>Read-only access. Two systems reading the same type run in parallel.</summary>
    public static ComponentAccess Read<T>() where T : IMessage<T>, new() =>
        new(ComponentType<T>.Ref, Access.Read, ComponentType<T>.Declaration);

    /// <summary>
    /// Read-write access. Conflicts with any other system reading or writing the type,
    /// so the scheduler puts them in different stages.
    /// </summary>
    public static ComponentAccess Write<T>() where T : IMessage<T>, new() =>
        new(ComponentType<T>.Ref, Access.Write, ComponentType<T>.Declaration);
}

/// <summary>
/// The dense type ids the coordinator bound this process's schemas to.
/// </summary>
/// <remarks>
/// Ids are assigned by the world, not chosen by the client, so they are only meaningful
/// after the registration handshake. Everything on the wire after that point — queries,
/// batches, leases — is expressed in them.
/// </remarks>
public sealed class SchemaBindings
{
    private readonly Dictionary<string, uint> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, string> _byId = new();

    public void Add(string logicalName, uint typeId)
    {
        _byName[logicalName] = typeId;
        _byId[typeId] = logicalName;
    }

    public bool TryGetId(string logicalName, out uint typeId) =>
        _byName.TryGetValue(logicalName, out typeId);

    public uint Require(string logicalName) =>
        _byName.TryGetValue(logicalName, out var typeId)
            ? typeId
            : throw new InvalidOperationException(
                $"Component type '{logicalName}' has no id — its schema was never registered. " +
                "Declare it in a query, or add a component of that type through a command buffer.");

    public uint Require<T>() where T : IMessage<T>, new() => Require(ComponentType<T>.Name);

    public string NameOf(uint typeId) =>
        _byId.TryGetValue(typeId, out var name) ? name : $"<unbound:{typeId}>";

    public int Count => _byName.Count;
}
