using System.Text.Json;

namespace NovaInstaller.Tests;

public class StackPlannerTests
{
    private static InstallerOptions Options(Dictionary<string, string>? env = null)
    {
        env ??= [];
        return InstallerOptions.FromEnvironment(key => env.GetValueOrDefault(key));
    }

    [Fact]
    public void Plan_WithoutSystems_InstallsNatsEngineAndEditor()
    {
        var apps = StackPlanner.Plan(Options());

        Assert.Equal(
            ["ecs-nats", "ecs-engine", "ecs-editor-api", "ecs-editor"],
            apps.Select(a => a.Name));
    }

    [Fact]
    public void Plan_PlacesNatsBeforeEveryConsumer()
    {
        var apps = StackPlanner.Plan(Options(new Dictionary<string, string>
        {
            ["ECS_SYSTEM_IMAGES"] = "ghcr.io/acme/movement-system:1.0"
        }));

        Assert.Equal("ecs-nats", apps[0].Name);
        Assert.Equal("ecs-engine", apps[1].Name);
    }

    [Fact]
    public void Plan_WithEditorDisabled_SkipsEditorApps()
    {
        var apps = StackPlanner.Plan(Options(new Dictionary<string, string>
        {
            ["ECS_INSTALL_EDITOR"] = "false"
        }));

        Assert.Equal(["ecs-nats", "ecs-engine"], apps.Select(a => a.Name));
    }

    [Fact]
    public void Plan_AppendsOneAppPerSystemImage()
    {
        var apps = StackPlanner.Plan(Options(new Dictionary<string, string>
        {
            ["ECS_INSTALL_EDITOR"] = "false",
            ["ECS_SYSTEM_IMAGES"] = "ghcr.io/acme/movement-system:1.0,nova=ghcr.io/acme/nova-systems:2.0"
        }));

        Assert.Equal(["ecs-nats", "ecs-engine", "ecs-movement-system", "ecs-nova"], apps.Select(a => a.Name));
        Assert.Equal("ghcr.io/acme/nova-systems:2.0", apps[3].ContainerImage.Image);
    }

    [Fact]
    public void Plan_GivesHeadlessAppsAHealthPortMatchingTheirPort()
    {
        var apps = StackPlanner.Plan(Options(new Dictionary<string, string>
        {
            ["ECS_INSTALL_EDITOR"] = "false",
            ["ECS_SYSTEM_IMAGES"] = "ghcr.io/acme/movement-system:1.0"
        }));

        foreach (var app in apps.Where(a => a.Name != "ecs-nats"))
        {
            var healthPort = app.Environment!.Single(e => e.Name == "HEALTH_PORT").Value;
            Assert.Equal(app.Port!.Value.ToString(), healthPort);
            Assert.Equal("/health", app.HealthPath);
        }
    }

    [Fact]
    public void Plan_PointsEveryConsumerAtTheNatsApp()
    {
        var apps = StackPlanner.Plan(Options(new Dictionary<string, string>
        {
            ["ECS_SYSTEM_IMAGES"] = "ghcr.io/acme/movement-system:1.0"
        }));

        var natsUrls = apps
            .Where(a => a.Environment is not null)
            .SelectMany(a => a.Environment!)
            .Where(e => e.Name == "NATS_URL")
            .Select(e => e.Value);

        Assert.All(natsUrls, url => Assert.Equal("nats://ecs-nats:4222", url));
    }

    [Fact]
    public void Plan_UsesExplicitNatsUrlWhenProvided()
    {
        var apps = StackPlanner.Plan(Options(new Dictionary<string, string>
        {
            ["ECS_INSTALL_EDITOR"] = "false",
            ["ECS_NATS_URL"] = "nats://platform-nats:4222"
        }));

        var engine = apps.Single(a => a.Name == "ecs-engine");
        Assert.Equal("nats://platform-nats:4222", engine.Environment!.Single(e => e.Name == "NATS_URL").Value);
    }

    [Fact]
    public void Plan_WiresEditorFrontendToBackendPublicPath()
    {
        var apps = StackPlanner.Plan(Options(new Dictionary<string, string> { ["NOVA_CELL"] = "cell" }));

        var frontend = apps.Single(a => a.Name == "ecs-editor");
        Assert.Equal("/cell/ecs-editor", frontend.Environment!.Single(e => e.Name == "BASE_PATH").Value);
        Assert.Equal("/cell/ecs-editor-api", frontend.Environment!.Single(e => e.Name == "EDITOR_BACKEND_URL").Value);

        var backend = apps.Single(a => a.Name == "ecs-editor-api");
        Assert.Equal("/cell/ecs-editor-api", backend.Environment!.Single(e => e.Name == "BASE_PATH").Value);
    }

    [Fact]
    public void Plan_GivesNatsPersistentStorageAndItsMonitoringPort()
    {
        var nats = StackPlanner.Plan(Options()).Single(a => a.Name == "ecs-nats");

        Assert.Equal(8222, nats.Port);
        Assert.Equal("/healthz", nats.HealthPath);
        Assert.Equal("/data", nats.Storage!.MountPath);
    }

    [Fact]
    public void Plan_AppliesRegistryCredentialsWhenBothPartsAreSet()
    {
        var apps = StackPlanner.Plan(Options(new Dictionary<string, string>
        {
            ["ECS_INSTALL_EDITOR"] = "false",
            ["ECS_REGISTRY_USER"] = "robot",
            ["ECS_REGISTRY_PASSWORD"] = "secret"
        }));

        var credentials = apps.Single(a => a.Name == "ecs-engine").ContainerImage.Credentials;
        Assert.Equal("ghcr.io", credentials!.Registry);
        Assert.Equal("robot", credentials.User);
    }

    [Fact]
    public void Plan_OmitsCredentialsWhenOnlyUserIsSet()
    {
        var apps = StackPlanner.Plan(Options(new Dictionary<string, string>
        {
            ["ECS_INSTALL_EDITOR"] = "false",
            ["ECS_REGISTRY_USER"] = "robot"
        }));

        Assert.Null(apps.Single(a => a.Name == "ecs-engine").ContainerImage.Credentials);
    }

    [Fact]
    public void Manifest_SerializesToTheApiSnakeCaseShape()
    {
        var engine = StackPlanner.Plan(Options()).Single(a => a.Name == "ecs-engine");

        var json = JsonSerializer.Serialize(engine, NovaJson.Options);

        Assert.Contains("\"app_icon\":\"app_icon.png\"", json);
        Assert.Contains("\"container_image\":{\"image\":", json);
        Assert.Contains("\"health_path\":\"/health\"", json);
        Assert.DoesNotContain("\"storage\"", json);
    }
}
