using System.Runtime.InteropServices;
using System.Windows.Threading;
using UnityTranslator.App.ExternalOcr;

namespace UnityTranslator.App.Tests;

public sealed class WindowsF9SourceTests
{
    [Fact]
    public async Task Real_source_forwards_keys_and_buffers_only_the_target_F9_edge()
    {
        var native = new Native();
        await using var source = new WindowsF9Source(TimeProvider.System, native);
        source.SetTarget(new("s", 42, 123, 1));
        await native.Installed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal((nint)73, await native.Send(0x41, true));
        Assert.Equal((nint)73, await native.Send(0x78, true));
        await native.Send(0x78, true); await native.Send(0x78, false);
        var input = await source.ReadAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(123, input.Lease!.Hwnd);
        Assert.Equal(4, native.Forwarded);
        await source.DisposeAsync();
        Assert.Equal(1, native.Removed);
    }

    [Fact]
    public async Task Install_failure_is_a_readable_fault_and_disposal_does_not_hang()
    {
        var native = new Native { InstallFails = true };
        await using var source = new WindowsF9Source(TimeProvider.System, native);
        var input = await source.ReadAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(input.Failed);
        await source.DisposeAsync();
        Assert.Equal(0, native.Removed);
    }

    [Fact]
    public async Task Failed_unhook_is_retained_by_repeated_disposal()
    {
        var native = new Native { RemoveFails = true };
        var source = new WindowsF9Source(TimeProvider.System, native);
        await native.Installed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => source.DisposeAsync().AsTask());
        var again = await Assert.ThrowsAnyAsync<Exception>(() => source.DisposeAsync().AsTask());
        Assert.Same(failure, again);
        Assert.Equal(1, native.Removed);
    }

    private sealed class Native : IWindowsF9Native
    {
        internal readonly TaskCompletionSource Installed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool InstallFails, RemoveFails;
        internal int Forwarded, Removed;
        private F9HookProc? _callback;
        private Dispatcher? _dispatcher;
        public nint Install(F9HookProc callback)
        {
            _callback = callback; _dispatcher = Dispatcher.CurrentDispatcher;
            Installed.TrySetResult(); return InstallFails ? 0 : 1;
        }
        public bool Uninstall(nint hook) { Interlocked.Increment(ref Removed); return !RemoveFails; }
        public bool IsF9Down() => false;
        public (nint Hwnd, int Pid) Foreground() => (123, 42);
        public nint Next(int code, nuint message, nint data) { Interlocked.Increment(ref Forwarded); return 73; }
        internal async Task<nint> Send(int key, bool down)
        {
            return await _dispatcher!.InvokeAsync(() =>
            {
                var data = Marshal.AllocHGlobal(24);
                try { for(var i=0;i<24;i+=4) Marshal.WriteInt32(data,i,0); Marshal.WriteInt32(data,key);
                    return _callback!(0, down ? (nuint)0x100 : 0x101, data); }
                finally { Marshal.FreeHGlobal(data); }
            });
        }
    }
}
