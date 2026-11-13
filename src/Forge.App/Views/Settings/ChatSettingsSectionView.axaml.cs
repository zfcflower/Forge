using Avalonia.Controls;

namespace Forge.App.Views.Settings;

/// <summary>
/// Body of the <c>chat</c> settings section: the thumbnail edge of tool-result images and whether they reach the
/// model. Everything is bound in markup, so the code-behind only loads the view.
/// </summary>
public partial class ChatSettingsSectionView : UserControl
{
    public ChatSettingsSectionView()
    {
        InitializeComponent();
    }
}
