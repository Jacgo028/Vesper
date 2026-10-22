using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using UnityTranslator.App.Cheats;
using UnityTranslator.Core.Cheats;

namespace UnityTranslator.App;

public partial class MainWindow
{
    private static ulong ParseScanAddress(string text,ulong fallback)
    {
        if(string.IsNullOrWhiteSpace(text))return fallback;
        text=text.Trim();if(text.StartsWith("0x",StringComparison.OrdinalIgnoreCase))text=text[2..];
        if(!ulong.TryParse(text,NumberStyles.HexNumber,CultureInfo.InvariantCulture,out var address))
            throw new ArgumentException("扫描地址必须是十六进制数字，例如 7FF01234；留空表示全部。");
        return address;
    }

    private void StartAddressTable()
    {
        if(_processMemory is null)return;
        _addressTable?.Dispose();
        var pointerSize=_processMemory is Win32ProcessMemory real?real.PointerSize:(_activeGame?.Architecture=="x86"?4:8);
        _addressTable=new MemoryAddressTable(_processMemory,pointerSize);
        AddressTableList.ItemsSource=_addressEntries;
        _addressRefreshTimer?.Stop();
        _addressRefreshTimer=new DispatcherTimer(DispatcherPriority.Background,Dispatcher){Interval=TimeSpan.FromMilliseconds(500)};
        _addressRefreshTimer.Tick+=(_,_)=>{if(_addressEntries.Count>0 && _scanStop is null && !_processLost)RefreshAddressTable();};
        _addressRefreshTimer.Start();
    }

    private void ResetBuiltInTools()
    {
        _scanStop?.Cancel();
        _addressRefreshTimer?.Stop();_addressRefreshTimer=null;
        _addressTable?.Dispose();_addressTable=null;_addressEntries.Clear();
        _scanWorkspace.Dispose();_scanWorkspace=new MemoryScanWorkspace();_totalScanHits=0;
        if(AddressTableStatus is not null)AddressTableStatus.Text="地址表已清空，游戏退出或切换后需要重新添加 / 导入；锁定已停止。";
    }

    private MemoryAddressTable RequireAddressTable()
    {
        if(_addressTable is null || _processLost || _processMemory?.IsAlive!=true)
            throw new InvalidOperationException("还没有连接正在运行的游戏。先到“游戏与翻译”页启动或连接游戏。");
        return _addressTable;
    }
    private CheatTableEntry SelectedAddressEntry() => AddressTableList.SelectedItem is CheatTableEntry {IsGroup:false} entry
        ?entry:throw new InvalidOperationException("请先在地址表中选中一条数字地址，分组不能修改或锁定。");
    private int NextAddressId()=>_addressEntries.Count==0?1:_addressEntries.Max(x=>x.Id)+1;
    private void AddressAction(Action action)
    {
        try{action();}
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or System.Xml.XmlException or ArgumentException or InvalidOperationException or OverflowException or FormatException or System.ComponentModel.Win32Exception)
        {
            AddressTableStatus.Text=ex is System.Xml.XmlException
                ?"地址表格式无效，或包含不允许的 XML 声明。请选择普通 .CT 文件；原有地址表未被更改。"
                :ex is UnauthorizedAccessException?"没有权限读取或保存这个地址表，请换到有读写权限的文件夹。":ex.Message;
            Log("地址表："+ex.GetType().Name+"："+ex.Message);
        }
    }
    private void RefreshAddressTable()
    {
        AddressAction(()=>
        {
            RequireAddressTable().RefreshEntries();
            AddressTableList.Items.Refresh();
        });
    }
    private void OnRefreshAddressTable(object sender,RoutedEventArgs e)=>RefreshAddressTable();
    private void OnAddressSelectionChanged(object sender,SelectionChangedEventArgs e)
    {if(AddressTableList.SelectedItem is CheatTableEntry entry)AddressValueBox.Text=entry.Value;}
    private void OnAddScanToAddressTable(object sender,RoutedEventArgs e)=>AddressAction(()=>
    {
        if(_selectedHit is null)throw new InvalidOperationException("请先在扫描结果里选中一行。");
        var entry=new CheatTableEntry{Id=NextAddressId(),Description=string.IsNullOrWhiteSpace(AddressNameBox.Text)?"扫描地址 "+_selectedHit.AddressText:AddressNameBox.Text,
            Address=_selectedHit.Hit.Address.ToString("X"),Kind=_selectedHit.Hit.Kind};
        RequireAddressTable().AddEntry(entry);_addressEntries.Add(entry);RefreshAddressTable();
        AddressTableStatus.Text="已加入地址表。可以独立修改，也可以同时锁定多项。";
    });
    private void OnAddManualAddress(object sender,RoutedEventArgs e)=>AddressAction(()=>
    {
        var entry=new CheatTableEntry{Id=NextAddressId(),Description=string.IsNullOrWhiteSpace(AddressNameBox.Text)?"手动地址":AddressNameBox.Text.Trim(),
            Address=AddressExpressionBox.Text.Trim(),Kind=UiText.KindAt(AddressKindBox.SelectedIndex),
            Offsets=AddressOffsetsBox.Text.Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).Select(CheatTableDocument.ParseOffset).ToArray()};
        RequireAddressTable().AddEntry(entry);_addressEntries.Add(entry);RefreshAddressTable();
        AddressTableStatus.Text="已添加地址。显示 ?? 时表示当前无法读取，请核对地址、偏移和类型。";
    });
    private void OnWriteAddressEntry(object sender,RoutedEventArgs e)=>AddressAction(()=>
    {
        var entry=SelectedAddressEntry();
        var value=RequireAddressTable().SetEntryValue(entry.Id,AddressValueBox.Text);
        RefreshAddressTable();AddressTableStatus.Text=$"“{entry.Description}”已改成 {value}，已读回核对。";
    });
    private void OnFreezeAddressEntry(object sender,RoutedEventArgs e)=>AddressAction(()=>
    {
        var entry=SelectedAddressEntry();var freeze=!entry.Frozen;var table=RequireAddressTable();
        if(freeze)table.SetEntryValue(entry.Id,AddressValueBox.Text);
        table.SetEntryFrozen(entry.Id,freeze);RefreshAddressTable();
        AddressTableStatus.Text=freeze?$"“{entry.Description}”已锁定。其他地址的锁定继续生效。":$"“{entry.Description}”已解除锁定。";
    });
    private void OnStopAllAddressFreezes(object sender,RoutedEventArgs e)=>AddressAction(()=>
    {
        var table=RequireAddressTable();foreach(var entry in _addressEntries.Where(x=>!x.IsGroup))table.SetEntryFrozen(entry.Id,false);
        _cheatSession?.StopFreeze();_frozenHit=null;ScanFreezeButton.Content="锁定这个数值";
        RefreshAddressTable();AddressTableStatus.Text="全部锁定已停止。";
    });
    private void OnUndoAddressEntry(object sender,RoutedEventArgs e)=>AddressAction(()=>
    {
        var result=RequireAddressTable().UndoLast();RefreshAddressTable();AddressTableStatus.Text=result.Message;
    });
    private void OnRemoveAddressEntry(object sender,RoutedEventArgs e)=>AddressAction(()=>
    {
        if(AddressTableList.SelectedItem is not CheatTableEntry entry)throw new InvalidOperationException("先选中要移除的地址或分组。");
        var remove=new HashSet<int>{entry.Id};
        foreach(var candidate in _addressEntries)if(candidate.ParentId is int parent && remove.Contains(parent))remove.Add(candidate.Id);
        RequireAddressTable().RemoveEntry(entry.Id);
        foreach(var candidate in _addressEntries.Where(x=>remove.Contains(x.Id)).ToArray())_addressEntries.Remove(candidate);
        AddressTableStatus.Text=$"已移除 {remove.Count} 项，相应锁定已停止。";
    });
    private void OnImportCheatTable(object sender,RoutedEventArgs e)=>AddressAction(()=>
    {
        RequireAddressTable();
        var dialog=new OpenFileDialog{Title="导入 CE 数字地址表（不执行脚本）",Filter="Cheat Engine 地址表|*.ct",CheckFileExists=true};
        if(dialog.ShowDialog(this)!=true)return;
        if(new FileInfo(dialog.FileName).Length>4*1024*1024)throw new IOException("地址表超过 4 MB，请拆分后导入。");
        ImportAddressTableText(File.ReadAllText(dialog.FileName));
    });
    private void ImportAddressTableText(string xml)=>AddressAction(()=>ImportAddressTable(CheatTableDocument.Parse(xml)));
    private void ImportAddressTable(CheatTableDocument document)
    {
        var table=RequireAddressTable();var map=new Dictionary<int,int>();
        foreach(var entry in document.Entries)
        {
            var old=entry.Id;entry.Id=NextAddressId();map[old]=entry.Id;
            entry.ParentId=entry.ParentId is int parent && map.TryGetValue(parent,out var mapped)?mapped:null;
            table.AddEntry(entry);_addressEntries.Add(entry);
        }
        RefreshAddressTable();
        AddressTableStatus.Text=$"已导入 {document.Entries.Count} 项。"+
            (document.SkippedScripts>0?$" 已跳过 {document.SkippedScripts} 个脚本条目。":"")+
            (document.HasLuaScript?" 已忽略表内 Lua 代码。":"")+
            (document.SkippedUnsupported>0?$" 已跳过 {document.SkippedUnsupported} 个暂不支持的类型（仅支持四种数字类型）。":"")+
            " 所有锁定默认关闭，未执行任何表内代码或热键。";
    }
    private void OnExportCheatTable(object sender,RoutedEventArgs e)=>AddressAction(()=>
    {
        if(_addressEntries.Count==0)throw new InvalidOperationException("地址表为空，请先添加或导入地址。");
        var dialog=new SaveFileDialog{Title="导出数字地址表",Filter="Cheat Engine 地址表|*.ct",DefaultExt=".ct",FileName="游戏地址表.ct"};
        if(dialog.ShowDialog(this)!=true)return;
        var document=new CheatTableDocument();document.Entries.AddRange(_addressEntries);
        File.WriteAllText(dialog.FileName,document.ToXml(),new System.Text.UTF8Encoding(false));
        AddressTableStatus.Text="已导出 .CT。保存地址、类型、指针与分组，不保存锁定状态或运行代码。";
    });
}
