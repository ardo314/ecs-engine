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
    public void Plan_WithoutSystems_InstallsEngineAndEditor()
    {
        var apps = StackPlanner.Plan(Options());

        Assert.Equal(
            ["ecs-engine", "ecs-editor"],
            apps.Select(a => a.Name));
    }

    [Fact]
    public void Plan_NeverInstallsABroker()
    {
        var apps = StackPlanner.Plan(Options(new Dictionary<string, string>
        {
            ["ECS_SYSTEM_IMAGES"] = "ghcr.io/acme/movement-system:1.0"
        }));

        Assert.DoesNotContain(apps, a => a.Name.Contains("nats"));
    }

    [Fact]
    public void Plan_PlacesTheCoordinatorBeforeEveryConsumer()
    {
        var apps = StackPlanner.Plan(Options(new Dictionary<string, string>
        {
            ["ECS_SYSTEM_IMAGES"] = "ghcr.io/acme/movement-system:1.0"
        }));

        Assert.Equal("ecs-engine", apps[0].Name);
    }

    [Fact]
    public void Plan_WithEditorDisabled_SkipsEditorApps()
    {
        var apps = StackPlanner.Plan(Options(new Dictionary<string, string>
        {
            ["ECS_INSTALL_EDITOR"] = "false"
        }));

        Assert.Equal(["ecs-engine"], apps.Select(a => a.Name));
    }

    [Fact]
    public void Plan_AppendsOneAppPerSystemImage()
    {
        var apps = StackPlanner.Plan(Options(new Dictionary<string, string>
        {
            ["ECS_INSTALL_EDITOR"] = "false",
            ["ECS_SYSTEM_IMAGES"] = "ghcr.io/acme/movement-system:1.0,nova=ghcr.io/acme/nova-systems:2.0"
        }));

        Assert.Equal(["ecs-engine", "ecs-movement-system", "ecs-nova"], apps.Select(a => a.Name));
        Assert.Equal("ghcr.io/acme/nova-systems:2.0", apps[2].ContainerImage.Image);
    }

    [Fact]
    public void Plan_ServesEveryAppOnTheFixedHealthPort()
    {
        var apps = StackPlanner.Plan(Options(new Dictionary<string, string>
        {
            ["ECS_SYSTEM_IMAGES"] = "ghcr.io/acme/movement-system:1.0"
        }));

        foreach (var app in apps)
        {
            Assert.Equal(HealthEndpoint.DefaultPort, app.Port);
            Assert.Equal("health", app.HealthPath);
            Assert.DoesNotContain(app.Environment!, e => e.Name == "HEALTH_PORT");
        }
    }

    // NOVA joins these onto BASE_PATH; a leading slash probes /<cell>/<app>//health.
    [Fact]
    public void Plan_KeepsProbePathsRelativeToTheAppBasePath()
    {
        var apps = StackPlanner.Plan(Options(new Dictionary<string, string>
        {
            ["ECS_SYSTEM_IMAGES"] = "ghcr.io/acme/movement-system:1.0"
        }));

        Assert.All(apps, app =>
        {
            Assert.False(app.HealthPath!.StartsWith('/'));
            Assert.False(app.AppIcon!.StartsWith('/'));
        });
    }

    [Fact]
    public void Plan_OmitsNatsUrlSoContainersFallBackToNovasBroker()
    {
        var apps = StackPlanner.Plan(Options(new Dictionary<string, string>
        {
            ["ECS_SYSTEM_IMAGES"] = "ghcr.io/acme/movement-system:1.0"
        }));

        Assert.DoesNotContain(apps.SelectMany(a => a.Environment!), e => e.Name == "NATS_URL");
    }

    [Fact]
    public void Plan_UsesExplicitNatsUrlWhenProvided()
    {
        var apps = StackPlanner.Plan(Options(new Dictionary<string, string>
        {
            ["ECS_SYSTEM_IMAGES"] = "ghcr.io/acme/movement-system:1.0",
            ["ECS_NATS_URL"] = "nats://platform-nats:4222"
        }));

        string[] consumers = ["ecs-engine", "ecs-editor", "ecs-movement-system"];

        foreach (var name in consumers)
        {
            var app = apps.Single(a => a.Name == name);
            Assert.Equal("nats://platform-nats:4222", app.Environment!.Single(e => e.Name == "NATS_URL").Value);
        }
    }

    [Fact]
    public void Plan_ServesTheEditorUiAndApiFromOneApp()
    {
        var apps = StackPlanner.Plan(Options(new Dictionary<string, string> { ["NOVA_CELL"] = "cell" }));

        var editor = apps.Single(a => a.Name.StartsWith("ecs-editor"));
        Assert.DoesNotContain(editor.Environment!, e => e.Name == "EDITOR_BACKEND_URL");
    }

    [Fact]
    public void Plan_LeavesBasePathToNova()
    {
        var apps = StackPlanner.Plan(Options());

        Assert.DoesNotContain(apps.SelectMany(a => a.Environment!), e => e.Name == "BASE_PATH");
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
        Assert.Contains("\"health_path\":\"health\"", json);
        Assert.DoesNotContain("\"storage\"", json);
    }
}
