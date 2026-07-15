using System.ComponentModel.Composition;
using Mendix.StudioPro.ExtensionsAPI.UI.Menu;
using Mendix.StudioPro.ExtensionsAPI.UI.Services;

namespace MendixVibeCoder;

[method:ImportingConstructor]
[Export(typeof(MenuExtension))]
public class VibeCoderMenuExtension(IDockingWindowService dockingWindowService) : MenuExtension
{
    public override IEnumerable<MenuViewModel> GetMenus()
    {
        yield return new MenuViewModel(
            "Open Vibe Coder",
            () => dockingWindowService.OpenPane(VibeCoderDockablePane.ID));
    }
}
