using Ecs.Protocol;
using Ecs.Protocol.V1;

namespace Engine.Coordinator;

/// <summary>
/// Applies structural commands to the world at a synchronisation point.
/// </summary>
/// <remarks>
/// Nothing mutates the world's topology while systems are iterating it. Commands are
/// buffered, whether they came back inside a <c>SystemResult</c> or were submitted out
/// of band, and played here in a single deterministic pass at the top of a tick. That
/// removes the entire class of problems around invalidated slices, entities that vanish
/// mid-iteration, and two systems disagreeing about what exists.
/// </remarks>
public sealed class CommandApplier
{
    private readonly WorldState _world;
    private readonly SchemaRegistry _schemas;

    public CommandApplier(WorldState world, SchemaRegistry schemas)
    {
        _world = world;
        _schemas = schemas;
    }

    /// <summary>Entities created by the most recent <see cref="Apply"/> pass.</summary>
    public List<ulong> Apply(IEnumerable<StructuralCommand> commands)
    {
        var spawned = new List<ulong>();

        foreach (var command in commands)
        {
            switch (command.CommandCase)
            {
                case StructuralCommand.CommandOneofCase.Spawn:
                    spawned.Add(Spawn(command.Spawn));
                    break;
                case StructuralCommand.CommandOneofCase.Despawn:
                    Despawn(command.Despawn);
                    break;
                case StructuralCommand.CommandOneofCase.Add:
                    Add(command.Add);
                    break;
                case StructuralCommand.CommandOneofCase.Remove:
                    Remove(command.Remove);
                    break;
                default:
                    Console.WriteLine("[Commands] Ignored a command with no body.");
                    break;
            }
        }

        return spawned;
    }

    private ulong Spawn(SpawnEntity spawn)
    {
        var entityId = _world.AllocateEntity();
        foreach (var value in spawn.Components)
        {
            if (TryAccept(value, out var type, out var payload))
                _world.SetComponent(entityId, type.TypeId, payload);
        }
        return entityId;
    }

    private void Despawn(DespawnEntity despawn)
    {
        if (_world.IsAlive(despawn.Entity))
            _world.DestroyEntity(despawn.Entity);
    }

    private void Add(AddComponent add)
    {
        if (ResolveTarget(add.Target) is not { } entityId) return;
        if (!TryAccept(add.Component, out var type, out var payload)) return;

        _world.SetComponent(entityId, type.TypeId, payload);
    }

    private void Remove(RemoveComponent remove)
    {
        if (ResolveTarget(remove.Target) is not { } entityId) return;
        if (!TryResolve(remove.Type, out var type)) return;

        _world.RemoveComponent(entityId, type.TypeId);
    }

    /// <summary>
    /// A command may target a concrete entity or a component type. Type entities are
    /// addressed by name and created on first use, so a system can describe a type in
    /// the same breath as it registers the schema.
    /// </summary>
    private ulong? ResolveTarget(CommandTarget? target)
    {
        switch (target?.TargetCase)
        {
            case CommandTarget.TargetOneofCase.Entity:
                return _world.IsAlive(target.Entity) ? target.Entity : null;

            case CommandTarget.TargetOneofCase.ComponentType:
                if (!_schemas.TryGet(target.ComponentType, out var type))
                {
                    Console.WriteLine(
                        $"[Commands] Cannot target unregistered component type '{target.ComponentType}'.");
                    return null;
                }
                return _world.GetOrCreateTypeEntity(type);

            default:
                return null;
        }
    }

    private bool TryAccept(ComponentValue? value, out RegisteredType type, out byte[] payload)
    {
        type = null!;
        payload = [];

        if (value is null || !TryResolve(value.Type, out type)) return false;

        payload = value.Payload.ToByteArray();
        if (PayloadValidator.IsValid(type.Descriptor, payload, out var error)) return true;

        Console.WriteLine($"[Commands] Rejected payload for '{type.LogicalName}': {error}");
        return false;
    }

    private bool TryResolve(ComponentTypeRef? reference, out RegisteredType type)
    {
        type = null!;
        if (reference is null) return false;

        if (!_schemas.TryGet(reference.LogicalName, out type))
        {
            Console.WriteLine(
                $"[Commands] Unknown component type '{reference.LogicalName}' — register its schema first.");
            return false;
        }

        if (type.SchemaHash != reference.SchemaHash)
        {
            Console.WriteLine(
                $"[Commands] Schema hash mismatch for '{reference.LogicalName}': " +
                $"world has 0x{type.SchemaHash:x16}, command carries 0x{reference.SchemaHash:x16}.");
            return false;
        }

        return true;
    }
}
