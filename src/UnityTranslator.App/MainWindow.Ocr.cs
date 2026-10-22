using System.Threading;
using System.Windows;
using System.Windows.Controls;
using UnityTranslator.Core;
using UnityTranslator.App.ExternalOcr;

namespace UnityTranslator.App;

public partial class MainWindow
{
    private bool _updatingOcrPresentation;
    private int _ocrStateRevision;
    private OcrSessionCapability? _ocrSessionCapability;

    private sealed record OcrSessionCapability(string SessionId, string GameId, string? PackageName, bool Supported);

    private void PublishOcrSessionCapability(GameProfile game)
    {
        // Loader preference lives in the UI-owned mutable library. Resolve it
        // only while establishing/rebinding the actual session on that thread.
        Dispatcher.VerifyAccess();
        var session = CheatSessionId;
        if (string.IsNullOrEmpty(session))
        {
            Volatile.Write(ref _ocrSessionCapability, null);
            return;
        }
        var preferred = PreferredLoaderFamily(game);
        Volatile.Write(ref _ocrSessionCapability, new OcrSessionCapability(session, game.GameId,
            PackageCatalog.ResolvePackageName(game, preferred), PackageCatalog.SupportsEmbeddedOcr(game, preferred)));
    }

    private void UpdateOcrPreferencePresentation(GameProfile? game)
    {
        var package = game is null ? null : PackageCatalog.ResolvePackageName(game, PreferredLoaderFamily(game));
        var availability = game is null ? new ExternalOcrAvailability(false, "") : ExternalOcrAvailability();
        var mode = OcrModeSelector.Select(package, availability);
        var supported = mode != OcrPresentationMode.Unavailable;
        _updatingOcrPresentation = true;
        try
        {
            EnableOcrBox.IsEnabled = supported;
            // _ocrEnabled is the user's retained preference, independent of the
            // currently inspected library row and its temporary disabled state.
            EnableOcrBox.IsChecked = supported && _ocrEnabled;
            OcrPreferenceHint.Text = game is null
                ? "选择一款游戏，查看识图翻译支持情况"
                : mode == OcrPresentationMode.Embedded ? "使用本地 OCR 识别游戏内的图片文字，游戏内按 F9 切换"
                : mode == OcrPresentationMode.External
                    ? "当前游戏不支持嵌入式识图翻译，将使用外部覆盖；仅窗口化／无边框模式。游戏前台按 F9 切换；可能与游戏原有按键冲突。"
                : "当前游戏版本不支持嵌入式识图翻译" +
                    (package is "il2cpp-bepinex6" or "legacy-mono" ? "；外部覆盖不可用：" + availability.Reason : "");
            System.Windows.Automation.AutomationProperties.SetHelpText(EnableOcrBox, OcrPreferenceHint.Text);
        }
        finally { _updatingOcrPresentation = false; }
    }

    private void OnOcrChanged(object sender, RoutedEventArgs e)
    {
        if (!_libraryPresentationReady || _updatingOcrPresentation) return;
        var game = SelectedLibraryGame;
        if (game is null || OcrModeSelector.Select(PackageCatalog.ResolvePackageName(game, PreferredLoaderFamily(game)),
                ExternalOcrAvailability()) == OcrPresentationMode.Unavailable)
        {
            UpdateOcrPreferencePresentation(game);
            return;
        }
        var enabled = sender is CheckBox { IsChecked: true };
        if (_ocrEnabled == enabled) return;
        _ocrEnabled = enabled;
        InvalidateOcrRequests();
        _externalOcrController?.SetEnabled(enabled);
    }

    private void InvalidateOcrRequests() => Interlocked.Increment(ref _ocrStateRevision);

    private bool CanProcessSessionOcr(NamedPipeHost host, OcrTranslationService service, string? session, int revision)
    {
        var game = _activeGame;
        var capability = Volatile.Read(ref _ocrSessionCapability);
        return _ocrEnabled && !_closing && !_processLost && game is not null &&
            !string.IsNullOrEmpty(session) && session == CheatSessionId &&
            capability is { Supported: true } && capability.SessionId == session && capability.GameId == game.GameId &&
            revision == Volatile.Read(ref _ocrStateRevision) &&
            ReferenceEquals(host, _host) && ReferenceEquals(service, _ocrService) &&
            _ocrWorker is { IsAvailable: true };
    }
}
