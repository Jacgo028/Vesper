using System.Threading.Channels;
using System.Reflection;
using Microsoft.Extensions.Time.Testing;
using UnityTranslator.App.ExternalOcr;
using UnityTranslator.Core;

namespace UnityTranslator.App.Tests;

public sealed class ExternalOcrControllerTests
{
    [Fact]
    public async Task Two_distinct_frames_are_required_and_OCR_is_throttled_for_two_seconds()
    {
        await using var f = new Fixture();
        f.Start();
        await f.Recognizer.Calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await f.Batch;
        await f.Ticks(19);
        Assert.Equal(1, f.Recognizer.Count);
        Assert.Equal(0, f.Provider.Count);
        await f.Ticks(2);
        await f.Recognizer.Calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await f.Clock.DelayScheduled.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await f.Ticks(3);
        var batch = await f.Presenter.Batches.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(batch.Boxes);
        Assert.Equal(1, f.Provider.Count);
    }

    [Fact]
    public async Task Repeated_sequence_does_not_prime_stability()
    {
        await using var f = new Fixture();
        f.Capture.FixedSequence = true;
        f.Start();
        await f.Recognizer.Calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await f.Batch;
        await f.Ticks(42);
        Assert.Equal(1, f.Recognizer.Count);
        Assert.Equal(0, f.Provider.Count);
    }

    [Theory]
    [InlineData("off-on")]
    [InlineData("focus")]
    [InlineData("pid")]
    [InlineData("hwnd")]
    public async Task Ignored_old_provider_wait_is_hidden_and_never_overlaps_or_presents_after_rebind(string transition)
    {
        await using var f = new Fixture();
        f.Provider.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Start();
        await f.Recognizer.Calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await f.Batch;
        await f.Ticks(21);
        await f.Provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var epoch = f.Controller.Epoch;
        var hides = f.Presenter.Hides;
        if (transition == "off-on") { f.Controller.SetEnabled(false); f.Controller.SetEnabled(true); }
        if (transition == "focus") { f.Tracker.Foreground = false; await f.Ticks(2); f.Tracker.Foreground = true; }
        if (transition == "pid")
        {
            f.CaptureFactory = () => new Capture(f.Clock);
            f.Start(f.Session with { ProcessId = 43 });
        }
        if (transition == "hwnd") f.Tracker.Target = f.Tracker.Target with { Hwnd = (nint)456 };
        await f.Ticks(25);
        Assert.True(f.Controller.Epoch > epoch);
        Assert.True(f.Presenter.Hides > hides);
        Assert.Equal(2, f.Recognizer.Count);
        Assert.Equal(1, f.Services);
        var oldBatch = f.Batch;
        f.Provider.Pending.SetResult("开始");
        await oldBatch.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(f.Presenter.Batches.Reader.TryRead(out _));
        Assert.All(f.Recognizer.Paths, path => Assert.False(File.Exists(path)));
        while (f.Recognizer.Calls.Reader.TryRead(out _)) { }
        await f.Ticks(2);
        await f.Prime();
        Assert.Equal(2, f.Services);
        Assert.Equal(1, f.Provider.Count); // A fresh epoch needs its own first stable observation.
        await f.Ticks(21);
        await f.Clock.DelayScheduled.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await f.Ticks(3);
        var recovered = await f.Presenter.Batches.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(f.Controller.Epoch, recovered.Stamp.Epoch);
        Assert.Equal(f.Session.ProcessId, recovered.Stamp.Session.ProcessId);
        Assert.Equal(f.Tracker.Target.Hwnd, recovered.Stamp.Hwnd);
    }

    [Fact]
    public async Task Dispose_hides_synchronously_and_drains_ignored_provider_before_disposing_presenter_and_releasing_png()
    {
        await using var f = new Fixture();
        f.Provider.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Start();
        await f.Recognizer.Calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await f.Batch;
        await f.Ticks(21);
        await f.Provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var hides = f.Presenter.Hides;
        var closing = f.Controller.DisposeAsync().AsTask();
        Assert.True(f.Presenter.Hides > hides);
        Assert.False(closing.IsCompleted);
        Assert.False(f.Presenter.Disposed);
        Assert.True(File.Exists(f.Recognizer.Paths[^1]));
        f.Provider.Pending.SetResult("开始");
        await closing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(f.Capture.Disposed);
        Assert.True(f.Presenter.Disposed);
        Assert.All(f.Recognizer.Paths, path => Assert.False(File.Exists(path)));
    }

    [Fact]
    public async Task F9_short_event_is_consumed_without_waiting_for_an_observation_tick()
    {
        await using var f = new Fixture();
        var changed = new TaskCompletionSource<ExternalOcrEnabledChange>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Controller.EnabledChanged += e => changed.TrySetResult(e);
        f.Start(enabled: false);
        await f.Ticks(2);
        Assert.NotNull(f.F9.Target);
        f.F9.Press(f.Clock.GetTimestamp());
        // No clock advance: a complete short press must not depend on the 100ms timer.
        var result = await changed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(result.Enabled);
        Assert.Equal("session", result.SessionId);
    }

    [Fact]
    public async Task F9_press_during_manual_retarget_has_no_transient_untargeted_gap()
    {
        await using var f = new Fixture();
        f.Start(enabled: false); await f.Ticks(2); await f.F9.WaitReads(1);
        var changed = new TaskCompletionSource<ExternalOcrEnabledChange>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Controller.EnabledChanged += e => changed.TrySetResult(e);
        // Simulate a short native callback interleaving at the first target-publication boundary.
        f.F9.OnNextTargetUpdate = () => f.F9.Press(f.Clock.GetTimestamp());
        f.Controller.SetEnabled(false);
        Assert.True((await changed.Task.WaitAsync(TimeSpan.FromSeconds(3))).Enabled);
    }

    [Fact]
    public async Task F9_can_reopen_after_manual_off_while_alignment_is_stalled()
    {
        await using var f = new Fixture();
        f.Presenter.AlignPending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Start();
        await f.Presenter.AlignEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        // Native source startup and observation have independent scheduling.
        await f.F9.WaitReads(1);
        f.Controller.SetEnabled(false);
        var changed = new TaskCompletionSource<ExternalOcrEnabledChange>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Controller.EnabledChanged += e => changed.TrySetResult(e);
        f.F9.Press(f.Clock.GetTimestamp());
        Assert.True((await changed.Task.WaitAsync(TimeSpan.FromSeconds(3))).Enabled);
        Assert.False(f.Presenter.AlignPending.Task.IsCompleted);
    }

    [Fact]
    public async Task F9_pending_before_a_manual_choice_cannot_reverse_it()
    {
        await using var f = new Fixture();
        var changes = Channel.CreateUnbounded<ExternalOcrEnabledChange>();
        f.Controller.EnabledChanged += e => changes.Writer.TryWrite(e);
        f.Start(enabled: false); await f.Ticks(2);
        f.F9.Pause = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.F9.Press(f.Clock.GetTimestamp());
        await f.F9.Taken.Task.WaitAsync(TimeSpan.FromSeconds(3));
        f.Controller.SetEnabled(false); await f.Ticks(2);
        f.F9.Pause.SetResult(); await f.F9.WaitReads(2);
        Assert.False(changes.Reader.TryRead(out _));
        f.F9.Press(f.Clock.GetTimestamp());
        Assert.True((await changes.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3))).Enabled);
    }

    [Fact]
    public async Task F9_event_older_than_500ms_is_discarded_not_replayed()
    {
        await using var f = new Fixture();
        var changes = Channel.CreateUnbounded<ExternalOcrEnabledChange>();
        f.Controller.EnabledChanged += e => changes.Writer.TryWrite(e);
        f.Start(enabled: false); await f.Ticks(2);
        f.F9.Pause = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.F9.Press(f.Clock.GetTimestamp());
        await f.F9.Taken.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await f.Ticks(6);
        f.F9.Pause.SetResult(); await f.F9.WaitReads(2);
        Assert.False(changes.Reader.TryRead(out _));
    }

    [Fact]
    public async Task F9_does_not_wait_for_a_slow_translation_provider()
    {
        await using var f = new Fixture();
        f.Provider.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Start(); await f.Prime(); await f.Ticks(21);
        await f.Provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var changed = new TaskCompletionSource<ExternalOcrEnabledChange>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Controller.EnabledChanged += e => changed.TrySetResult(e);
        var hides = f.Presenter.Hides;
        f.F9.Press(f.Clock.GetTimestamp());
        Assert.False((await changed.Task.WaitAsync(TimeSpan.FromSeconds(3))).Enabled);
        Assert.True(f.Presenter.Hides > hides);
        Assert.False(f.Provider.Pending.Task.IsCompleted);
    }

    [Fact]
    public async Task F9_failed_cleanup_blocks_reinstall_and_warns_the_next_session()
    {
        await using var f = new Fixture { ExpectedDisposeFailure = true };
        var failure = new IOException("input cleanup not confirmed");
        f.F9.DisposeError = failure;
        f.Start(enabled: false); await f.Ticks(2);
        f.Controller.Invalidate(ExternalOcrStopReason.SessionEnded);
        var warned = new TaskCompletionSource<ExternalOcrStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Controller.StatusChanged += e => warned.TrySetResult(e);
        f.Start(f.Session with { SessionId = "next" }, enabled: false);
        var status = await warned.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("next", status.SessionId);
        Assert.Equal(1, f.InputCreations);
        var error = await Assert.ThrowsAsync<AggregateException>(() => f.Controller.DisposeAsync().AsTask());
        Assert.Contains(failure, error.Flatten().InnerExceptions);
        Assert.True(f.Presenter.Disposed);
    }

    [Fact]
    public async Task F9_ended_session_releases_its_input_source()
    {
        await using var f = new Fixture();
        f.Start(enabled: false);
        await f.Ticks(2);
        f.Controller.Invalidate(ExternalOcrStopReason.SessionEnded);
        await f.F9.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Null(f.F9.Target);
    }

    [Fact]
    public async Task F9_old_lease_is_not_replayed_after_focus_loss()
    {
        await using var f = new Fixture();
        var events = Channel.CreateUnbounded<ExternalOcrEnabledChange>();
        f.Controller.EnabledChanged += e => events.Writer.TryWrite(e);
        f.Start(enabled: false);
        await f.Ticks(2);
        var old = f.F9.Target;
        f.F9.Press(f.Clock.GetTimestamp());
        var first = await events.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(first.Enabled);
        f.Tracker.Foreground = false;
        await f.Ticks(2);
        f.Tracker.Foreground = true;
        await f.Ticks(2);
        f.F9.Events.Writer.TryWrite(new(old, 999, f.Clock.GetTimestamp()));
        f.F9.Press(f.Clock.GetTimestamp());
        var second = await events.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(second.Enabled);
        Assert.False(events.Reader.TryRead(out _));
        var epoch = f.Controller.Epoch;
        f.Controller.SetEnabled(false);
        Assert.Equal(epoch, f.Controller.Epoch);
    }

    [Theory]
    [InlineData("SessionEnded")]
    [InlineData("ProcessLost")]
    public async Task Terminal_invalidation_cannot_be_revived_by_old_F9(string reason)
    {
        await using var f = new Fixture();
        f.Start(enabled: false);
        await f.Ticks(2);
        f.Controller.Invalidate(Enum.Parse<ExternalOcrStopReason>(reason));
        var epoch = f.Controller.Epoch;
        f.F9.Press(f.Clock.GetTimestamp());
        f.Clock.Advance(TimeSpan.FromSeconds(10));
        f.Controller.SetEnabled(true);
        Assert.Equal(epoch, f.Controller.Epoch);
        Assert.Equal(0, f.Capture.Reads);
        Assert.Equal(0, f.Services);
    }

    [Fact]
    public async Task Availability_requires_all_OCR_files_and_capture_support_without_creating_device()
    {
        var directory = Path.Combine(Path.GetTempPath(), "external-factory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "tessdata"));
        var creations = 0;
        var supported = true;
        var captures = new ExternalCaptureFactory(() => true, () => supported,
            () => { creations++; throw new InvalidOperationException("must not create device"); });
        var factory = new ExternalOcrControllerFactory(directory, captures);
        try
        {
            Assert.False(factory.GetAvailability().IsAvailable);
            File.WriteAllText(Path.Combine(directory, "tesseract.exe"), "marker");
            File.WriteAllText(Path.Combine(directory, "tessdata", "eng.traineddata"), "marker");
            Assert.False(factory.GetAvailability().IsAvailable);
            File.WriteAllText(Path.Combine(directory, "tessdata", "jpn.traineddata"), "marker");
            Assert.True(factory.GetAvailability().IsAvailable);
            supported = false;
            Assert.False(factory.GetAvailability().IsAvailable);
            Assert.Equal(0, creations);
        }
        finally { Directory.Delete(directory, true); }
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Backend_creation_failure_is_reported_with_current_identity_and_no_escaped_task()
    {
        await using var f = new Fixture();
        f.CaptureFactory = () => throw new IOException("synthetic device failure");
        var status = new TaskCompletionSource<ExternalOcrStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Controller.StatusChanged += value => status.TrySetResult(value);
        f.Start();
        var failure = await status.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("synthetic device failure", failure.Message);
        Assert.Equal(f.Session.SessionId, failure.SessionId);
        Assert.Equal(42, failure.ProcessId);
        Assert.Equal(f.Controller.Epoch, failure.Epoch);
        Assert.Equal(0, f.Recognizer.Count);
        await f.Controller.DisposeAsync();
    }

    [Fact]
    public async Task Repeated_invalidation_is_idempotent_until_a_new_observation_becomes_eligible()
    {
        await using var f = new Fixture();
        f.Start();
        await f.Prime();
        f.Controller.Invalidate(ExternalOcrStopReason.FocusLost);
        var epoch = f.Controller.Epoch;
        f.Controller.Invalidate(ExternalOcrStopReason.FocusLost);
        Assert.Equal(epoch, f.Controller.Epoch);
    }

    [Fact]
    public async Task Malformed_capture_invalidates_and_reports_failure_instead_of_preserving_stability()
    {
        await using var f = new Fixture();
        f.Start();
        await f.Prime();
        var epoch = f.Controller.Epoch;
        f.Capture.Malformed = true;
        await f.Ticks(21);
        await f.Capture.Calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await f.Batch;
        Assert.True(f.Controller.Epoch > epoch);
    }

    [Fact]
    public async Task Failed_recognizer_deletes_owned_png_and_requires_fresh_stability()
    {
        await using var f = new Fixture();
        f.Recognizer.Error = new IOException("recognizer read failed");
        var status = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Controller.StatusChanged += _ => status.TrySetResult();
        f.Start();
        await status.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await f.Batch;
        Assert.All(f.Recognizer.Paths, path => Assert.False(File.Exists(path)));
        f.Recognizer.Error = null;
        await f.Ticks(21);
        await f.Recognizer.Calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await f.Batch;
        Assert.Equal(0, f.Provider.Count);
    }

    [Fact]
    public async Task Transient_alignment_cancellation_recovers_without_stopping_session()
    {
        await using var f = new Fixture();
        f.Presenter.CancelNextAlign = true;
        f.Start();
        await f.Ticks(3);
        await f.Prime();
        Assert.Equal(1, f.Recognizer.Count);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("embedded")]
    [InlineData("no-window")]
    [InlineData("background")]
    public async Task Ineligible_sessions_never_allocate_capture_service_or_network(string state)
    {
        await using var f = new Fixture();
        if (state == "embedded") f.Session = f.Session with { PackageName = "unity2022-mono" };
        if (state == "no-window") f.Tracker.NoWindow = true;
        if (state == "background") f.Tracker.Foreground = false;
        f.Start(enabled: state != "disabled");
        await f.Ticks(25);
        Assert.Equal(0, f.CaptureCreations);
        Assert.Equal(0, f.Services);
        Assert.Equal(0, f.Provider.Count);
    }

    [Theory]
    [InlineData("pixels")]
    [InlineData("bounds")]
    [InlineData("age")]
    public async Task Results_are_rejected_when_pixels_bounds_or_twelve_second_age_fail(string condition)
    {
        await using var f = new Fixture();
        if (condition == "bounds") f.Recognizer.Boxes = [new(20, 0, 20, 16, "Start", .99f)];
        f.Provider.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Start();
        await f.Prime();
        await f.Ticks(21);
        await f.Provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (condition == "pixels") f.Capture.Rgb = 255;
        await f.Ticks(condition == "age" ? 120 : 3);
        var batch = f.Batch;
        f.Provider.Pending.SetResult("开始");
        await batch.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(f.Presenter.Batches.Reader.TryRead(out _));
        Assert.All(f.Recognizer.Paths, path => Assert.False(File.Exists(path)));
    }

    [Theory]
    [InlineData("off-on")]
    [InlineData("dpi")]
    [InlineData("age")]
    public async Task Queued_present_rechecks_epoch_placement_and_age_at_actual_accept(string transition)
    {
        await using var f = new Fixture();
        f.Presenter.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Start();
        await f.Prime();
        await f.Ticks(21);
        await f.Clock.DelayScheduled.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await f.Ticks(3);
        await f.Presenter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (transition == "off-on") { f.Controller.SetEnabled(false); f.Controller.SetEnabled(true); }
        if (transition == "dpi") { f.Presenter.Dpi = 1.75; await f.Ticks(2); }
        if (transition == "age") await f.Ticks(120);
        f.Presenter.Pending.SetResult();
        await f.Batch.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(f.Presenter.Batches.Reader.TryRead(out _));
    }

    [Fact]
    public async Task At_most_six_boxes_are_translated_and_presented()
    {
        await using var f = new Fixture();
        f.Recognizer.Boxes = Enumerable.Range(0, 8).Select(i => new OcrBox(0, 0, 20, 16, "Start" + i, .99f)).ToArray();
        var batch = await f.Show();
        Assert.Equal(6, batch.Boxes.Count);
        Assert.Equal(6, f.Provider.Count);
    }

    [Fact]
    public async Task Visible_regions_are_read_no_faster_than_250ms_and_hide_when_pixels_change()
    {
        await using var f = new Fixture();
        await f.Show();
        var epoch = f.Controller.Epoch;
        f.Capture.Rgb = 255;
        await f.Ticks(4);
        await f.Validation.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(f.Controller.Epoch > epoch);
        Assert.False(f.Presenter.Visible);
        Assert.All(f.Capture.ReadTimes.Zip(f.Capture.ReadTimes.Skip(1)), pair =>
            Assert.True(f.Clock.GetElapsedTime(pair.First, pair.Second) >= TimeSpan.FromMilliseconds(250)));
    }

    [Fact]
    public async Task Display_expires_after_eight_seconds_even_without_a_new_OCR_result()
    {
        await using var f = new Fixture();
        await f.Show();
        f.Capture.FixedSequence = true;
        var epoch = f.Controller.Epoch;
        await f.Ticks(80);
        Assert.True(f.Controller.Epoch > epoch);
        Assert.False(f.Presenter.Visible);
    }

    [Fact]
    public async Task Empty_new_OCR_result_clears_previously_visible_boxes()
    {
        await using var f = new Fixture();
        await f.Show();
        f.Recognizer.Boxes = [];
        await f.Ticks(21);
        await f.NextRecognition();
        await f.Batch.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(f.Presenter.Visible);
    }

    [Theory]
    [InlineData("backend")]
    [InlineData("service")]
    public async Task Slow_resource_creation_does_not_block_disable_and_hide(string resource)
    {
        await using var f = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        void Block() { entered.TrySetResult(); release.Wait(); }
        if (resource == "backend") f.CaptureFactory = () => { Block(); return f.Capture; };
        else f.BeforeCreateService = Block;
        f.Start();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var hideCount = f.Presenter.Hides;
        var disable = Task.Run(() => f.Controller.SetEnabled(false));
        try
        {
            await disable.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(f.Presenter.Hides > hideCount);
        }
        finally { release.Set(); await disable; }
        await f.Batch.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, f.Recognizer.Count);
    }

    [Fact]
    public async Task Pending_capture_cannot_block_focus_observation_or_publish_after_focus_loss()
    {
        await using var f = new Fixture();
        f.Capture.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Start();
        await f.Capture.Calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var epoch = f.Controller.Epoch;
        f.Tracker.Foreground = false;
        await f.Ticks(2);
        Assert.True(f.Controller.Epoch > epoch);
        f.Capture.Pending.SetResult(ExternalOcrTestDoubles.Frame() with { Timestamp = f.Clock.GetTimestamp() });
        await f.Batch.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, f.Recognizer.Count);
    }

    [Fact]
    public async Task Slow_capture_does_not_shorten_the_two_second_OCR_interval()
    {
        await using var f = new Fixture();
        f.Capture.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Start();
        await f.Capture.Calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await f.Ticks(10);
        f.Capture.Pending.SetResult(ExternalOcrTestDoubles.Frame() with { Timestamp = f.Clock.GetTimestamp() });
        await f.Prime();
        f.Capture.Pending = null;
        await f.Ticks(11);
        if (!f.Batch.IsCompleted)
            await f.Recognizer.Calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, f.Recognizer.Count);
    }

    [Fact]
    public async Task Recognizer_cancellation_without_controller_cancellation_invalidates_visible_content()
    {
        await using var f = new Fixture();
        await f.Show();
        f.Recognizer.Error = new OperationCanceledException("recognizer timed out");
        var epoch = f.Controller.Epoch;
        await f.Ticks(21);
        await f.NextRecognition();
        await f.Batch.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(f.Controller.Epoch > epoch);
        Assert.False(f.Presenter.Visible);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("focus")]
    [InlineData("no-window")]
    [InlineData("wrong-pid")]
    public async Task Eligibility_revocation_retires_an_existing_capture_stream(string transition)
    {
        await using var f = new Fixture();
        f.Start();
        await f.Prime();
        if (transition == "disabled") f.Controller.SetEnabled(false);
        if (transition == "focus") f.Tracker.Foreground = false;
        if (transition == "no-window") f.Tracker.NoWindow = true;
        if (transition == "wrong-pid") f.Tracker.Target = f.Tracker.Target with { ProcessId = 999 };
        await f.Ticks(2);
        await f.Capture.DisposeFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(f.Presenter.Visible);
        await f.Ticks(25);
        Assert.Equal(1, f.CaptureCreations);
        Assert.Equal(1, f.Capture.Reads);
        Assert.Equal(1, f.Capture.Disposals);
    }

    [Theory]
    [InlineData("off-on")]
    [InlineData("new-session")]
    public async Task Rapid_resume_waits_for_old_read_and_backend_retirement_before_recreation(string transition)
    {
        await using var f = new Fixture();
        f.Capture.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Capture.DisposePending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacement = new Capture(f.Clock);
        f.Start();
        await f.Capture.Calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var oldBatch = f.Batch;
        f.CaptureFactory = () =>
        {
            Assert.True(f.Capture.Disposed); // A fresh native device cannot race retirement of the old stream.
            return replacement;
        };
        f.Controller.SetEnabled(false);
        if (transition == "off-on") f.Controller.SetEnabled(true);
        else f.Start(f.Session with { SessionId = "next", ProcessId = 43 });
        await f.Ticks(25);
        Assert.Equal(1, f.CaptureCreations);
        Assert.False(f.Capture.DisposeEntered.Task.IsCompleted);
        f.Capture.Pending.SetResult(ExternalOcrTestDoubles.Frame() with { Timestamp = f.Clock.GetTimestamp() });
        await oldBatch.WaitAsync(TimeSpan.FromSeconds(5));
        await f.Capture.DisposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await f.Ticks(25); // Observation keeps running while native disposal is blocked.
        Assert.Equal(1, f.CaptureCreations);
        Assert.Equal(0, f.Recognizer.Count);
        f.Capture.DisposePending.SetResult();
        await replacement.Calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await f.Prime();
        Assert.Equal(2, f.CaptureCreations);
        Assert.Equal(1, f.Services);
        Assert.Equal(1, f.Capture.Disposals);
    }

    [Fact]
    public async Task Disable_retires_capture_while_ignored_provider_still_owns_its_png()
    {
        await using var f = new Fixture();
        f.Provider.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Start();
        await f.Prime();
        await f.Ticks(21);
        await f.Provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var oldBatch = f.Batch;
        f.Controller.SetEnabled(false);
        await f.Capture.DisposeFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(oldBatch.IsCompleted);
        Assert.True(File.Exists(f.Recognizer.Paths[^1]));
        f.Controller.SetEnabled(true);
        await f.Ticks(25);
        Assert.Equal(1, f.CaptureCreations);
        f.Provider.Pending.SetResult("开始");
        await oldBatch.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(File.Exists(f.Recognizer.Paths[^1]));
    }

    [Fact]
    public async Task Final_disposal_drains_an_already_pending_capture_retirement_once()
    {
        await using var f = new Fixture();
        f.Start();
        await f.Prime();
        f.Capture.DisposePending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Controller.SetEnabled(false);
        await f.Capture.DisposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var closing = f.Controller.DisposeAsync().AsTask();
        Assert.False(closing.IsCompleted);
        Assert.False(f.Presenter.Disposed);
        f.Capture.DisposePending.SetResult();
        await closing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(f.Presenter.Disposed);
        Assert.Equal(1, f.Capture.Disposals);
    }

    [Fact]
    public async Task Throwing_capture_disposal_and_presenter_disposal_are_both_reported_by_final_drain()
    {
        await using var f = new Fixture { ExpectedDisposeFailure = true };
        var captureFailure = new IOException("native cleanup failed");
        var presenterFailure = new InvalidOperationException("presenter cleanup failed");
        f.Start();
        await f.Prime();
        f.Capture.DisposeError = captureFailure;
        f.Presenter.DisposeError = presenterFailure;
        var error = await Assert.ThrowsAsync<AggregateException>(() => f.Controller.DisposeAsync().AsTask());
        Assert.Contains(captureFailure, error.Flatten().InnerExceptions);
        Assert.Contains(presenterFailure, error.Flatten().InnerExceptions);
        Assert.True(f.Presenter.Disposed);
        Assert.Equal(1, f.Capture.Disposals);
        var repeated = await Assert.ThrowsAsync<AggregateException>(() => f.Controller.DisposeAsync().AsTask());
        Assert.Same(error, repeated);
    }

    [Theory]
    [InlineData("off-on")]
    [InlineData("new-session")]
    public async Task Throwing_retirement_blocks_recreation_and_survives_epoch_or_session_change(string transition)
    {
        await using var f = new Fixture { ExpectedDisposeFailure = true };
        var failure = new IOException("old stream retirement unconfirmed");
        f.Start();
        await f.Prime();
        f.Capture.DisposeError = failure;
        f.Capture.DisposePending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Controller.SetEnabled(false);
        await f.Capture.DisposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (transition == "off-on") f.Controller.SetEnabled(true);
        else f.Start(f.Session with { SessionId = "rebound", ProcessId = 43 });
        f.Capture.DisposePending.SetResult();
        await f.Capture.DisposeFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await f.CaptureRetirement.WaitAsync(TimeSpan.FromSeconds(5));
        await f.Ticks(25);
        Assert.Equal(1, f.CaptureCreations);
        Assert.Equal(1, f.Recognizer.Count);
        Assert.False(f.Presenter.Visible);
        var error = await Assert.ThrowsAsync<AggregateException>(() => f.Controller.DisposeAsync().AsTask());
        Assert.Contains(failure, error.Flatten().InnerExceptions);
        Assert.Equal(1, f.Capture.Disposals); // A second native Dispose may be a no-op, never proof of successful cleanup.
        Assert.True(f.Presenter.Disposed);
    }

    [Fact]
    public async Task Throwing_retirement_still_drains_ignored_provider_and_owned_png_before_final_error()
    {
        await using var f = new Fixture { ExpectedDisposeFailure = true };
        var failure = new IOException("native disposal failed during close");
        f.Provider.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Start();
        await f.Prime();
        await f.Ticks(21);
        await f.Provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        f.Capture.DisposeError = failure;
        var closing = f.Controller.DisposeAsync().AsTask();
        await f.Capture.DisposeFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(closing.IsCompleted);
        Assert.False(f.Presenter.Disposed);
        Assert.True(File.Exists(f.Recognizer.Paths[^1]));
        f.Provider.Pending.SetResult("开始");
        var error = await Assert.ThrowsAsync<AggregateException>(() => closing);
        Assert.Contains(failure, error.Flatten().InnerExceptions);
        Assert.False(File.Exists(f.Recognizer.Paths[^1]));
        Assert.True(f.Presenter.Disposed);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly TestClock Clock = new();
        internal readonly Tracker Tracker = new();
        internal readonly Capture Capture;
        internal readonly Presenter Presenter = new();
        internal readonly Recognizer Recognizer = new();
        internal readonly Provider Provider = new();
        internal readonly F9Source F9 = new();
        internal readonly ExternalOcrController Controller;
        internal ExternalOcrSession Session = new("session", "game", 42, "il2cpp-bepinex6");
        internal int Services;
        internal int CaptureCreations;
        internal int InputCreations;
        internal Func<IExternalWindowCapture>? CaptureFactory;
        internal Action? BeforeCreateService;
        internal bool ExpectedDisposeFailure;
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "external-controller-tests-" + Guid.NewGuid().ToString("N"));
        private readonly TranslationStore _store;
        // Await a real work task at boundaries; production exposes no testing-only drain API.
        internal Task Batch => (Task)typeof(ExternalOcrController).GetField("_batch", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Controller)!;
        internal Task CaptureRetirement => (Task)typeof(ExternalOcrController).GetField("_captureRetirement", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Controller)!;
        internal Task Validation
        {
            get
            {
                var binding = typeof(ExternalOcrController).GetField("_current", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Controller)!;
                return (Task)binding.GetType().GetField("Validation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(binding)!;
            }
        }
        internal Fixture()
        {
            Directory.CreateDirectory(_directory);
            _store = new(Path.Combine(_directory, "translations.db"));
            Capture = new(Clock);
            Controller = new(Tracker, () =>
            {
                var count = Interlocked.Increment(ref CaptureCreations);
                return CaptureFactory?.Invoke() ?? (count == 1 ? Capture : new Capture(Clock));
            }, Presenter, Clock, () => { Interlocked.Increment(ref InputCreations); return F9; });
        }
        internal async Task Prime()
        {
            await Recognizer.Calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await Batch.WaitAsync(TimeSpan.FromSeconds(5));
            while (Capture.Calls.Reader.TryRead(out _)) { }
        }
        internal async Task NextRecognition()
        {
            var recognition = Recognizer.Calls.Reader.ReadAsync().AsTask();
            using var stop = new CancellationTokenSource();
            try
            {
                while (!recognition.IsCompleted)
                {
                    var delay = Clock.DelayScheduled.Reader.ReadAsync(stop.Token).AsTask();
                    if (await Task.WhenAny(recognition, delay).WaitAsync(TimeSpan.FromSeconds(5)) == recognition) break;
                    // A display-validation read may own the serialized capture slot first.
                    // Advance exactly the requested fake delay, rounded to observation ticks.
                    await Ticks((int)Math.Ceiling((await delay).TotalMilliseconds / 100));
                }
                await recognition;
            }
            finally { stop.Cancel(); }
        }
        internal async Task<ExternalOverlayBatch> Show()
        {
            Start();
            await Prime();
            await Ticks(21);
            await Clock.DelayScheduled.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await Ticks(3);
            var batch = await Presenter.Batches.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await Batch.WaitAsync(TimeSpan.FromSeconds(5));
            while (Recognizer.Calls.Reader.TryRead(out _)) { }
            return batch;
        }
        internal void Start(ExternalOcrSession? session = null, bool enabled = true)
        {
            Session = session ?? Session;
            Tracker.Target = Tracker.Target with { ProcessId = Session.ProcessId };
            Controller.Start(Session, () => { Interlocked.Increment(ref Services); BeforeCreateService?.Invoke(); return new(Recognizer, new(_store, Provider)); }, enabled);
        }
        internal async Task Ticks(int count)
        {
            // Explicit timer handshakes: an Observe at each tick proves the monitor is alive.
            // Allow async work to finish via its own call/presentation TCS where it matters.
            for (var i = 0; i < count; i++)
            {
                while (Tracker.Observations.Reader.TryRead(out _)) { }
                Clock.Advance(TimeSpan.FromMilliseconds(100));
                await Tracker.Observations.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        public async ValueTask DisposeAsync()
        {
            Provider.Pending?.TrySetResult("开始");
            Presenter.Pending?.TrySetResult();
            Presenter.AlignPending?.TrySetResult();
            Capture.Pending?.TrySetResult(ExternalOcrTestDoubles.Frame() with { Timestamp = Clock.GetTimestamp() });
            Capture.DisposePending?.TrySetResult();
            try { await Controller.DisposeAsync(); }
            catch when (ExpectedDisposeFailure) { /* The test explicitly asserts the drain failure before cleanup. */ }
            finally
            {
                _store.Dispose();
                Directory.Delete(_directory, true);
            }
        }
    }

    private sealed class F9Source : IExternalF9Source
    {
        internal F9InputLease? Target;
        internal readonly Channel<F9InputEvent> Events = Channel.CreateUnbounded<F9InputEvent>();
        internal readonly TaskCompletionSource Disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Taken = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource? Pause;
        internal Exception? DisposeError;
        internal Action? OnNextTargetUpdate;
        private readonly Channel<int> _reads = Channel.CreateUnbounded<int>();
        private int _readCount;
        private long _sequence;
        public void SetTarget(F9InputLease? target)
        {
            Target = target;
            var action = OnNextTargetUpdate; OnNextTargetUpdate = null; action?.Invoke();
        }
        internal void Press(long timestamp) => Events.Writer.TryWrite(new(Target, ++_sequence, timestamp));
        public async ValueTask<F9InputEvent> ReadAsync(CancellationToken ct)
        {
            _reads.Writer.TryWrite(Interlocked.Increment(ref _readCount));
            var input = await Events.Reader.ReadAsync(ct);
            Taken.TrySetResult();
            if (Pause is { } pause) await pause.Task.WaitAsync(ct);
            return input;
        }
        internal async Task WaitReads(int count)
        { while (await _reads.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)) < count) { } }
        public ValueTask DisposeAsync()
        {
            Target = null; Disposed.TrySetResult();
            return DisposeError is null ? ValueTask.CompletedTask : ValueTask.FromException(DisposeError);
        }
    }

    private sealed class Tracker : IExternalWindowTracker
    {
        internal WindowTarget Target = ExternalOcrTestDoubles.Frame().Target;
        internal bool Foreground = true, NoWindow;
        internal readonly Channel<bool> Observations = Channel.CreateUnbounded<bool>();
        public WindowObservation Observe(int pid)
        {
            var observation = new WindowObservation(NoWindow ? null : Target, Foreground, false, true, false);
            Observations.Writer.TryWrite(true);
            return observation;
        }
    }
    private sealed class TestClock : FakeTimeProvider
    {
        internal readonly Channel<TimeSpan> DelayScheduled = Channel.CreateUnbounded<TimeSpan>();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            if (period == Timeout.InfiniteTimeSpan) DelayScheduled.Writer.TryWrite(dueTime);
            return timer;
        }
    }
    private sealed class Capture(FakeTimeProvider clock) : IExternalWindowCapture
    {
        internal int Reads;
        internal int Disposals;
        internal bool FixedSequence, Disposed, Malformed;
        internal byte Rgb;
        internal TaskCompletionSource<CapturedClientFrame>? Pending;
        internal TaskCompletionSource? DisposePending;
        internal Exception? DisposeError;
        internal readonly TaskCompletionSource DisposeEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource DisposeFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly Channel<int> Calls = Channel.CreateUnbounded<int>();
        internal readonly List<long> ReadTimes = [];
        public ValueTask<CapturedClientFrame> ReadClientAsync(WindowTarget target, CancellationToken ct)
        {
            Assert.False(Disposed);
            var sequence = Interlocked.Increment(ref Reads);
            ReadTimes.Add(clock.GetTimestamp());
            Calls.Writer.TryWrite(sequence);
            if (Pending is not null) return new(Pending.Task);
            var frame = ExternalOcrTestDoubles.Frame(rgb: Rgb) with { Target = target, Sequence = FixedSequence ? 1 : sequence, Timestamp = clock.GetTimestamp() };
            return ValueTask.FromResult(Malformed ? frame with { Width = 17 } : frame);
        }
        public async ValueTask DisposeAsync()
        {
            var attempt = Interlocked.Increment(ref Disposals);
            DisposeEntered.TrySetResult();
            try
            {
                if (DisposePending is not null) await DisposePending.Task;
                // Match native lifetime semantics: a retry may return without confirming first-attempt cleanup.
                if (attempt == 1 && DisposeError is not null) throw DisposeError;
                Disposed = true;
            }
            finally { DisposeFinished.TrySetResult(); }
        }
    }
    private sealed class Presenter : IExternalOverlayPresenter
    {
        internal int Hides;
        internal bool Disposed, Visible, CancelNextAlign;
        internal double Dpi = 1;
        internal TaskCompletionSource? Pending;
        internal TaskCompletionSource? AlignPending;
        internal readonly TaskCompletionSource AlignEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Exception? DisposeError;
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private OverlayPlacement? _placement;
        internal readonly Channel<ExternalOverlayBatch> Batches = Channel.CreateUnbounded<ExternalOverlayBatch>();
        public async ValueTask<OverlayPlacement> AlignAsync(WindowTarget target, CancellationToken ct)
        {
            AlignEntered.TrySetResult();
            if (AlignPending is not null) await AlignPending.Task;
            if (CancelNextAlign) { CancelNextAlign = false; throw new OperationCanceledException("DPI revoked alignment"); }
            return _placement?.Target == target && _placement.DpiScaleX == Dpi ? _placement : _placement = new(target, Dpi, Dpi);
        }
        public async ValueTask PresentAsync(OverlayPlacement placement, ExternalOverlayBatch batch, Func<bool> accept, CancellationToken ct)
        {
            Entered.TrySetResult();
            if (Pending is not null) await Pending.Task;
            if (accept()) { Visible = true; Batches.Writer.TryWrite(batch); }
        }
        public void Hide() { Interlocked.Increment(ref Hides); _placement = null; Visible = false; }
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return DisposeError is null ? ValueTask.CompletedTask : ValueTask.FromException(DisposeError);
        }
    }
    private sealed class Recognizer : IOcrRecognizer
    {
        internal int Count;
        internal Exception? Error;
        internal IReadOnlyList<OcrBox> Boxes = [new(0, 0, 20, 16, "Start", .99f)];
        internal readonly List<string> Paths = [];
        internal readonly Channel<int> Calls = Channel.CreateUnbounded<int>();
        public Task<IReadOnlyList<OcrBox>> RecognizeAsync(string path, CancellationToken ct)
        {
            Assert.True(File.Exists(path));
            Paths.Add(path);
            Calls.Writer.TryWrite(Interlocked.Increment(ref Count));
            return Error is null ? Task.FromResult(Boxes) : Task.FromException<IReadOnlyList<OcrBox>>(Error);
        }
    }
    private sealed class Provider : IOnlineTranslator
    {
        internal int Count;
        internal TaskCompletionSource<string?>? Pending;
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<string?> TranslateAsync(string text, string language, CancellationToken ct)
        { Interlocked.Increment(ref Count); Entered.TrySetResult(); return Pending?.Task ?? Task.FromResult<string?>("开始"); }
    }
}
