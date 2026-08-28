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
        Assert.Equal("registry.example.com/ecs/editor-backend:1.2.3", options.EditorBackendImage);
        Assert.Equal("registry.example.com/ecs/nova-nats:1.2.3", options.NatsImage);
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
