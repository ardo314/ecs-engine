namespace Client;

/// <summary>
/// Entry point for running a system. Provides a one-liner to instantiate,
/// connect, and run a <see cref="SystemBase"/>-derived system.
/// </summary>
public static class SystemHost
{
    /// <summary>
    /// Creates and runs a system of type <typeparamref name="TSystem"/>.
    /// Handles connection, lifecycle (OnCreate/OnDestroy), and graceful shutdown on Ctrl+C.
    /// </summary>
    public static async Task RunAsync<TSystem>(string[]? args = null, CancellationToken ct = default)
        where TSystem : SystemBase, new()
    {
        var system = new TSystem();
        var natsUrl = Environment.GetEnvironmentVariable("NATS_URL") ?? "nats://localhost:4222";

        await using var runner = new SystemRunner(system, natsUrl);

        // Call OnCreate before connecting (queries and initial ECB commands are set up here)
        system.InvokeOnCreate();

        await runner.ConnectAsync(ct);

        // Wire up Ctrl+C if no external token provided
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        Console.WriteLine($"[{system.SystemName}] Starting...");

        try
        {
            await runner.RunAsync(cts.Token);
        }
        catch (OperationCanceledException) { /* graceful shutdown */ }
        finally
        {
            system.InvokeOnDestroy();
        }
    }
}
