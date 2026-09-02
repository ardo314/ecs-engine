using Client;
using Engine.Core;
using Movement.V1;

namespace Examples;

public class MovementSystem : SystemBase
{
    private readonly EntityQuery _q;
    private ulong _tickCount;

    public MovementSystem()
    {
        _q = NewQuery()
            .With(Query.ReadWrite<Position>())
            .With(Query.ReadOnly<Velocity>());
    }

    protected override Task OnUpdateAsync()
    {
        foreach (var entity in _q.Entities)
        {
            var pos = _q.Get<Position>(entity);
            var vel = _q.Get<Velocity>(entity);
            _q.Set(entity, new Position
            {
                X = pos.X + vel.X * DeltaTime,
                Y = pos.Y + vel.Y * DeltaTime,
                Z = pos.Z + vel.Z * DeltaTime
            });
        }

        _tickCount++;
        if (_tickCount % 20 == 0 && _q.Entities.Count > 0)
        {
            var e = _q.Entities[0];
            var p = _q.Get<Position>(e);
            Console.WriteLine($"[Movement] Tick {_tickCount} | Entity {e.Id} Position: ({p.X:F2}, {p.Y:F2}, {p.Z:F2})");
        }

        return Task.CompletedTask;
    }
}
