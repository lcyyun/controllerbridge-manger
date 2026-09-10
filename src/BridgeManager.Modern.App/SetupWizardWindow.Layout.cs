using Microsoft.UI.Xaml;

namespace BridgeManager.Modern;

public sealed partial class SetupWizardWindow
{
    private void WizardRoot_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var compact = e.NewSize.Width < 850;
        WizardRail.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        WizardRailColumn.Width = new GridLength(compact ? 0 : 250);
        WizardBody.Padding = compact ? new Thickness(20, 20, 20, 16)
            : new Thickness(34, 28, 34, 24);
    }
}
