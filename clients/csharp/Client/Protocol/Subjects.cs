namespace Engine.Core;

/// <summary>
/// The NATS subject hierarchy. Routing metadata belongs in subjects and headers,
/// never in a payload, so this is the single place a subject name is written down.
/// </summary>
internal static class Subjects
{
    public const string Prefix = "engine";

    /// <summary>Request/reply. A system declaring the schemas it uses, before anything else.</summary>
    public const string SchemaRegister = $"{Prefix}.schema.register";

    /// <summary>A system announcing itself and its queries. Repeated until first invoked.</summary>
    public const string SystemRegister = $"{Prefix}.system.register";

    public const string SystemUnregister = $"{Prefix}.system.unregister";

    /// <summary>The coordinator handing a system its tick-scoped lease and data.</summary>
    public static string SystemInvoke(string systemName) => $"{Prefix}.system.invoke.{systemName}";

    /// <summary>A system returning writes and structural commands under its lease.</summary>
    public const string SystemResult = $"{Prefix}.system.result";

    /// <summary>The coordinator refusing a result. Addressed to the offending instance.</summary>
    public static string SystemRejected(string instanceId) => $"{Prefix}.system.rejected.{instanceId}";

    /// <summary>Request/reply. Structural commands submitted outside any tick.</summary>
    public const string WorldCommand = $"{Prefix}.world.command";

    public const string QuerySystems = $"{Prefix}.world.query.systems";
    public const string QueryEntities = $"{Prefix}.world.query.entities";

    public const string WatchSubscribe = $"{Prefix}.world.watch.subscribe";
    public const string WatchCancel = $"{Prefix}.world.watch.cancel";

    public static string WatchData(string watchId) => $"{Prefix}.world.watch.data.{watchId}";
}
