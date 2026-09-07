using Client;

namespace Client.Tests.Unit;

[Collection("NatsEnvironment")]
public class NatsConfigTests : IDisposable
{
    private readonly string? _url = Environment.GetEnvironmentVariable("NATS_URL");

    public NatsConfigTests()
    {
        Environment.SetEnvironmentVariable("NATS_URL", null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("NATS_URL", _url);
    }

    [Fact]
    public void ResolveUrl_PrefersTheExplicitArgument()
    {
        Environment.SetEnvironmentVariable("NATS_URL", "nats://from-env:4222");

        Assert.Equal("nats://explicit:4222", NatsConfig.ResolveUrl("nats://explicit:4222"));
    }

    [Fact]
    public void ResolveUrl_UsesNatsUrlWhenNoArgumentIsGiven()
    {
        Environment.SetEnvironmentVariable("NATS_URL", "nats://configured:4222");

        Assert.Equal("nats://configured:4222", NatsConfig.ResolveUrl());
    }

    [Fact]
    public void ResolveUrl_IgnoresBlankValues()
    {
        Environment.SetEnvironmentVariable("NATS_URL", "   ");

        Assert.Equal(NatsConfig.DefaultUrl, NatsConfig.ResolveUrl());
    }

    [Fact]
    public void ResolveUrl_FallsBackToTheLocalDefault()
    {
        Assert.Equal(NatsConfig.DefaultUrl, NatsConfig.ResolveUrl());
    }

    [Theory]
    [InlineData("nats://user:token@broker:4222", "nats://***@broker:4222")]
    [InlineData("nats://broker:4222", "nats://broker:4222")]
    public void Redact_MasksCredentialsEmbeddedInTheUrl(string url, string expected)
    {
        Assert.Equal(expected, NatsConfig.Redact(url));
    }
}
