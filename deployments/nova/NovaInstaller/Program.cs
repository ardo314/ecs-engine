using System.Text.Json;
using NovaInstaller;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    var options = InstallerOptions.FromEnvironment(Environment.GetEnvironmentVariable);
    var manifests = StackPlanner.Plan(options);

    Console.WriteLine($"NOVA instance : {options.NovaBaseUrl}");
    Console.WriteLine($"Cell          : {options.Cell}");
    Console.WriteLine($"Apps          : {string.Join(", ", manifests.Select(m => m.Name))}");

    if (options.DryRun)
    {
        Console.WriteLine();
        foreach (var manifest in manifests)
            Console.WriteLine(JsonSerializer.Serialize(Redact(manifest), NovaJson.Pretty));
        return 0;
    }

    if (string.IsNullOrEmpty(options.AccessToken))
        Console.WriteLine("Warning: NOVA_ACCESS_TOKEN is empty; requests will be unauthenticated.");

    // Started before the install so the probe is already answering while apps go in.
    using var health = HealthEndpoint.StartFromEnvironment();
    if (health is not null)
        Console.WriteLine($"Health endpoint listening on port {health.Port}");

    using var client = new NovaAppClient(options.NovaBaseUrl, options.Cell, options.AccessToken);
    var existing = (await client.ListAppNamesAsync(cts.Token)).ToHashSet(StringComparer.Ordinal);

    Console.WriteLine();
    foreach (var manifest in manifests)
    {
        if (existing.Contains(manifest.Name))
        {
            Console.WriteLine($"replacing  {manifest.Name}");
            await client.DeleteAppAsync(manifest.Name, cts.Token);
            await client.WaitUntilAbsentAsync(manifest.Name, TimeSpan.FromSeconds(60), cts.Token);
        }
        else
        {
            Console.WriteLine($"installing {manifest.Name}");
        }

        await client.AddAppAsync(manifest, cts.Token);
    }

    Console.WriteLine($"\nDone. {manifests.Count} apps installed in cell '{options.Cell}'.");

    if (health is null) return 0;

    // NOVA restarts an app that stops answering its probe, which would reinstall the stack.
    Console.WriteLine("Install complete; serving health probes until stopped.");
    try
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cts.Token);
    }
    catch (OperationCanceledException)
    {
        // Shutdown requested.
    }

    return 0;
}
catch (InstallerConfigurationException e)
{
    Console.Error.WriteLine($"Configuration error: {e.Message}");
    return 2;
}
catch (NovaApiException e)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}
catch (HttpRequestException e)
{
    Console.Error.WriteLine($"Could not reach the NOVA API: {e.Message}");
    return 1;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled.");
    return 130;
}

static AppManifest Redact(AppManifest manifest) => manifest.ContainerImage.Credentials is null
    ? manifest
    : new AppManifest
    {
        Name = manifest.Name,
        AppIcon = manifest.AppIcon,
        ContainerImage = new ContainerImage
        {
            Image = manifest.ContainerImage.Image,
            Credentials = new RegistryCredentials
            {
                Registry = manifest.ContainerImage.Credentials.Registry,
                User = manifest.ContainerImage.Credentials.User,
                Password = "***"
            }
        },
        Port = manifest.Port,
        HealthPath = manifest.HealthPath,
        Environment = manifest.Environment,
        Storage = manifest.Storage,
        Resources = manifest.Resources
    };
