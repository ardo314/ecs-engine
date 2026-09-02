namespace NovaInstaller.Tests;

public class InstallerOptionsTests
{
    private static InstallerOptions Options(Dictionary<string, string> env) =>
        InstallerOptions.FromEnvironment(key => env.GetValueOrDefault(key));

    [Fact]
    public void FromEnvironment_DerivesImagesFromRegistryAndTag()
    {
        var options = Options(new Dictionary<string, string>
        {
            ["ECS_IMAGE_REGISTRY"] = "registry.example.com/ecs",
            ["ECS_IMAGE_TAG"] = "1.2.3"
        });

        Assert.Equal("registry.example.com/ecs/engine:1.2.3", options.EngineImage);
        Assert.Equal("registry.example.com/ecs/editor:1.2.3", options.EditorImage);
    }

    [Fact]
    public void FromEnvironment_DefaultsToTheTagBakedInAtBuildTime()
    {
        var options = Options([]);

        Assert.Equal($"{InstallerOptions.DefaultRegistry}/engine:{InstallerOptions.DefaultImageTag}", options.EngineImage);
        Assert.Equal($"{InstallerOptions.DefaultRegistry}/editor:{InstallerOptions.DefaultImageTag}", options.EditorImage);
    }

    [Fact]
    public void FromEnvironment_LeavesNatsUrlUnsetByDefault()
    {
        Assert.Null(Options([]).NatsUrl);
    }

    [Fact]
    public void FromEnvironment_PrefersExplicitImageOverrides()
    {
        var options = Options(new Dictionary<string, string> { ["ECS_ENGINE_IMAGE"] = "docker.io/acme/engine:dev" });

        Assert.Equal("docker.io/acme/engine:dev", options.EngineImage);
    }

    [Fact]
    public void FromEnvironment_TrimsTrailingSlashFromBaseUrl()
    {
        var options = Options(new Dictionary<string, string> { ["NOVA_BASE_URL"] = "https://nova.example.com/" });

        Assert.Equal("https://nova.example.com", options.NovaBaseUrl);
    }

    [Fact]
    public void FromEnvironment_FallsBackToTheInjectedNovaApiAndCellName()
    {
        var options = Options(new Dictionary<string, string>
        {
            ["NOVA_API"] = "http://api-gateway.wandelbots.svc.cluster.local/api/v1",
            ["CELL_NAME"] = "cell-a"
        });

        Assert.Equal("http://api-gateway.wandelbots.svc.cluster.local", options.NovaBaseUrl);
        Assert.Equal("cell-a", options.Cell);
    }

    [Fact]
    public void FromEnvironment_PrefersExplicitBaseUrlOverInjectedNovaApi()
    {
        var options = Options(new Dictionary<string, string>
        {
            ["NOVA_BASE_URL"] = "https://nova.example.com",
            ["NOVA_API"] = "http://api-gateway/api/v1"
        });

        Assert.Equal("https://nova.example.com", options.NovaBaseUrl);
    }

    [Theory]
    [InlineData("api-gateway:8080", "http://api-gateway:8080")]
    [InlineData("https://nova.example.com/api", "https://nova.example.com")]
    [InlineData("https://nova.example.com/api/v2/", "https://nova.example.com")]
    public void NormalizeBaseUrl_ReducesAnAddressToTheInstanceRoot(string raw, string expected)
    {
        Assert.Equal(expected, InstallerOptions.NormalizeBaseUrl(raw));
    }

    [Fact]
    public void NormalizeBaseUrl_RejectsNonHttpAddresses()
    {
        Assert.Throws<InstallerConfigurationException>(() => InstallerOptions.NormalizeBaseUrl("nats://broker:4222"));
    }

    [Fact]
    public void FromEnvironment_RejectsNonPositiveTickRate()
    {
        Assert.Throws<InstallerConfigurationException>(() =>
            Options(new Dictionary<string, string> { ["ECS_TICK_RATE"] = "0" }));
    }

    [Fact]
    public void FromEnvironment_RejectsUnparseableBoolean()
    {
        Assert.Throws<InstallerConfigurationException>(() =>
            Options(new Dictionary<string, string> { ["ECS_INSTALL_EDITOR"] = "maybe" }));
    }

    [Theory]
    [InlineData("ghcr.io/acme/movement-system:1.0", "movement-system")]
    [InlineData("docker.io/library/nats", "nats")]
    [InlineData("acme/nova-systems@sha256:abc", "nova-systems")]
    public void DeriveName_UsesRepositorySegmentWithoutTagOrDigest(string image, string expected)
    {
        Assert.Equal(expected, InstallerOptions.DeriveName(image));
    }

    [Fact]
    public void ParseSystemImages_AcceptsBareImagesAndNamePairs()
    {
        var systems = InstallerOptions.ParseSystemImages("ghcr.io/acme/movement-system:1.0, io = ghcr.io/acme/nova:2.0");

        Assert.Equal(["movement-system", "io"], systems.Select(s => s.Name));
        Assert.Equal(["ghcr.io/acme/movement-system:1.0", "ghcr.io/acme/nova:2.0"], systems.Select(s => s.Image));
    }

    [Fact]
    public void ParseSystemImages_RejectsDuplicateNames()
    {
        Assert.Throws<InstallerConfigurationException>(() =>
            InstallerOptions.ParseSystemImages("a=ghcr.io/x/one:1,a=ghcr.io/x/two:1"));
    }

    [Fact]
    public void ParseSystemImages_RejectsEntryWithoutImage()
    {
        Assert.Throws<InstallerConfigurationException>(() => InstallerOptions.ParseSystemImages("name="));
    }

    [Fact]
    public void ParseSystemImages_TreatsEmptyValueAsNoSystems()
    {
        Assert.Empty(InstallerOptions.ParseSystemImages("  "));
    }

    [Theory]
    [InlineData("Movement System", "movement-system")]
    [InlineData("Nova.Systems", "nova-systems")]
    [InlineData("--weird__name--", "weird-name")]
    [InlineData("9lives", "a9lives")]
    public void Sanitize_ProducesRfc1035Labels(string input, string expected)
    {
        Assert.Equal(expected, AppName.Sanitize(input));
    }

    [Fact]
    public void Sanitize_TruncatesToLabelLengthLimit()
    {
        var name = AppName.Sanitize(new string('a', 100));

        Assert.Equal(63, name.Length);
    }
}
