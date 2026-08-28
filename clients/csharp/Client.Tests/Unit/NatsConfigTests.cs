using Client;

namespace Client.Tests.Unit;

[Collection("NatsEnvironment")]
public class NatsConfigTests : IDisposable
{
    private readonly string? _url = Environment.GetEnvironmentVariable("NATS_URL");
    private readonly string? _broker = Environment.GetEnvironmentVariable("NATS_BROKER");

    public NatsConfigTests()
    {
        Environment.SetEnvironmentVariable("NATS_URL", null);
        Environment.SetEnvironmentVariable("NATS_BROKER", null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("NATS_URL", _url);
        Environment.SetEnvironmentVariable("NATS_BROKER", _broker);
    }

    [Fact]
    public void ResolveUrl_PrefersTheExplicitArgument()
    {
        Environment.SetEnvironmentVariable("NATS_URL", "nats://from-env:4222");

        Assert.Equal("nats://explicit:4222", NatsConfig.ResolveUrl("nats://explicit:4222"));
    }

    [Fact]
    public void ResolveUrl_UsesNatsUrlBeforeNatsBroker()
    {
        Environment.SetEnvironmentVariable("NATS_URL", "nats://configured:4222");
        Environment.SetEnvironmentVariable("NATS_BROKER", "nats://injected:4222");

        Assert.Equal("nats://configured:4222", NatsConfig.ResolveUrl());
    }

    [Fact]
    public void ResolveUrl_FallsBackToNatsBrokerWhenNatsUrlIsUnset()
    {
        Environment.SetEnvironmentVariable("NATS_BROKER", "nats://injected:4222");

        Assert.Equal("nats://injected:4222", NatsConfig.ResolveUrl());
    }

    [Fact]
    public void ResolveUrl_IgnoresBlankValues()
    {
        Environment.SetEnvironmentVariable("NATS_URL", "   ");
        Environment.SetEnvironmentVariable("NATS_BROKER", "nats://injected:4222");

        Assert.Equal("nats://injected:4222", NatsConfig.ResolveUrl());
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
