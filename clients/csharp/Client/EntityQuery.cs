using Ecs.Protocol;
using Ecs.Protocol.V1;
using Google.Protobuf;

namespace Engine.Core;

/// <summary>
/// A typed view over the entities a system was leased for this tick.
/// </summary>
/// <remarks>
/// Declared in the system constructor with <c>Query.Read&lt;T&gt;</c> and
/// <c>Query.Write&lt;T&gt;</c>, bound to dense type ids during the registration
/// handshake, then repopulated each tick from the invocation's component batches.
///
/// A system cannot look up an arbitrary entity: it sees exactly the slice its lease
/// covers. To dereference a reference component, declare a second query matching the
/// target entities and index it — both queries are filled from the same invocation in
/// the same tick, so that costs bandwidth rather than a round trip.
/// </remarks>
public sealed class EntityQuery
{
    // ── Declaration (system constructor) ────────────────────────

    private readonly List<ComponentAccess> _required = new();
    private readonly List<ComponentAccess> _optional = new();
    private readonly List<ComponentAccess> _excluded = new();
    private readonly List<ComponentAccess> _tags = new();
    private bool _frozen;

    // ── Binding (registration handshake) ────────────────────────

    private SchemaBindings _bindings = new();
    private readonly HashSet<uint> _declaredTypes = new();
    private readonly HashSet<uint> _writableTypes = new();
    private uint[] _requiredIds = [];
    private uint[] _optionalIds = [];
    private uint[] _excludedIds = [];
    private uint[] _tagIds = [];

    // ── Tick state ──────────────────────────────────────────────

    private readonly Dictionary<uint, Dictionary<ulong, byte[]>> _data = new();
    private readonly Dictionary<uint, uint[]> _resolvedTags = new();
    private readonly List<Entity> _matched = new();
    private readonly Dictionary<uint, Dictionary<ulong, byte[]>> _mutations = new();

    // ── Declaration ─────────────────────────────────────────────

    /// <summary>Adds a required component. An entity must have it to match.</summary>
    public EntityQuery With(ComponentAccess access)
    {
        ThrowIfFrozen();
        _required.Add(access);
        return this;
    }

    /// <summary>Adds optional components. An entity must have at least one to match.</summary>
    public EntityQuery WithAny(params ComponentAccess[] accesses)
    {
        ThrowIfFrozen();
        _optional.AddRange(accesses);
        return this;
    }

    /// <summary>Excludes entities carrying the component.</summary>
    public EntityQuery Without<T>() where T : IMessage<T>, new()
    {
        ThrowIfFrozen();
        _excluded.Add(Query.Read<T>());
        return this;
    }

    /// <summary>
    /// Joins through the type system: an entity matches when it carries at least one
    /// component whose <em>type entity</em> has <typeparamref name="TTag"/>. The matching
    /// types are resolved by the coordinator every tick, so a type introduced later is
    /// picked up without this query changing. Tag joins are read-only.
    /// </summary>
    public EntityQuery WithAnyTagged<TTag>() where TTag : IMessage<TTag>, new()
    {
        ThrowIfFrozen();
        _tags.Add(Query.Read<TTag>());
        return this;
    }

    // ── Binding ─────────────────────────────────────────────────

    /// <summary>Every schema this query needs the coordinator to know about.</summary>
    internal IEnumerable<ComponentTypeDeclaration> Declarations() =>
        _required.Concat(_optional).Concat(_excluded).Concat(_tags).Select(a => a.Declaration);

    internal IEnumerable<string> ReadNames() =>
        _required.Concat(_optional).Where(a => !a.IsWrite).Select(a => a.Name);

    internal IEnumerable<string> WriteNames() =>
        _required.Concat(_optional).Where(a => a.IsWrite).Select(a => a.Name);

    internal void Freeze() => _frozen = true;

    /// <summary>
    /// Resolves declared type names to the ids the coordinator assigned. Called once the
    /// registration handshake completes, before the first invocation.
    /// </summary>
    internal void Bind(SchemaBindings bindings)
    {
        _bindings = bindings;

        _requiredIds = [.. _required.Select(a => bindings.Require(a.Name))];
        _optionalIds = [.. _optional.Select(a => bindings.Require(a.Name))];
        _excludedIds = [.. _excluded.Select(a => bindings.Require(a.Name))];
        _tagIds = [.. _tags.Select(a => bindings.Require(a.Name))];

        _declaredTypes.Clear();
        _declaredTypes.UnionWith(_requiredIds);
        _declaredTypes.UnionWith(_optionalIds);

        _writableTypes.Clear();
        foreach (var access in _required.Concat(_optional).Where(a => a.IsWrite))
            _writableTypes.Add(bindings.Require(access.Name));
    }

    internal QueryDescriptor ToDescriptor()
    {
        var descriptor = new QueryDescriptor();

        for (var i = 0; i < _required.Count; i++)
            descriptor.Required.Add(new Ecs.Protocol.V1.ComponentAccess
            {
                TypeId = _requiredIds[i],
                Access = _required[i].Access,
            });

        for (var i = 0; i < _optional.Count; i++)
            descriptor.Optional.Add(new Ecs.Protocol.V1.ComponentAccess
            {
                TypeId = _optionalIds[i],
                Access = _optional[i].Access,
            });

        descriptor.Excluded.AddRange(_excludedIds);
        descriptor.Tagged.AddRange(_tagIds.Select(id => new TaggedAccess { TagTypeId = id }));
        return descriptor;
    }

    // ── Per-tick population ─────────────────────────────────────

    /// <summary>
    /// Rebuilds this query's view from the tick's invocation. The coordinator has already
    /// narrowed the slice to entities matching at least one of the system's queries; this
    /// applies each individual query's own filter.
    /// </summary>
    internal void Populate(IReadOnlyDictionary<uint, ComponentColumn> columns, SystemInvocation invocation)
    {
        _data.Clear();
        _matched.Clear();
        _mutations.Clear();
        _resolvedTags.Clear();

        var taggedTypes = new HashSet<uint>();
        foreach (var tagId in _tagIds)
        {
            var resolved = invocation.Tags.FirstOrDefault(t => t.TagTypeId == tagId);
            var types = resolved is null ? [] : resolved.TypeIds.ToArray();
            _resolvedTags[tagId] = types;
            taggedTypes.UnionWith(types);
        }

        foreach (var (typeId, column) in columns)
        {
            if (!_declaredTypes.Contains(typeId) &&
                !_excludedIds.Contains(typeId) &&
                !taggedTypes.Contains(typeId))
            {
                continue;
            }

            var byEntity = new Dictionary<ulong, byte[]>(column.Count);
            for (var i = 0; i < column.Count; i++)
            {
                if (column.Rows[i] is { } row) byEntity[column.Entities[i]] = row;
            }
            _data[typeId] = byEntity;
        }

        HashSet<ulong>? candidates = null;

        foreach (var typeId in _requiredIds)
        {
            if (!_data.TryGetValue(typeId, out var byEntity)) return;

            if (candidates is null) candidates = [.. byEntity.Keys];
            else candidates.IntersectWith(byEntity.Keys);
        }

        foreach (var tagId in _tagIds)
        {
            var withTag = new HashSet<ulong>();
            foreach (var typeId in _resolvedTags[tagId])
            {
                if (_data.TryGetValue(typeId, out var byEntity)) withTag.UnionWith(byEntity.Keys);
            }

            if (candidates is null) candidates = withTag;
            else candidates.IntersectWith(withTag);
        }

        if (candidates is null) return;

        if (_optionalIds.Length > 0)
        {
            candidates.RemoveWhere(entity =>
                !_optionalIds.Any(id => _data.TryGetValue(id, out var d) && d.ContainsKey(entity)));
        }

        foreach (var typeId in _excludedIds)
        {
            if (_data.TryGetValue(typeId, out var byEntity)) candidates.ExceptWith(byEntity.Keys);
        }

        foreach (var entity in candidates) _matched.Add(new Entity(entity));
    }

    // ── Data access ─────────────────────────────────────────────

    /// <summary>Entities matching this query for the current tick.</summary>
    public IReadOnlyList<Entity> Entities => _matched;

    public T Get<T>(Entity entity) where T : IMessage<T>, new() =>
        TryGet<T>(entity, out var component)
            ? component
            : throw new KeyNotFoundException(
                $"Entity {entity.Id} has no {ComponentType<T>.Name} in this query.");

    public bool TryGet<T>(Entity entity, out T component) where T : IMessage<T>, new()
    {
        if (_bindings.TryGetId(ComponentType<T>.Name, out var typeId) &&
            _data.TryGetValue(typeId, out var byEntity) &&
            byEntity.TryGetValue(entity.Id, out var payload))
        {
            component = new T();
            component.MergeFrom(payload);
            return true;
        }

        component = default!;
        return false;
    }

    public bool Has<T>(Entity entity) where T : IMessage<T>, new() =>
        _bindings.TryGetId(ComponentType<T>.Name, out var typeId) &&
        _data.TryGetValue(typeId, out var byEntity) &&
        byEntity.ContainsKey(entity.Id);

    /// <summary>
    /// Buffers a component write. Throws unless the type was declared with
    /// <see cref="Query.Write{T}"/> — the client half of the borrow check, so a mistake
    /// surfaces here rather than as a rejected result a network hop later.
    /// </summary>
    public void Set<T>(Entity entity, T component) where T : IMessage<T>, new()
    {
        var typeId = _bindings.Require(ComponentType<T>.Name);

        if (!_writableTypes.Contains(typeId))
            throw new InvalidOperationException(
                $"Cannot write {ComponentType<T>.Name}: this query declared it read-only.");

        if (!_mutations.TryGetValue(typeId, out var byEntity))
        {
            byEntity = new Dictionary<ulong, byte[]>();
            _mutations[typeId] = byEntity;
        }
        byEntity[entity.Id] = component.ToByteArray();
    }

    // ── Tag joins ───────────────────────────────────────────────

    /// <summary>The component types carrying <typeparamref name="TTag"/> this tick.</summary>
    public IReadOnlyList<string> TaggedTypeNames<TTag>() where TTag : IMessage<TTag>, new()
    {
        if (!_bindings.TryGetId(ComponentType<TTag>.Name, out var tagId) ||
            !_resolvedTags.TryGetValue(tagId, out var types))
        {
            return [];
        }

        return [.. types.Select(_bindings.NameOf)];
    }

    /// <summary>
    /// The components on <paramref name="entity"/> whose type carries
    /// <typeparamref name="TTag"/>. Payloads stay raw because the concrete types are only
    /// known at runtime — decode one with <see cref="TaggedComponent.As{T}"/> once its
    /// name identifies something you know.
    /// </summary>
    public IEnumerable<TaggedComponent> GetTagged<TTag>(Entity entity) where TTag : IMessage<TTag>, new()
    {
        if (!_bindings.TryGetId(ComponentType<TTag>.Name, out var tagId) ||
            !_resolvedTags.TryGetValue(tagId, out var types))
        {
            yield break;
        }

        foreach (var typeId in types)
        {
            if (_data.TryGetValue(typeId, out var byEntity) &&
                byEntity.TryGetValue(entity.Id, out var payload))
            {
                yield return new TaggedComponent(_bindings.NameOf(typeId), payload);
            }
        }
    }

    // ── Tuple iteration ─────────────────────────────────────────

    public IEnumerable<(Entity Entity, T1 C1)> Each<T1>()
        where T1 : IMessage<T1>, new()
    {
        foreach (var entity in _matched)
        {
            if (TryGet<T1>(entity, out var c1)) yield return (entity, c1);
        }
    }

    public IEnumerable<(Entity Entity, T1 C1, T2 C2)> Each<T1, T2>()
        where T1 : IMessage<T1>, new()
        where T2 : IMessage<T2>, new()
    {
        foreach (var entity in _matched)
        {
            if (TryGet<T1>(entity, out var c1) && TryGet<T2>(entity, out var c2))
                yield return (entity, c1, c2);
        }
    }

    public IEnumerable<(Entity Entity, T1 C1, T2 C2, T3 C3)> Each<T1, T2, T3>()
        where T1 : IMessage<T1>, new()
        where T2 : IMessage<T2>, new()
        where T3 : IMessage<T3>, new()
    {
        foreach (var entity in _matched)
        {
            if (TryGet<T1>(entity, out var c1) && TryGet<T2>(entity, out var c2) &&
                TryGet<T3>(entity, out var c3))
            {
                yield return (entity, c1, c2, c3);
            }
        }
    }

    public IEnumerable<(Entity Entity, T1 C1, T2 C2, T3 C3, T4 C4)> Each<T1, T2, T3, T4>()
        where T1 : IMessage<T1>, new()
        where T2 : IMessage<T2>, new()
        where T3 : IMessage<T3>, new()
        where T4 : IMessage<T4>, new()
    {
        foreach (var entity in _matched)
        {
            if (TryGet<T1>(entity, out var c1) && TryGet<T2>(entity, out var c2) &&
                TryGet<T3>(entity, out var c3) && TryGet<T4>(entity, out var c4))
            {
                yield return (entity, c1, c2, c3, c4);
            }
        }
    }

    // ── Flush ───────────────────────────────────────────────────

    /// <summary>Encodes and clears the buffered writes for this tick's result.</summary>
    internal List<ComponentBatch> FlushWrites()
    {
        var batches = new List<ComponentBatch>(_mutations.Count);

        foreach (var (typeId, byEntity) in _mutations)
        {
            if (byEntity.Count == 0) continue;

            batches.Add(ComponentBatchCodecs.Encode(new ComponentColumn(
                typeId,
                [.. byEntity.Keys],
                [.. byEntity.Values])));
        }

        _mutations.Clear();
        return batches;
    }

    private void ThrowIfFrozen()
    {
        if (_frozen)
            throw new InvalidOperationException(
                "Queries must be declared in the system constructor, before it joins a world.");
    }
}

/// <summary>
/// A component reached through a tag join, whose concrete type is only known at runtime.
/// </summary>
public readonly record struct TaggedComponent(string TypeName, byte[] Payload)
{
    /// <summary>Decodes the payload as <typeparamref name="T"/>, or null if it is a different type.</summary>
    public T? As<T>() where T : class, IMessage<T>, new()
    {
        if (TypeName != ComponentType<T>.Name) return null;

        var component = new T();
        component.MergeFrom(Payload);
        return component;
    }
}
