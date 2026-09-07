using Ecs.Protocol.V1;
using Engine.Coordinator;

namespace Engine.Tests.Unit;

public class LeaseManagerTests
{
    private static readonly HashSet<uint> Writable = [22];

    [Fact]
    public void Issue_ProducesADistinctLeasePerInvocation()
    {
        var leases = new LeaseManager();

        var first = leases.Issue(1, "Movement", [1ul], Writable);
        var second = leases.Issue(1, "Physics", [1ul], Writable);

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(2, leases.LiveCount);
    }

    [Fact]
    public void Claim_SucceedsOnceAndOnlyOnce()
    {
        var leases = new LeaseManager();
        var lease = leases.Issue(8291, "Movement", [1ul, 2ul], Writable);

        Assert.True(leases.TryClaim(lease.Id, 8291, out var claimed, out _));
        Assert.Equal("Movement", claimed.SystemName);

        // A duplicated result must not be applied twice.
        Assert.False(leases.TryClaim(lease.Id, 8291, out _, out var reason));
        Assert.Equal(ResultRejectionReason.UnknownLease, reason);
    }

    [Fact]
    public void Claim_RefusesAResultForADifferentTick()
    {
        var leases = new LeaseManager();
        var lease = leases.Issue(8291, "Movement", [1ul], Writable);

        Assert.False(leases.TryClaim(lease.Id, 8292, out _, out var reason));
        Assert.Equal(ResultRejectionReason.TickMismatch, reason);
    }

    [Fact]
    public void Claim_RefusesALeaseItNeverIssued()
    {
        var leases = new LeaseManager();

        Assert.False(leases.TryClaim("not-a-lease", 1, out _, out var reason));
        Assert.Equal(ResultRejectionReason.UnknownLease, reason);
    }

    [Fact]
    public void RetiringATick_StopsALateWorkerFromLandingStaleWrites()
    {
        var leases = new LeaseManager();
        var stale = leases.Issue(8290, "Slow", [1ul], Writable);

        Assert.Equal(1, leases.RetireThrough(8291));
        Assert.False(leases.TryClaim(stale.Id, 8290, out _, out var reason));
        Assert.Equal(ResultRejectionReason.UnknownLease, reason);
    }

    [Fact]
    public void RetiringATick_LeavesLaterLeasesAlone()
    {
        var leases = new LeaseManager();
        var future = leases.Issue(8292, "Movement", [1ul], Writable);

        leases.RetireThrough(8291);

        Assert.True(leases.TryClaim(future.Id, 8292, out _, out _));
    }

    [Fact]
    public void Lease_RecordsTheSliceAndTheWritableTypes()
    {
        var leases = new LeaseManager();
        var lease = leases.Issue(1, "Movement", [4ul, 5ul], new HashSet<uint> { 22, 31 });

        Assert.Contains(4ul, lease.Entities);
        Assert.DoesNotContain(6ul, lease.Entities);
        Assert.Contains(22u, lease.Writable);
        Assert.DoesNotContain(17u, lease.Writable);
    }
}
