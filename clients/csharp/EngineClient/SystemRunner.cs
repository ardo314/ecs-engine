using NATS.Client.Core;

namespace EngineClient;

/// <summary>
/// Connects to the engine coordinator via NATS and runs a system function each tick.
/// </summary>
public class SystemRunner
{
    private readonly string _systemName;
    private readonly NatsConnection _nats;

    public SystemRunner(string systemName, NatsConnection nats)
    {
        _systemName = systemName;
        _nats = nats;
    }

    // TODO: Implement system registration, tick subscription, and component data handling.
}
