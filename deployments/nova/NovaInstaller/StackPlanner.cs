namespace NovaInstaller;

/// <summary>
/// Turns installer options into the ordered list of NOVA apps that make up the
/// ECS stack. Order matters: the coordinator goes in before anything that talks
/// to it. NATS is provided by the NOVA instance, so no broker is installed.
/// </summary>
public static class StackPlanner
{
    // NOVA joins these onto the app's BASE_PATH, which already ends in a slash, so both
    // are relative: a leading slash makes the probe arrive as /<cell>/<app>//health.
    public const string IconPath = "app_icon.png";
    public const string ProbePath = "health";

    public static IReadOnlyList<AppManifest> Plan(InstallerOptions options)
    {
        var apps = new List<AppManifest> { Engine(options) };

        if (options.InstallEditor)
            apps.Add(Editor(options));

        apps.AddRange(options.SystemImages.Select(system => System(options, system)));
        return apps;
    }

    private static AppManifest Engine(InstallerOptions options) => new()
    {
        Name = $"{options.AppPrefix}-engine",
        AppIcon = IconPath,
        ContainerImage = Image(options, options.EngineImage),
        Port = HealthEndpoint.DefaultPort,
        HealthPath = ProbePath,
        Environment =
        [
            .. NatsUrl(options),
            new EnvVar { Name = "TICK_RATE", Value = options.TickRate.ToString() }
        ]
    };

    // One app: the editor serves its own UI and API from the same origin, so there is
    // no backend URL to wire up.
    private static AppManifest Editor(InstallerOptions options) => new()
    {
        Name = $"{options.AppPrefix}-editor",
        AppIcon = IconPath,
        ContainerImage = Image(options, options.EditorImage),
        Port = HealthEndpoint.DefaultPort,
        HealthPath = ProbePath,
        Environment = [.. NatsUrl(options)]
    };

    private static AppManifest System(InstallerOptions options, SystemImage system) => new()
    {
        Name = $"{options.AppPrefix}-{system.Name}",
        AppIcon = IconPath,
        ContainerImage = Image(options, system.Image),
        Port = HealthEndpoint.DefaultPort,
        HealthPath = ProbePath,
        Environment = [.. NatsUrl(options)]
    };

    /// <summary>Omitted unless overridden, so containers fall back to NOVA's NATS_BROKER.</summary>
    private static EnvVar[] NatsUrl(InstallerOptions options) =>
        options.NatsUrl is null ? [] : [new EnvVar { Name = "NATS_URL", Value = options.NatsUrl }];

    private static ContainerImage Image(InstallerOptions options, string image) => new()
    {
        Image = image,
        Credentials = options.RegistryUser is null || options.RegistryPassword is null
            ? null
            : new RegistryCredentials
            {
                Registry = image.Split('/')[0],
                User = options.RegistryUser,
                Password = options.RegistryPassword
            }
    };
}
