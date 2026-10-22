using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using UnityTranslator.App.ExternalOcr;
using UnityTranslator.Core;

namespace UnityTranslator.App.Tests;

[Collection(WpfUiCollection.Name)]
public sealed class ExternalOverlayPresenterTests
{
    private static readonly WindowTarget Target = new((nint)42, 12,
        new OcrRect(-900, 80, 800, 600), new OcrRect(-908, 49, 816, 639));

    // Break caught: accepting on the caller thread, failing to draw, or moving from Present.
    [Fact]
    public async Task Accepted_batch_draws_on_owned_sta_without_repositioning()
    {
        await using var fixture = await Fixture.Create();
        var callbackThread = 0;
        await fixture.Presenter.PresentAsync(fixture.Placement, Batch(fixture.Placement), () =>
        {
            callbackThread = Environment.CurrentManagedThreadId;
            return true;
        }, default);
        Assert.True(fixture.Host.Visible);
        Assert.Single(fixture.Host.Drawing!.Children);
        Assert.Equal(fixture.Host.OwnerThread, callbackThread);
        Assert.NotEqual(Environment.CurrentManagedThreadId, callbackThread);
        Assert.Equal(ApartmentState.STA, fixture.Host.Apartment);
        Assert.Single(fixture.Host.Moves);
        Assert.Equal(Target.ClientOnScreen, fixture.Host.Moves[0].Rect);
    }

    [Fact]
    public async Task False_accept_never_draws()
    {
        await using var fixture = await Fixture.Create();
        var called = false;
        await fixture.Presenter.PresentAsync(fixture.Placement, Batch(fixture.Placement), () => { called = true; return false; }, default);
        Assert.True(called);
        Assert.False(fixture.Host.Visible);
        Assert.Equal(0, fixture.Host.Shows);
    }

    // Break caught: churning placement identity on unchanged 100 ms observation loops.
    [Fact]
    public async Task Repeated_unchanged_align_keeps_pending_work_valid()
    {
        await using var fixture = await Fixture.Create();
        for (var i = 0; i < 20; i++)
            Assert.Same(fixture.Placement, await fixture.Presenter.AlignAsync(Target with { }, default));
        await fixture.Presenter.PresentAsync(fixture.Placement, Batch(fixture.Placement), () => true, default);
        Assert.True(fixture.Host.Visible);
        Assert.Single(fixture.Host.Moves);
    }

    // Break caught: reference-insensitive value equality accepting an old placement after a round trip.
    [Fact]
    public async Task Move_keeps_box_size_and_old_placement_cannot_move_or_render_after_round_trip()
    {
        await using var fixture = await Fixture.Create();
        await fixture.Presenter.PresentAsync(fixture.Placement, Batch(fixture.Placement), () => true, default);
        var originalDrawing = fixture.Host.Drawing;
        var moved = Target with { ClientOnScreen = new OcrRect(-800, 100, 800, 600) };
        var next = await fixture.Presenter.AlignAsync(moved, default);
        Assert.NotSame(fixture.Placement, next);
        Assert.True(fixture.Host.Moves[1].MoveOnly);
        Assert.Same(originalDrawing, fixture.Host.Drawing);
        Assert.Equal(new Rect(30, 60, 300, 120), Assert.IsType<DrawingGroup>(fixture.Host.Drawing!.Children[0]).ClipGeometry.Bounds);
        var back = await fixture.Presenter.AlignAsync(Target, default);
        Assert.Equal(fixture.Placement, back);
        Assert.NotSame(fixture.Placement, back);
        var called = false;
        await fixture.Presenter.PresentAsync(fixture.Placement, Batch(fixture.Placement), () => { called = true; return true; }, default);
        Assert.False(called);
        Assert.Equal(1, fixture.Host.Shows);
        Assert.Equal(3, fixture.Host.Moves.Count);
        Assert.Equal(Target.ClientOnScreen, fixture.Host.Moves[^1].Rect);
    }

    [Fact]
    public async Task Resize_clears_previous_pixels_and_rejects_old_batch()
    {
        await using var fixture = await Fixture.Create();
        await fixture.Presenter.PresentAsync(fixture.Placement, Batch(fixture.Placement), () => true, default);
        var resized = Target with { ClientOnScreen = new OcrRect(-900, 80, 600, 400) };
        var placement = await fixture.Presenter.AlignAsync(resized, default);
        Assert.False(fixture.Host.Visible);
        Assert.False(fixture.Host.Moves[^1].MoveOnly);
        await fixture.Presenter.PresentAsync(placement, Batch(fixture.Placement), () => true, default);
        Assert.Equal(1, fixture.Host.Shows);
    }

    // Break caught: enqueue-only Hide allowing a queued Present to resurrect revoked pixels.
    [Fact]
    public async Task Hide_revokes_before_dispatcher_can_run_queued_present()
    {
        await using var fixture = await Fixture.Create();
        using var block = await fixture.Host.Block();
        var called = false;
        var queued = fixture.Presenter.PresentAsync(fixture.Placement, Batch(fixture.Placement), () => { called = true; return true; }, default).AsTask();
        fixture.Presenter.Hide();
        block.Dispose();
        await queued.WaitAsync(TimeSpan.FromSeconds(10));
        await fixture.Host.Drain();
        Assert.False(called);
        Assert.Equal(0, fixture.Host.Shows);
        Assert.False(fixture.Host.Visible);
    }

    [Fact]
    public async Task Callback_that_hides_cannot_commit_after_returning_true()
    {
        await using var fixture = await Fixture.Create();
        await fixture.Presenter.PresentAsync(fixture.Placement, Batch(fixture.Placement), () =>
        {
            fixture.Presenter.Hide();
            return true;
        }, default);
        await fixture.Host.Drain();
        Assert.Equal(0, fixture.Host.Shows);
    }

    // Break caught: an older queued hide erasing newly aligned/presented content.
    [Fact]
    public async Task Queued_hide_cannot_erase_new_generation()
    {
        await using var fixture = await Fixture.Create();
        await fixture.Host.Dispatcher.InvokeAsync(async () =>
        {
            fixture.Presenter.Hide();
            var fresh = await fixture.Presenter.AlignAsync(Target, default);
            await fixture.Presenter.PresentAsync(fresh, Batch(fresh), () => true, default);
        }).Task.Unwrap();
        await fixture.Host.Drain();
        Assert.True(fixture.Host.Visible);
        Assert.Equal(1, fixture.Host.Shows);
    }

    [Fact]
    public async Task Caller_mutating_boxes_after_enqueue_cannot_change_drawn_snapshot()
    {
        await using var fixture = await Fixture.Create();
        var boxes = new List<OcrOverlayItem> { new(30, 60, 300, 120, "开始") };
        using var block = await fixture.Host.Block();
        var queued = fixture.Presenter.PresentAsync(fixture.Placement, Batch(fixture.Placement) with { Boxes = boxes }, () => true, default).AsTask();
        boxes[0] = new(600, 500, 100, 50, "设置");
        block.Dispose();
        await queued.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(new Rect(30, 60, 300, 120), Assert.IsType<DrawingGroup>(fixture.Host.Drawing!.Children[0]).ClipGeometry.Bounds);
    }

    [Theory]
    [InlineData("hwnd")]
    [InlineData("pid")]
    [InlineData("width")]
    [InlineData("height")]
    [InlineData("dpi-x")]
    [InlineData("dpi-y")]
    public async Task Incompatible_batch_never_reaches_accept(string change)
    {
        await using var fixture = await Fixture.Create();
        var batch = Batch(fixture.Placement);
        var stamp = batch.Stamp;
        stamp = change switch
        {
            "hwnd" => stamp with { Hwnd = 99 },
            "pid" => stamp with { Session = stamp.Session with { ProcessId = 99 } },
            "width" => stamp with { Width = 801 },
            "height" => stamp with { Height = 601 },
            "dpi-x" => stamp with { DpiScaleX = 2 },
            _ => stamp with { DpiScaleY = 2 }
        };
        var called = false;
        await fixture.Presenter.PresentAsync(fixture.Placement, batch with { Stamp = stamp }, () => { called = true; return true; }, default);
        Assert.False(called);
        Assert.Equal(0, fixture.Host.Shows);
    }

    [Fact]
    public async Task Cancellation_after_enqueue_never_draws()
    {
        await using var fixture = await Fixture.Create();
        using var cancellation = new CancellationTokenSource();
        using var block = await fixture.Host.Block();
        var queued = fixture.Presenter.PresentAsync(fixture.Placement, Batch(fixture.Placement), () => true, cancellation.Token).AsTask();
        cancellation.Cancel();
        block.Dispose();
        try { await queued.WaitAsync(TimeSpan.FromSeconds(10)); } catch (OperationCanceledException) { }
        Assert.Equal(0, fixture.Host.Shows);
    }

    [Theory]
    [InlineData(1.5)]
    [InlineData(2)]
    public async Task Dpi_change_revokes_old_placement_and_realigns_using_overlay_transform(double dpi)
    {
        await using var fixture = await Fixture.Create();
        await fixture.Presenter.PresentAsync(fixture.Placement, Batch(fixture.Placement), () => true, default);
        await fixture.Host.Dispatcher.InvokeAsync(() => fixture.Host.ChangeDpi(dpi)).Task;
        await fixture.Host.Drain();
        Assert.False(fixture.Host.Visible);
        var fresh = await fixture.Presenter.AlignAsync(Target, default);
        Assert.Equal(dpi, fresh.DpiScaleX);
        Assert.Equal(dpi, fresh.DpiScaleY);
        Assert.NotSame(fixture.Placement, fresh);
        Assert.Equal(Target.ClientOnScreen, fixture.Host.Moves[^1].Rect);
        await fixture.Presenter.PresentAsync(fixture.Placement, Batch(fixture.Placement), () => true, default);
        Assert.Equal(1, fixture.Host.Shows);
        await fixture.Presenter.PresentAsync(fresh, Batch(fresh), () => true, default);
        Assert.Equal(2, fixture.Host.Shows);
        Assert.Equal(new Rect(30 / dpi, 60 / dpi, 300 / dpi, 120 / dpi),
            Assert.IsType<DrawingGroup>(fixture.Host.Drawing!.Children[0]).ClipGeometry.Bounds);
    }

    // Break caught: Loaded-priority DPI recovery replaying a target superseded by a newer Align.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deferred_dpi_realign_cannot_replay_target_or_revoke_new_placement(bool moveAwayAndBack)
    {
        await using var fixture = await Fixture.Create();
        var moved = Target with { ClientOnScreen = new OcrRect(100, 100, 800, 600) };
        OverlayPlacement? fresh = null;
        // All awaits here complete on the owner dispatcher, before Loaded work can drain.
        await fixture.Host.Dispatcher.InvokeAsync(async () =>
        {
            fixture.Host.ChangeDpi(2);
            fresh = await fixture.Presenter.AlignAsync(moved, default);
            if (moveAwayAndBack)
            {
                fixture.Host.ChangeDpi(1);
                fresh = await fixture.Presenter.AlignAsync(Target, default);
                Assert.Equal(fixture.Placement, fresh);
                Assert.NotSame(fixture.Placement, fresh);
            }
            await fixture.Presenter.PresentAsync(fresh, Batch(fresh), () => true, default);
            Assert.Equal(1, fixture.Host.Shows);
        }).Task.Unwrap();

        await fixture.Host.Drain();
        var expectedMoves = moveAwayAndBack
            ? new[] { Target.ClientOnScreen, moved.ClientOnScreen, Target.ClientOnScreen }
            : new[] { Target.ClientOnScreen, moved.ClientOnScreen };
        Assert.Equal(expectedMoves, fixture.Host.Moves.Select(move => move.Rect).ToArray());
        Assert.True(fixture.Host.Visible);
        var freshAccepted = false;
        await fixture.Presenter.PresentAsync(fresh!, Batch(fresh!), () => { freshAccepted = true; return true; }, default);
        Assert.True(freshAccepted);
        Assert.Equal(2, fixture.Host.Shows);
        Assert.Same(fresh, await fixture.Presenter.AlignAsync(moveAwayAndBack ? Target : moved, default));

        var staleAccepted = false;
        await fixture.Presenter.PresentAsync(fixture.Placement, Batch(fixture.Placement), () => { staleAccepted = true; return true; }, default);
        Assert.False(staleAccepted);
        Assert.Equal(2, fixture.Host.Shows);
        Assert.Equal(expectedMoves, fixture.Host.Moves.Select(move => move.Rect).ToArray());
    }

    [Fact]
    public async Task Reentrant_dpi_during_positioning_does_not_publish_prechange_placement()
    {
        await using var fixture = await Fixture.Create();
        fixture.Host.OnNextAlign = () => fixture.Host.ChangeDpi(2);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Presenter.AlignAsync(
            Target with { ClientOnScreen = new OcrRect(100, 100, 800, 600) }, default).AsTask());
        await fixture.Host.Drain();
        var fresh = await fixture.Presenter.AlignAsync(Target with { ClientOnScreen = new OcrRect(100, 100, 800, 600) }, default);
        Assert.Equal(2, fresh.DpiScaleX);
        Assert.False(fixture.Host.Visible);
    }

    [Fact]
    public async Task Dispose_revokes_queued_work_closes_host_and_stops_owned_dispatcher()
    {
        var fixture = await Fixture.Create();
        using var block = await fixture.Host.Block();
        var queued = fixture.Presenter.PresentAsync(fixture.Placement, Batch(fixture.Placement), () => true, default).AsTask();
        var disposed = fixture.Presenter.DisposeAsync().AsTask();
        Assert.False(disposed.IsCompleted);
        block.Dispose();
        await queued.WaitAsync(TimeSpan.FromSeconds(10));
        await disposed.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, fixture.Host.Shows);
        Assert.True(fixture.Host.Disposed);
        Assert.True(fixture.Host.Dispatcher.HasShutdownFinished);
        await fixture.Presenter.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => fixture.Presenter.AlignAsync(Target, default).AsTask());
    }

    // Break caught: a failed capture-visible/PMv2 initialization loses its actionable status or leaks a dispatcher.
    [Fact]
    public async Task Initialization_failure_preserves_reason_and_shuts_down_dispatcher()
    {
        Dispatcher? owner = null;
        await using var presenter = new ExternalOverlayPresenter(() =>
        {
            owner = Dispatcher.CurrentDispatcher;
            throw new System.ComponentModel.Win32Exception(5, "capture-visible configuration rejected");
        });
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => presenter.AlignAsync(Target, default).AsTask());
        Assert.Contains("capture-visible", failure.Message);
        Assert.Equal(5, Assert.IsType<System.ComponentModel.Win32Exception>(failure.InnerException).NativeErrorCode);
        await presenter.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(owner!.HasShutdownFinished);
    }

    [Fact]
    public async Task Native_position_failure_hides_previous_content_and_revokes_placement()
    {
        await using var fixture = await Fixture.Create();
        await fixture.Presenter.PresentAsync(fixture.Placement, Batch(fixture.Placement), () => true, default);
        fixture.Host.OnNextAlign = () => throw new System.ComponentModel.Win32Exception(5, "position rejected");
        await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(() => fixture.Presenter.AlignAsync(
            Target with { ClientOnScreen = new OcrRect(100, 100, 800, 600) }, default).AsTask());
        Assert.False(fixture.Host.Visible);
        await fixture.Presenter.PresentAsync(fixture.Placement, Batch(fixture.Placement), () => true, default);
        Assert.Equal(1, fixture.Host.Shows);
    }

    [Fact]
    public async Task Native_host_factory_can_enter_PMv2_before_any_dispatcher_hwnd_exists()
    {
        Dispatcher? beforeHost = null;
        await using var presenter = new ExternalOverlayPresenter(() =>
        {
            beforeHost = Dispatcher.FromThread(Thread.CurrentThread);
            return new FakeHost();
        });
        await presenter.AlignAsync(Target, default);
        Assert.Null(beforeHost);
    }

    private static ExternalOverlayBatch Batch(OverlayPlacement placement) => new(new(
        new("session", "game", 12, "il2cpp-bepinex6"), placement.Target.Hwnd, 1,
        placement.Target.ClientOnScreen.Width, placement.Target.ClientOnScreen.Height,
        placement.DpiScaleX, placement.DpiScaleY, 100), new[] { new OcrOverlayItem(30, 60, 300, 120, "开始游戏") });

    private sealed record Fixture(ExternalOverlayPresenter Presenter, FakeHost Host, OverlayPlacement Placement) : IAsyncDisposable
    {
        internal static async Task<Fixture> Create()
        {
            FakeHost? host = null;
            var presenter = new ExternalOverlayPresenter(() => host = new FakeHost());
            try
            {
                var placement = await presenter.AlignAsync(Target, default).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
                return new(presenter, host!, placement);
            }
            catch { await presenter.DisposeAsync(); throw; }
        }
        public ValueTask DisposeAsync() => Presenter.DisposeAsync();
    }

    // Fake only the native HWND boundary. Ownership, dispatcher scheduling, guards and WPF drawing are real.
    private sealed class FakeHost : IExternalOverlayHost
    {
        public event Action? DpiChanged;
        public Matrix TransformFromDevice { get; private set; } = Matrix.Identity;
        internal Dispatcher Dispatcher { get; } = Dispatcher.CurrentDispatcher;
        internal int OwnerThread { get; } = Environment.CurrentManagedThreadId;
        internal ApartmentState Apartment { get; } = Thread.CurrentThread.GetApartmentState();
        internal List<(OcrRect Rect, bool MoveOnly)> Moves { get; } = new();
        internal DrawingGroup? Drawing;
        internal bool Visible;
        internal int Shows;
        internal bool Disposed;
        internal Action? OnNextAlign;
        public void Align(OcrRect client, bool moveOnly)
        {
            Dispatcher.VerifyAccess();
            Moves.Add((client, moveOnly));
            var callback = OnNextAlign;
            OnNextAlign = null;
            callback?.Invoke();
        }
        public void Present(DrawingGroup drawing) { Dispatcher.VerifyAccess(); Drawing = drawing; Visible = true; Shows++; }
        public void Hide() { Dispatcher.VerifyAccess(); Visible = false; Drawing = null; }
        public void Dispose() { Dispatcher.VerifyAccess(); Disposed = true; Visible = false; }
        internal void ChangeDpi(double dpi)
        {
            TransformFromDevice = new Matrix(1 / dpi, 0, 0, 1 / dpi, 0, 0);
            DpiChanged?.Invoke();
        }
        internal Task Drain() => Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task.WaitAsync(TimeSpan.FromSeconds(10));
        internal async Task<IDisposable> Block()
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new ManualResetEventSlim();
            _ = Dispatcher.InvokeAsync(() => { entered.SetResult(); release.Wait(TimeSpan.FromSeconds(15)); });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return new ReleaseBlock(release);
        }
        private sealed class ReleaseBlock(ManualResetEventSlim release) : IDisposable
        {
            public void Dispose() => release.Set();
        }
    }
}
