namespace NovaInstaller;

/// <summary>
/// Turns installer options into the ordered list of NOVA apps that make up the
/// ECS stack. Order matters: the coordinator goes in before anything that talks
/// to it. NATS is provided by the NOVA instance, so no broker is installed.
/// </summary>
public static class StackPlanner
{
    public const string IconPath = "app_icon.png";

    public static IReadOnlyList<AppManifest> Plan(InstallerOptions options)
    {
        var apps = new List<AppManifest> { Engine(options) };

        if (options.InstallEditor)
        {
            apps.Add(EditorBackend(options));
            apps.Add(EditorFrontend(options));
        }

        apps.AddRange(options.SystemImages.Select(system => System(options, system)));
        return apps;
    }

    private static AppManifest Engine(InstallerOptions options) => new()
    {
        Name = $"{options.AppPrefix}-engine",
        AppIcon = IconPath,
        ContainerImage = Image(options, options.EngineImage),
        Port = 8080,
        HealthPath = "/health",
        Environment =
        [
            .. NatsUrl(options),
            new EnvVar { Name = "TICK_RATE", Value = options.TickRate.ToString() },
            new EnvVar { Name = "HEALTH_PORT", Value = "8080" }
        ]
    };

    private static AppManifest EditorBackend(InstallerOptions options)
    {
        var name = $"{options.AppPrefix}-editor-api";
        return new AppManifest
        {
            Name = name,
            AppIcon = IconPath,
            ContainerImage = Image(options, options.EditorBackendImage),
            Port = 5000,
            HealthPath = "/health",
            Environment =
            [
                .. NatsUrl(options),
                new EnvVar { Name = "ASPNETCORE_URLS", Value = "http://+:5000" },
                new EnvVar { Name = "BASE_PATH", Value = PublicPath(options, name) }
            ]
        };
    }

    private static AppManifest EditorFrontend(InstallerOptions options)
    {
        var name = $"{options.AppPrefix}-editor";
        return new AppManifest
        {
            Name = name,
            AppIcon = IconPath,
            ContainerImage = Image(options, options.EditorFrontendImage),
            Port = 80,
            HealthPath = $"{PublicPath(options, name)}/config.js",
            Environment =
            [
                new EnvVar { Name = "BASE_PATH", Value = PublicPath(options, name) },
                // Path-only: config.ts resolves it against the page origin.
                new EnvVar { Name = "EDITOR_BACKEND_URL", Value = PublicPath(options, $"{options.AppPrefix}-editor-api") }
            ]
        };
    }

    private static AppManifest System(InstallerOptions options, SystemImage system) => new()
    {
        Name = $"{options.AppPrefix}-{system.Name}",
        AppIcon = IconPath,
        ContainerImage = Image(options, system.Image),
        Port = 8080,
        HealthPath = "/health",
        Environment =
        [
            .. NatsUrl(options),
            new EnvVar { Name = "HEALTH_PORT", Value = "8080" }
        ]
    };

    /// <summary>Omitted unless overridden, so containers fall back to NOVA's NATS_BROKER.</summary>
    private static EnvVar[] NatsUrl(InstallerOptions options) =>
        options.NatsUrl is null ? [] : [new EnvVar { Name = "NATS_URL", Value = options.NatsUrl }];

    /// <summary>The URL prefix NOVA serves an app under.</summary>
    private static string PublicPath(InstallerOptions options, string appName) => $"/{options.Cell}/{appName}";

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
