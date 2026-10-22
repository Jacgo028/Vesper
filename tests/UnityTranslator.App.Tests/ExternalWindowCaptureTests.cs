using UnityTranslator.App.ExternalOcr;

namespace UnityTranslator.App.Tests;

public sealed class ExternalWindowCaptureTests
{
    private static readonly WindowTarget Target = new((nint)42, 123,
        new(-9, -19, 2, 2), new(-10, -20, 4, 4));

    [Fact]
    public async Task Readback_crops_physical_client_and_copies_padded_rows_into_owned_pixels()
    {
        var native = new NativeFake();
        await using var capture = Source(native);
        var frame = await capture.ReadClientAsync(Target, default);
        Assert.Equal(2, frame.Width);
        Assert.Equal(2, frame.Height);
        Assert.Equal(new byte[] { 24,25,26,27,28,29,30,31,44,45,46,47,48,49,50,51 }, frame.Bgra32.ToArray());
        Array.Fill(native.Data.Pixels, (byte)0);
        Assert.Equal((byte)24, frame.Bgra32.Span[0]);
        Assert.Equal(Target, frame.Target);
    }

    [Fact]
    public async Task Static_content_still_has_distinct_readback_sequences()
    {
        await using var capture = Source(new());
        var first = await capture.ReadClientAsync(Target, default);
        var second = await capture.ReadClientAsync(Target, default);
        Assert.True(first.Sequence > 0);
        Assert.True(second.Sequence > first.Sequence);
        Assert.True(second.Timestamp >= first.Timestamp);
        Assert.Equal(first.Bgra32.ToArray(), second.Bgra32.ToArray());
    }

    [Theory]
    [InlineData("pid")]
    [InlineData("resize")]
    [InlineData("missing")]
    public async Task Changed_target_is_rejected_before_native_readback(string change)
    {
        var native = new NativeFake();
        var observed = change switch
        {
            "pid" => Target with { ProcessId = 999 },
            "resize" => Target with { ClientOnScreen = new(-9,-19,1,2) },
            _ => null
        };
        await using var capture = new WindowsGraphicsCaptureSource(TimeProvider.System, (_, _) => observed, native);
        await Assert.ThrowsAsync<InvalidOperationException>(() => capture.ReadClientAsync(Target, default).AsTask());
        Assert.Equal(0, native.Reads);
        Assert.True(native.Resets > 0);
    }

    [Fact]
    public async Task Resize_during_readback_discards_result_and_cached_content()
    {
        var native = new NativeFake();
        var observations = 0;
        await using var capture = new WindowsGraphicsCaptureSource(TimeProvider.System,
            (_, _) => ++observations == 1 ? Target : Target with { FrameOnScreen = new(-10,-20,5,4) }, native);
        await Assert.ThrowsAsync<InvalidOperationException>(() => capture.ReadClientAsync(Target, default).AsTask());
        Assert.True(native.Resets > 0);
    }

    [Theory]
    [InlineData(3,4,20,80)]
    [InlineData(4,3,20,80)]
    [InlineData(4,4,12,80)]
    [InlineData(4,4,20,50)]
    public async Task Inconsistent_frame_size_stride_or_buffer_is_rejected(int width,int height,int pitch,int length)
    {
        var native = new NativeFake { Data = new(width,height,pitch,new byte[length]) };
        await using var capture = Source(native);
        await Assert.ThrowsAsync<InvalidOperationException>(() => capture.ReadClientAsync(Target, default).AsTask());
        Assert.True(native.Resets > 0);
    }

    [Fact]
    public async Task Concurrent_read_is_rejected_without_disrupting_first_read()
    {
        var pending = new TaskCompletionSource<NativeCaptureReadback>(TaskCreationOptions.RunContinuationsAsynchronously);
        var native = new NativeFake { Pending = pending.Task };
        await using var capture = Source(native);
        var first = capture.ReadClientAsync(Target, default).AsTask();
        await Assert.ThrowsAsync<InvalidOperationException>(() => capture.ReadClientAsync(Target, default).AsTask());
        pending.SetResult(native.Data);
        Assert.Equal(2, (await first).Width);
        Assert.Equal(0, native.Resets);
    }

    [Fact]
    public async Task Native_failure_invalidates_cached_content()
    {
        var native = new NativeFake { Pending = Task.FromException<NativeCaptureReadback>(new InvalidOperationException("device lost")) };
        await using var capture = Source(native);
        await Assert.ThrowsAsync<InvalidOperationException>(() => capture.ReadClientAsync(Target, default).AsTask());
        Assert.True(native.Resets > 0);
    }

    // Break caught: failing to cancel a pending native read, or disposing native
    // resources before that canceled read has actually left its critical section.
    [Fact]
    public async Task Disposal_cancels_pending_read_and_waits_for_read_exit_before_native_cleanup()
    {
        var native = new CancellationAwareNative();
        var capture = new WindowsGraphicsCaptureSource(TimeProvider.System, (_, _) => Target, native);
        using var caller = new CancellationTokenSource();
        var reading = capture.ReadClientAsync(Target, caller.Token).AsTask();
        Task? disposing = null;
        try
        {
            await native.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            disposing = capture.DisposeAsync().AsTask();
            await native.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(disposing.IsCompleted);
            Assert.False(native.Disposed);
            native.AllowExit.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading.WaitAsync(TimeSpan.FromSeconds(2)));
            await disposing.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(new[] { "read-entered", "canceled", "read-exited", "reset", "disposed" }, native.Order.ToArray());
            await Assert.ThrowsAsync<ObjectDisposedException>(() => capture.ReadClientAsync(Target, default).AsTask());
        }
        finally
        {
            caller.Cancel();
            native.AllowExit.TrySetResult();
            try { await reading.WaitAsync(TimeSpan.FromSeconds(2)); } catch (OperationCanceledException) { }
            await (disposing ?? capture.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    private sealed class CancellationAwareNative : INativeWindowCapture
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Canceled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource AllowExit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly System.Collections.Concurrent.ConcurrentQueue<string> Order = new();
        public bool Disposed;
        public async ValueTask<NativeCaptureReadback> ReadAsync(WindowTarget target, CancellationToken ct)
        {
            Order.Enqueue("read-entered");
            Entered.TrySetResult();
            try
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
                catch (OperationCanceledException)
                {
                    Order.Enqueue("canceled");
                    Canceled.TrySetResult();
                    await AllowExit.Task;
                    throw;
                }
                throw new InvalidOperationException("Infinite read unexpectedly completed.");
            }
            finally { Order.Enqueue("read-exited"); }
        }
        public void Reset() => Order.Enqueue("reset");
        public void Dispose() { Disposed = true; Order.Enqueue("disposed"); }
    }

    private static WindowsGraphicsCaptureSource Source(NativeFake native) => new(TimeProvider.System, (_, _) => Target, native);
    private sealed class NativeFake : INativeWindowCapture
    {
        public NativeCaptureReadback Data = new(4,4,20,Enumerable.Range(0,80).Select(x => (byte)x).ToArray());
        public Task<NativeCaptureReadback>? Pending;
        public int Reads, Resets;
        public ValueTask<NativeCaptureReadback> ReadAsync(WindowTarget target, CancellationToken ct)
        {
            Reads++;
            return Pending is null ? ValueTask.FromResult(Data) : new(Pending);
        }
        public void Reset() => Resets++;
        public void Dispose() { }
    }
}
