using System.ComponentModel.Composition;
using Mendix.StudioPro.ExtensionsAPI.UI.DockablePane;

namespace MendixVibeCoder;

[Export(typeof(DockablePaneExtension))]
public class VibeCoderDockablePane : DockablePaneExtension
{
    public const string ID = "vibe-coder-pane";
    public override string Id => ID;

    private VibeCoderWebViewViewModel? _currentViewModel;

    public VibeCoderDockablePane()
    {
        OnWebServerBaseUrlChanged += HandleWebServerBaseUrlChanged;
    }

    public override DockablePaneViewModelBase Open()
    {
        _currentViewModel = new VibeCoderWebViewViewModel(WebServerBaseUrl, () => CurrentApp);
        return _currentViewModel;
    }

    private void HandleWebServerBaseUrlChanged()
    {
        _currentViewModel?.UpdateBaseUri(WebServerBaseUrl);
    }
}
