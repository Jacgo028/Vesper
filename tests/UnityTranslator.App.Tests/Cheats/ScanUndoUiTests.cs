using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using UnityTranslator.App.Cheats;
using UnityTranslator.Core;
using UnityTranslator.Core.Cheats;

namespace UnityTranslator.App.Tests.Cheats;

[Collection(WpfUiCollection.Name)]
public sealed class ScanUndoUiTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ut-scan-undo-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void UndoRestoresCellsAndExpectedBytesForAnImmediateWrite()
    {
        OnWindow(async (window, memory) =>
        {
            var row = await ScanAndSelect(window);
            Write(window, "99");
            Assert.Equal("99", CellText(window, row));
            window.ScanWriteValueBox.Text = "123";

            Click(window.ScanUndoButton);

            Assert.Equal(53, memory.ReadInt32(0x1000));
            Assert.Equal("53", CellText(window, row));
            Assert.Equal(BitConverter.GetBytes(53), row.Hit.LastBytes);
            Assert.Equal("123", window.ScanWriteValueBox.Text);
            Click(window.ScanWriteButton);
            Assert.Equal(123, memory.ReadInt32(0x1000));
            Assert.Equal("123", CellText(window, row));
        });
    }

    [Fact]
    public void UndoAllowsImmediateFreezeWithoutRefiningOrReselecting()
    {
        OnWindow(async (window, memory) =>
        {
            await ScanAndSelect(window);
            Write(window, "99");
            Click(window.ScanUndoButton);
            window.ScanWriteValueBox.Text = "80";

            Click(window.ScanFreezeButton);

            Assert.True(window.IsFreezing, window.CheatStatus.Text);
            try
            {
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
                while (memory.ReadInt32(0x1000) != 80 && DateTime.UtcNow < deadline) await Task.Delay(10);
                Assert.Equal(80, memory.ReadInt32(0x1000));
            }
            finally { Click(window.ScanFreezeButton); }
            Assert.False(window.IsFreezing);
        });
    }

    [Fact]
    public void UndoUpdatesTheWrittenRowWhenSelectionChangesAndKeepsUnrelatedRowsGuarded()
    {
        OnWindow(async (window, memory) =>
        {
            memory.SetInt32(0x1010, 53);
            await window.ScanForTest(NumericKind.Int32, "53");
            var original = Assert.IsType<ScanHitView>(window.ScanResultList.Items[0]);
            var unrelated = Assert.IsType<ScanHitView>(window.ScanResultList.Items[1]);
            window.ScanResultList.SelectedItem = original;
            Write(window, "99");
            window.ScanResultList.SelectedItem = unrelated;
            window.ScanWriteValueBox.Text = "147";
            memory.SetInt32(0x1010, 66);

            Click(window.ScanUndoButton);

            Assert.Same(unrelated, window.ScanResultList.SelectedItem);
            Assert.Equal("147", window.ScanWriteValueBox.Text);
            Assert.Equal("53", CellText(window, original));
            Assert.Equal(BitConverter.GetBytes(53), original.Hit.LastBytes);
            Assert.Equal("53", CellText(window, unrelated));
            Assert.Equal(BitConverter.GetBytes(53), unrelated.Hit.LastBytes);
            Assert.Equal(66, memory.ReadInt32(0x1010));
            Write(window, "88");
            Assert.Equal(66, memory.ReadInt32(0x1010));
            window.ScanResultList.SelectedItem = original;
            Write(window, "80");
            Assert.Equal(80, memory.ReadInt32(0x1000));
        });
    }

    [Theory]
    [InlineData("changed", "游戏已再次修改此数值")]
    [InlineData("unreadable", "目标地址已不可读取")]
    [InlineData("process", "游戏进程已退出或发生变化")]
    public void FailedUndoPreservesExpectedBytesAndReportsItsActualReason(string failure, string expectedReason)
    {
        OnWindow(async (window, memory) =>
        {
            var row = await ScanAndSelect(window);
            Write(window, "99");
            if (failure == "changed") memory.SetInt32(0x1000, 77);
            else if (failure == "unreadable") memory.Readable = false;
            else memory.StartedUtcTicks++;

            Click(window.ScanUndoButton);

            Assert.Equal("99", CellText(window, row));
            Assert.Equal(BitConverter.GetBytes(99), row.Hit.LastBytes);
            Assert.Contains(expectedReason, window.CheatStatus.Text);
            Assert.DoesNotContain("没有成功修改", window.CheatStatusAdvice.Text);
            memory.Readable = true;
            Assert.Equal(failure == "changed" ? 77 : 99, memory.ReadInt32(0x1000));
        });
    }

    private static async Task<ScanHitView> ScanAndSelect(MainWindow window)
    {
        await window.ScanForTest(NumericKind.Int32, "53");
        var row = Assert.IsType<ScanHitView>(Assert.Single(window.ScanResultList.Items));
        window.ScanResultList.SelectedItem = row;
        Assert.True(window.ScanWriteButton.IsEnabled);
        return row;
    }

    private static void Write(MainWindow window, string value)
    {
        window.ScanWriteValueBox.Text = value;
        Click(window.ScanWriteButton);
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static string CellText(MainWindow window, ScanHitView row)
    {
        var grid = window.ScanResultList;
        grid.ScrollIntoView(row);
        grid.UpdateLayout();
        return Assert.IsType<TextBlock>(grid.Columns[2].GetCellContent(row)).Text;
    }

    private void OnWindow(Func<MainWindow, UndoMemory, Task> body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            MainWindow? window = null;
            try
            {
                MainWindow.PrepareTestDispatcherForTest();
                window = new MainWindow(_directory, new RecordingExternalFactory { Available = false })
                {
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -30000,
                    Top = -30000
                };
                window.Show();
                window.NavList.SelectedIndex = 1;
                window.UpdateLayout();
                var memory = new UndoMemory();
                var game = new GameProfile("undo", _directory, Path.Combine(_directory, "Game.exe"),
                    "2022.3", "Mono", "x64", true, string.Empty);
                window.StartCheatSessionForTest(game, memory);
                var task = body(window, memory);
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
                while (!task.IsCompleted && DateTime.UtcNow < deadline)
                {
                    var frame = new DispatcherFrame();
                    window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
                    Dispatcher.PushFrame(frame);
                }
                if (!task.IsCompleted) throw new TimeoutException("Scan undo UI test did not finish.");
                task.GetAwaiter().GetResult();
            }
            catch (Exception ex) { failure = ex; }
            finally { window?.Close(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(40)), "Scan undo UI thread did not finish.");
        if (failure is not null) throw new InvalidOperationException("Scan undo UI test failed.", failure);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed class UndoMemory : IProcessMemory
    {
        private readonly byte[] _bytes = new byte[64];
        private readonly object _gate = new();
        public UndoMemory() => SetInt32(0x1000, 53);
        public int ProcessId => 71;
        public long StartedUtcTicks { get; set; } = 72;
        public bool IsAlive => true;
        public bool Readable { get; set; } = true;
        public IReadOnlyList<MemoryRegion> Regions() => [new(0x1000, (ulong)_bytes.Length, true, true)];
        public void SetInt32(ulong address, int value) => TryWrite(address, BitConverter.GetBytes(value));
        public int ReadInt32(ulong address)
        {
            var bytes = new byte[4];
            Assert.True(TryRead(address, bytes));
            return BitConverter.ToInt32(bytes);
        }
        public bool TryRead(ulong address, Span<byte> destination)
        {
            lock (_gate)
            {
                if (!Readable || !Contains(address, destination.Length)) return false;
                _bytes.AsSpan((int)(address - 0x1000), destination.Length).CopyTo(destination);
                return true;
            }
        }
        public bool TryWrite(ulong address, ReadOnlySpan<byte> source)
        {
            lock (_gate)
            {
                if (!Contains(address, source.Length)) return false;
                source.CopyTo(_bytes.AsSpan((int)(address - 0x1000), source.Length));
                return true;
            }
        }
        private bool Contains(ulong address, int length) => address >= 0x1000 &&
            address - 0x1000 <= (ulong)_bytes.Length && (ulong)length <= (ulong)_bytes.Length - (address - 0x1000);
    }
}
