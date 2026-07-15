using System.ComponentModel.Composition;
using Mendix.StudioPro.ExtensionsAPI.UI.DockablePane;

namespace MendixVibeCoder;

[Export(typeof(DockablePaneExtension))]
public class VibeCoderDockablePane : DockablePaneExtension
{
    public const string ID = "vibe-coder-pane";
    public override string Id => ID;

    public override DockablePaneViewModelBase Open() =>
        new VibeCoderWebViewViewModel(WebServerBaseUrl, () => CurrentApp);
}
