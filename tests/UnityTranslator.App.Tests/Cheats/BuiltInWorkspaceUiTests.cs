using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using UnityTranslator.App.Cheats;
using UnityTranslator.Core;

namespace UnityTranslator.App.Tests.Cheats;

[Collection(WpfUiCollection.Name)]
public sealed class BuiltInWorkspaceUiTests
{
    [Fact]
    public void AddressTableButtonsWriteUndoAndStopWithTheGameSession()
    {
        Exception? failure=null;
        var directory=Path.Combine(Path.GetTempPath(),"ut-table-ui-"+Guid.NewGuid().ToString("N"));
        var thread=new Thread(()=>
        {
            MainWindow.PrepareTestDispatcherForTest();
            var window=new MainWindow(directory, new RecordingExternalFactory { Available = false });
            try
            {
                window.NavList.SelectedIndex=1;
                var apiShortcut=Assert.IsType<RadioButton>(window.FindName("ApiSettingsButton"));
                apiShortcut.IsChecked=true;
                Assert.Same(window.FindName("TranslationServicesTab"),window.WorkspaceTabs.SelectedItem);
                Assert.Equal("翻译服务",((ListBoxItem)window.NavList.SelectedItem).Content);
                Assert.Equal(Visibility.Visible,Assert.IsType<Grid>(window.FindName("ApiTranslationPanel")).Visibility);
                var memory=new UiMemory();
                var game=new GameProfile("table-test",directory,Path.Combine(directory,"Game.exe"),"2022.3","Mono","x64",true,"");
                window.StartCheatSessionForTest(game,memory);
                Assert.Equal("Vesper暮星",window.Title);
                Assert.False(window.UnityQueryButton.IsEnabled);
                var importer=typeof(MainWindow).GetMethod("ImportAddressTableText",BindingFlags.NonPublic|BindingFlags.Instance)!;
                importer.Invoke(window,["<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///C:/Windows/win.ini'>]><CheatTable><CheatEntries>&e;</CheatEntries></CheatTable>"]);
                Assert.Contains("地址表格式无效",window.AddressTableStatus.Text);
                Assert.Empty(window.AddressTableList.Items);
                window.AddressNameBox.Text="生命";window.AddressExpressionBox.Text="1000";
                Click(window,"添加到地址表");
                Assert.Single(window.AddressTableList.Items);
                window.AddressTableList.SelectedIndex=0;
                window.AddressValueBox.Text="88";Click(window,"修改选中项");
                Assert.Equal(88,memory.Value);
                Click(window,"撤销地址表修改");Assert.Equal(53,memory.Value);
                window.AddressValueBox.Text="99";Click(window,"锁定 / 解锁选中项");
                Assert.True(((CheatTableEntry)window.AddressTableList.Items[0]).Frozen);
                window.NotifyGameExitedForTest();
                Assert.Empty(window.AddressTableList.Items);
                Assert.False(window.CanWriteCheatValue);
            }
            catch(Exception ex){failure=ex;}
            finally{window.Close();}
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
        if(Directory.Exists(directory))Directory.Delete(directory,true);
        if(failure is not null)throw failure;
    }
    private static void Click(MainWindow window,string text)
    {
        // Event bindings are exercised through the actual named button, rather than a duplicate test-only implementation.
        Button? Find(DependencyObject node)
        {
            if(node is Button button && Equals(button.Content,text))return button;
            foreach(var item in LogicalTreeHelper.GetChildren(node))if(item is DependencyObject child && Find(child) is {} found)return found;
            return null;
        }
        var button=Find(window)??throw new InvalidOperationException("Missing button: "+text);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
    private sealed class UiMemory:IProcessMemory
    {
        public int Value=53;
        public IReadOnlyList<MemoryRegion> Regions()=>[new(0x1000,4,true,true)];
        public bool TryRead(ulong address,Span<byte> destination){if(address!=0x1000||destination.Length!=4)return false;BitConverter.GetBytes(Value).CopyTo(destination);return true;}
        public bool TryWrite(ulong address,ReadOnlySpan<byte> source){if(address!=0x1000||source.Length!=4)return false;Value=BitConverter.ToInt32(source);return true;}
        public bool IsAlive=>true;public int ProcessId=>41;public long StartedUtcTicks=>42;
    }
}
