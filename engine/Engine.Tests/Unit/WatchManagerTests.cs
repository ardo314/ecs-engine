using Engine.Coordinator;
using Engine.Core.Messages;

namespace Engine.Tests.Unit;

[Trait("Category", "Unit")]
public class WatchManagerTests
{
    [Fact]
    public void Register_ReturnsResponseWithDataSubject()
    {
        var wm = new WatchManager();
        var watchId = Guid.NewGuid();

        var response = wm.Register(new WatchRequest
        {
            WatchId = watchId,
            IncludeSystems = true,
            IncludeEntities = true
        });

        Assert.Equal(watchId, response.WatchId);
        Assert.Equal($"engine.watch.data.{watchId}", response.DataSubject);
    }

    [Fact]
    public void Register_AddsToActiveWatches()
    {
        var wm = new WatchManager();
        var watchId = Guid.NewGuid();

        wm.Register(new WatchRequest { WatchId = watchId, IncludeSystems = true, IncludeEntities = true });

        var watches = wm.GetActiveWatches();
        Assert.Single(watches);
        Assert.Equal(watchId, watches[0].WatchId);
    }

    [Fact]
    public void Cancel_RemovesWatch()
    {
        var wm = new WatchManager();
        var watchId = Guid.NewGuid();
        wm.Register(new WatchRequest { WatchId = watchId, IncludeSystems = true, IncludeEntities = false });

        wm.Cancel(watchId);

        Assert.Empty(wm.GetActiveWatches());
    }

    [Fact]
    public void Cancel_NonExistent_NoError()
    {
        var wm = new WatchManager();
        wm.Cancel(Guid.NewGuid()); // Should not throw
    }

    [Fact]
    public void ShouldIncludeSystems_ReturnsTrueOnFirstCall()
    {
        var wm = new WatchManager();
        var watchId = Guid.NewGuid();
        wm.Register(new WatchRequest { WatchId = watchId, IncludeSystems = true, IncludeEntities = false });

        var spec = wm.GetActiveWatches()[0];
        Assert.True(wm.ShouldIncludeSystems(spec));
    }

    [Fact]
    public void ShouldIncludeSystems_ReturnsFalseIfNoChange()
    {
        var wm = new WatchManager();
        var watchId = Guid.NewGuid();
        wm.Register(new WatchRequest { WatchId = watchId, IncludeSystems = true, IncludeEntities = false });

        var spec = wm.GetActiveWatches()[0];
        wm.ShouldIncludeSystems(spec); // first call consumes

        // Second call without NotifySystemsChanged → false
        spec = wm.GetActiveWatches()[0];
        Assert.False(wm.ShouldIncludeSystems(spec));
    }

    [Fact]
    public void ShouldIncludeSystems_ReturnsTrueAfterSystemsChanged()
    {
        var wm = new WatchManager();
        var watchId = Guid.NewGuid();
        wm.Register(new WatchRequest { WatchId = watchId, IncludeSystems = true, IncludeEntities = false });

        var spec = wm.GetActiveWatches()[0];
        wm.ShouldIncludeSystems(spec); // consume initial

        wm.NotifySystemsChanged();

        spec = wm.GetActiveWatches()[0];
        Assert.True(wm.ShouldIncludeSystems(spec));
    }

    [Fact]
    public void ShouldIncludeSystems_FalseWhenIncludeSystemsIsFalse()
    {
        var wm = new WatchManager();
        wm.Register(new WatchRequest { WatchId = Guid.NewGuid(), IncludeSystems = false, IncludeEntities = true });

        var spec = wm.GetActiveWatches()[0];
        Assert.False(wm.ShouldIncludeSystems(spec));
    }

    [Fact]
    public void GetActiveWatches_ReturnsSnapshot()
    {
        var wm = new WatchManager();
        wm.Register(new WatchRequest { WatchId = Guid.NewGuid(), IncludeSystems = true, IncludeEntities = true });
        wm.Register(new WatchRequest { WatchId = Guid.NewGuid(), IncludeSystems = false, IncludeEntities = true });

        var watches = wm.GetActiveWatches();
        Assert.Equal(2, watches.Count);
    }

    [Fact]
    public void Register_PreservesComponentFilter()
    {
        var wm = new WatchManager();
        wm.Register(new WatchRequest
        {
            WatchId = Guid.NewGuid(),
            IncludeSystems = false,
            IncludeEntities = true,
            ComponentFilter = ["Position", "Velocity"]
        });

        var spec = wm.GetActiveWatches()[0];
        Assert.NotNull(spec.ComponentFilter);
        Assert.Equal(["Position", "Velocity"], spec.ComponentFilter);
    }
}
