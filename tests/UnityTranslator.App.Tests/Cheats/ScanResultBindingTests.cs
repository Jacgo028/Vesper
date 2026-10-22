using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using UnityTranslator.App.Cheats;
using UnityTranslator.Core;
using UnityTranslator.Core.Cheats;

namespace UnityTranslator.App.Tests.Cheats;

[Collection(WpfUiCollection.Name)]
public sealed class ScanResultBindingTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ut-scan-grid-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void FirstAndRefinedHitsAppearInCellsAndSelectedRowCanBeWritten()
    {
        OnWindow(async window =>
        {
            var memory = new ScanMemory(0x1000, 130);
            memory.SetInt32(0x1010, 130);
            window.StartCheatSessionForTest(Game("first"), memory);

            await window.ScanForTest(NumericKind.Int32, "130");

            Assert.True(window.CheatHits.Count == 2, "Scan did not produce candidates: " + window.CheatStatus.Text + " " + window.CheatStatusAdvice.Text);
            Assert.Equal(2, window.ScanResultList.Items.Count);
            Assert.Equal("0x1000", CellText(window.ScanResultList, 0, 0));
            Assert.Equal("整数", CellText(window.ScanResultList, 0, 1));
            Assert.Equal("130", CellText(window.ScanResultList, 0, 2));
            Assert.False(window.ScanWriteButton.IsEnabled);

            memory.SetInt32(0x1000, 175);
            memory.SetInt32(0x1010, 99);
            await window.RefineWithValueForTest("175");

            var row = Assert.IsType<ScanHitView>(Assert.Single(window.ScanResultList.Items));
            Assert.Equal("175", CellText(window.ScanResultList, 0, 2));
            window.ScanResultList.SelectedItem = row;
            Assert.True(window.ScanWriteButton.IsEnabled);
            Assert.True(window.ScanFreezeButton.IsEnabled);
            Assert.Equal("175", window.ScanWriteValueBox.Text);

            window.ScanWriteValueBox.Text = "777";
            window.ScanWriteButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.Equal(777, memory.ReadInt32(0x1000));
            Assert.Equal(99, memory.ReadInt32(0x1010));
            Assert.Equal("777", CellText(window.ScanResultList, 0, 2));
        });
    }

    [Fact]
    public void SwitchingSessionsRemovesDisplayedRowsAndSelectionBeforeAnotherScan()
    {
        OnWindow(async window =>
        {
            var first = new ScanMemory(0x1000, 130);
            window.StartCheatSessionForTest(Game("first"), first);
            await window.ScanForTest(NumericKind.Int32, "130");
            window.ScanResultList.SelectedItem = Assert.Single(window.ScanResultList.Items);
            Assert.True(window.ScanWriteButton.IsEnabled);

            var second = new ScanMemory(0x2000, 88);
            window.StartCheatSessionForTest(Game("second"), second);

            Assert.Empty(window.ScanResultList.Items);
            Assert.Null(window.ScanResultList.SelectedItem);
            Assert.False(window.ScanWriteButton.IsEnabled);
            Assert.False(window.ScanFreezeButton.IsEnabled);

            await window.ScanForTest(NumericKind.Int32, "88");
            var row = Assert.IsType<ScanHitView>(Assert.Single(window.ScanResultList.Items));
            Assert.Equal("0x2000", row.AddressText);
            window.ScanResultList.SelectedItem = row;
            window.ScanWriteValueBox.Text = "99";
            window.ScanWriteButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(99, second.ReadInt32(0x2000));
            Assert.Equal(130, first.ReadInt32(0x1000));
        });
    }

    [Fact]
    public void ProcessExitRemovesDisplayedRowsAndDisablesTheirActions()
    {
        OnWindow(async window =>
        {
            window.StartCheatSessionForTest(Game("first"), new ScanMemory(0x1000, 130));
            await window.ScanForTest(NumericKind.Int32, "130");
            window.ScanResultList.SelectedItem = Assert.Single(window.ScanResultList.Items);
            Assert.True(window.ScanWriteButton.IsEnabled);

            window.NotifyGameExitedForTest();

            Assert.Empty(window.ScanResultList.Items);
            Assert.Null(window.ScanResultList.SelectedItem);
            Assert.False(window.ScanStartButton.IsEnabled);
            Assert.False(window.ScanWriteButton.IsEnabled);
            Assert.False(window.ScanFreezeButton.IsEnabled);
            window.FinishTakeOverWaitForTest();
        });
    }

    private GameProfile Game(string id) => new(id, _directory, Path.Combine(_directory, id + ".exe"),
        "2022.3", "Mono", "x64", true, string.Empty);

    private static string CellText(DataGrid grid, int rowIndex, int columnIndex)
    {
        grid.ApplyTemplate();
        grid.Measure(new Size(700, 170));
        grid.Arrange(new Rect(0, 0, 700, 170));
        grid.ScrollIntoView(grid.Items[rowIndex]);
        grid.UpdateLayout();
        return Assert.IsType<TextBlock>(grid.Columns[columnIndex].GetCellContent(grid.Items[rowIndex])).Text;
    }

    private void OnWindow(Func<MainWindow, Task> body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            MainWindow? window = null;
            try
            {
                MainWindow.PrepareTestDispatcherForTest();
                window = new MainWindow(_directory, new RecordingExternalFactory { Available = false });
                window.ShowInTaskbar = false;
                window.ShowActivated = false;
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = -30000;
                window.Top = -30000;
                window.Show();
                window.NavList.SelectedIndex = 1;
                window.UpdateLayout();
                var task = body(window);
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
                while (!task.IsCompleted && DateTime.UtcNow < deadline)
                {
                    var frame = new DispatcherFrame();
                    window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
                    Dispatcher.PushFrame(frame);
                }
                if (!task.IsCompleted) throw new TimeoutException("Scan result UI test did not finish.");
                task.GetAwaiter().GetResult();
            }
            catch (Exception ex) { failure = ex; }
            finally { window?.Close(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(40)), "Scan result UI thread did not finish.");
        if (failure is not null) throw new InvalidOperationException("Scan result UI test failed.", failure);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    // Only the process-memory boundary is controlled; scanning, bindings, selection and writes are production code.
    private sealed class ScanMemory : IProcessMemory
    {
        private readonly byte[] _bytes = new byte[64];
        private readonly ulong _start;
        private readonly object _gate = new();

        public ScanMemory(ulong start, int value)
        {
            _start = start;
            SetInt32(start, value);
        }

        public int ProcessId => 71;
        public long StartedUtcTicks => 72;
        public bool IsAlive => true;
        public IReadOnlyList<MemoryRegion> Regions() => [new(_start, (ulong)_bytes.Length, true, true)];
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
                if (!Contains(address, destination.Length)) return false;
                _bytes.AsSpan((int)(address - _start), destination.Length).CopyTo(destination);
                return true;
            }
        }

        public bool TryWrite(ulong address, ReadOnlySpan<byte> source)
        {
            lock (_gate)
            {
                if (!Contains(address, source.Length)) return false;
                source.CopyTo(_bytes.AsSpan((int)(address - _start), source.Length));
                return true;
            }
        }

        private bool Contains(ulong address, int length) => address >= _start &&
            address - _start <= (ulong)_bytes.Length && (ulong)length <= (ulong)_bytes.Length - (address - _start);
    }
}
