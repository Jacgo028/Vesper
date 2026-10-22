using System.Windows;
using System.Windows.Media;

namespace UnityTranslator.App.Tests;

[Collection(WpfUiCollection.Name)]
public sealed class MainWindowAddGameTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "unity-translator-addgame-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void AddingAGameShowsItAsACard()
    {
        var exe = CreateMonoGame();
        var errors = new List<string>();

        var added = RunOnUiThread(() =>
        {
            var window = new MainWindow(Path.Combine(_root, "appdata"), new RecordingExternalFactory { Available = false });
            window.MessageBoxHandler = message => { errors.Add(message); return MessageBoxResult.OK; };
            try
            {
                window.AddGame(exe);
                return window.Games.Count;
            }
            finally { window.Close(); }
        });

        Assert.Empty(errors);
        Assert.Equal(1, added);
    }

    [Fact]
    public void AddingTheSameGameTwiceKeepsOneCard()
    {
        var exe = CreateMonoGame();
        var errors = new List<string>();

        var added = RunOnUiThread(() =>
        {
            var window = new MainWindow(Path.Combine(_root, "appdata"), new RecordingExternalFactory { Available = false });
            window.MessageBoxHandler = message => { errors.Add(message); return MessageBoxResult.OK; };
            try
            {
                window.AddGame(exe);
                window.AddGame(exe);
                window.AddGame(Path.GetDirectoryName(exe)!);
                return window.Games.Count;
            }
            finally { window.Close(); }
        });

        Assert.Empty(errors);
        Assert.Equal(1, added);
    }

    /// <summary>
    /// 回归：同一款游戏反复加入时，界面上的游戏列表会被“更新”路径再次命中。
    /// 以前用 Remove + Insert 实现，集合变更通知期间会让列表索引失效并抛
    /// ArgumentOutOfRangeException，表现为偶发失败。这里连续加入 30 次做固定复现。
    /// </summary>
    [Fact]
    public void AddingTheSameGameRepeatedlyStaysStable()
    {
        var exe = CreateMonoGame();
        var errors = new List<string>();

        var count = RunOnUiThread(() =>
        {
            var window = new MainWindow(Path.Combine(_root, "appdata"), new RecordingExternalFactory { Available = false });
            window.MessageBoxHandler = message => { errors.Add(message); return MessageBoxResult.OK; };
            try
            {
                for (var i = 0; i < 30; i++) window.AddGame(exe);
                return window.Games.Count;
            }
            finally { window.Close(); }
        });

        Assert.Empty(errors);
        Assert.Equal(1, count);
    }

    [Fact]
    public void AddingAGameShowsFeedbackInsideTheDropZone()
    {
        var exe = CreateMonoGame();
        var errors = new List<string>();

        var feedback = RunOnUiThread(() =>
        {
            var window = new MainWindow(Path.Combine(_root, "appdata"), new RecordingExternalFactory { Available = false });
            window.MessageBoxHandler = message => { errors.Add(message); return MessageBoxResult.OK; };
            try
            {
                window.AddGame(exe);
                // Success must be expressed in text and use the current semantic theme brush.
                return (Title: window.DropResultTitle.Text,
                        Icon: window.DropResultIcon.Text,
                        Opacity: window.DropResultPanel.Opacity,
                        UsesSuccess: ReferenceEquals(window.FindResource("SuccessBorder"), window.DropZone.BorderBrush));
            }
            finally { window.Close(); }
        });

        Assert.Empty(errors);
        Assert.Contains("Game", feedback.Title);
        Assert.Equal(1d, feedback.Opacity);
        Assert.True(feedback.UsesSuccess);
    }

    [Fact]
    public void IncompatibleGameShowsFailureFeedbackInsteadOfSuccess()
    {
        var exe = CreateMonoGame();
        File.Delete(Path.Combine(_root, "Game", "UnityPlayer.dll"));
        var errors = new List<string>();

        var feedback = RunOnUiThread(() =>
        {
            var window = new MainWindow(Path.Combine(_root, "appdata"), new RecordingExternalFactory { Available = false });
            window.MessageBoxHandler = message => { errors.Add(message); return MessageBoxResult.OK; };
            try
            {
                window.AddGame(exe);
                return (Title: window.DropResultTitle.Text,
                        Detail: window.DropResultDetail.Text,
                        UsesDanger: ReferenceEquals(window.FindResource("DangerBorder"), window.DropZone.BorderBrush));
            }
            finally { window.Close(); }
        });

        Assert.Empty(errors);
        Assert.Contains("暂时不能安装", feedback.Title);
        // 面向玩家的说明先说人话，技术文件名仍然保留，方便排查。
        Assert.Contains("下载完整", feedback.Detail);
        Assert.True(feedback.UsesDanger);
    }

    /// <summary>WPF 控件必须在 STA 线程上创建。</summary>
    private static T RunOnUiThread<T>(Func<T> body)
    {
        T result = default!;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { result = body(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "界面线程超时未结束");
        if (failure is not null) throw new InvalidOperationException("调用 AddGame 时抛出异常", failure);
        return result;
    }

    private string CreateMonoGame()
    {
        var gameRoot = Path.Combine(_root, "Game");
        Directory.CreateDirectory(Path.Combine(gameRoot, "Game_Data", "Managed"));
        var exe = Path.Combine(gameRoot, "Game.exe");
        var pe = new byte[256];
        pe[0] = (byte)'M';
        pe[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(pe, 0x3c);
        pe[0x80] = (byte)'P';
        pe[0x81] = (byte)'E';
        BitConverter.GetBytes((ushort)0x8664).CopyTo(pe, 0x84);
        File.WriteAllBytes(exe, pe);
        File.WriteAllBytes(Path.Combine(gameRoot, "UnityPlayer.dll"), [1]);
        File.WriteAllBytes(Path.Combine(gameRoot, "Game_Data", "Managed", "Assembly-CSharp.dll"), [1]);
        return exe;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
