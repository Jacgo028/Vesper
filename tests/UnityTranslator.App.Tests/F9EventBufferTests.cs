using Microsoft.Extensions.Time.Testing;
using UnityTranslator.App.ExternalOcr;

namespace UnityTranslator.App.Tests;

public sealed class F9EventBufferTests
{
    private static readonly F9InputLease Lease = new("game-session", 42, (nint)123, 1);

    [Fact]
    public void Complete_short_press_survives_until_consumption()
    {
        var b = new F9EventBuffer(new FakeTimeProvider(), false);
        b.SetTarget(Lease);
        b.Key(true, 123, 42); b.Key(false, 123, 42);
        Assert.True(b.Events.TryRead(out var edge));
        Assert.Equal(Lease, edge.Lease);
        Assert.False(b.Events.TryRead(out _));
    }

    [Fact]
    public void Repeated_downs_produce_one_edge_and_release_allows_next_press()
    {
        var b = new F9EventBuffer(new FakeTimeProvider(), false);
        b.SetTarget(Lease);
        for (var i = 0; i < 50; i++) b.Key(true, 123, 42);
        b.Key(false, 123, 42); b.Key(true, 123, 42);
        Assert.True(b.Events.TryRead(out var first));
        Assert.True(b.Events.TryRead(out var second));
        Assert.True(second.Sequence > first.Sequence);
        Assert.False(b.Events.TryRead(out _));
    }

    [Fact]
    public void Background_hold_does_not_become_a_press_after_target_change()
    {
        var b = new F9EventBuffer(new FakeTimeProvider(), false);
        b.SetTarget(Lease); b.Key(true, 999, 9);
        b.SetTarget(null); b.SetTarget(Lease with { Generation = 2 });
        b.Key(true, 123, 42);
        Assert.False(b.Events.TryRead(out _));
        b.Key(false, 999, 9); b.Key(true, 123, 42);
        Assert.True(b.Events.TryRead(out _));
    }

    [Fact]
    public void Rebinding_discards_pending_edges_and_does_not_rearm_a_held_key()
    {
        var b = new F9EventBuffer(new FakeTimeProvider(), false);
        b.SetTarget(Lease); b.Key(true, 123, 42);
        b.SetTarget(Lease with { Generation = 2 });
        b.Key(true, 123, 42);
        Assert.False(b.Events.TryRead(out _));
        b.Key(false, 123, 42); b.Key(true, 123, 42);
        Assert.True(b.Events.TryRead(out var edge));
        Assert.Equal(2, edge.Lease!.Generation);
    }

    [Fact]
    public void Initially_held_key_requires_release()
    {
        var b = new F9EventBuffer(new FakeTimeProvider(), true);
        b.SetTarget(Lease); b.Key(true, 123, 42);
        Assert.False(b.Events.TryRead(out _));
        b.Key(false, 123, 42); b.Key(true, 123, 42);
        Assert.True(b.Events.TryRead(out _));
    }

    [Fact]
    public void Overflow_discards_old_input_and_reports_one_failure_not_partial_replay()
    {
        var b = new F9EventBuffer(new FakeTimeProvider(), false);
        b.SetTarget(Lease);
        for (var i = 0; i < 65; i++) { b.Key(true, 123, 42); b.Key(false, 123, 42); }
        Assert.True(b.Events.TryRead(out var failure));
        Assert.True(failure.Failed);
        b.Key(true, 123, 42); b.Fail();
        Assert.False(b.Events.TryRead(out _));
    }
}
