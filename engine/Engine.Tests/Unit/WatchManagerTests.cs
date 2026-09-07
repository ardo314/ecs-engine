using Ecs.Protocol.V1;
using Engine.Coordinator;

namespace Engine.Tests.Unit;

public class WatchManagerTests
{
    private static WatchRequest Request(string id, bool systems = true, bool entities = true) => new()
    {
        WatchId = id,
        IncludeSystems = systems,
        IncludeEntities = entities,
    };

    [Fact]
    public void Register_AnswersWithTheSubjectToListenOn()
    {
        var watches = new WatchManager();

        var response = watches.Register(Request("w1"));

        Assert.Equal("w1", response.WatchId);
        Assert.Equal("engine.world.watch.data.w1", response.DataSubject);
        Assert.Single(watches.ActiveWatches());
    }

    [Fact]
    public void Cancel_RemovesTheWatchAndToleratesAnUnknownId()
    {
        var watches = new WatchManager();
        watches.Register(Request("w1"));

        watches.Cancel("w1");
        watches.Cancel("never-registered");

        Assert.Empty(watches.ActiveWatches());
    }

    [Fact]
    public void Systems_AreSentOnceAndThenOnlyWhenTheyChange()
    {
        var watches = new WatchManager();
        var spec = watches.ActiveWatchFor(Request("w1"));

        Assert.True(watches.ClaimSystems(spec));
        Assert.False(watches.ClaimSystems(spec));

        watches.NotifySystemsChanged();
        Assert.True(watches.ClaimSystems(spec));
    }

    [Fact]
    public void SystemsAreNeverSentToAWatcherThatDidNotAskForThem()
    {
        var watches = new WatchManager();
        var spec = watches.ActiveWatchFor(Request("w1", systems: false));

        Assert.False(watches.ClaimSystems(spec));
    }

    [Fact]
    public void Schemas_AreSentOncePerRegistryVersion()
    {
        var watches = new WatchManager();
        var spec = watches.ActiveWatchFor(Request("w1"));

        Assert.True(watches.ClaimSchemas(spec, 3));
        Assert.False(watches.ClaimSchemas(spec, 3));

        // A new component type was bound, so the watcher needs the descriptors to
        // decode anything using it.
        Assert.True(watches.ClaimSchemas(spec, 4));
    }

    [Fact]
    public void Register_KeepsTheEntityFilter()
    {
        var watches = new WatchManager();
        var request = Request("w1");
        request.Filter = new EntityFilter { AllOf = { "movement.v1.Position" } };

        watches.Register(request);

        var spec = Assert.Single(watches.ActiveWatches());
        Assert.Equal(["movement.v1.Position"], spec.Filter!.AllOf);
    }
}

internal static class WatchManagerTestExtensions
{
    /// <summary>Registers and returns the stored spec, which is what the tick loop works with.</summary>
    public static WatchSpec ActiveWatchFor(this WatchManager watches, WatchRequest request)
    {
        watches.Register(request);
        return watches.ActiveWatches().Single(w => w.WatchId == request.WatchId);
    }
}
