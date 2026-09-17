using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BridgeManager.Modern;

public sealed partial class SetupWizardWindow
{
    private void WizardLayout_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var compact = e.NewSize.Width < 760;
        WizardSidebar.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        WizardSidebarColumn.Width = compact ? new GridLength(0) : new GridLength(250);
        WizardContentColumn.Width = new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(WizardContent, compact ? 0 : 1);
        Grid.SetColumnSpan(WizardContent, compact ? 2 : 1);
        WizardContent.Padding = compact
            ? new Thickness(18, 20, 18, 18)
            : new Thickness(34, 28, 34, 24);
        Grid.SetRow(WizardStepCounter, compact ? 1 : 0);
        Grid.SetColumn(WizardStepCounter, compact ? 0 : 1);
        WizardStepCounter.HorizontalAlignment = compact
            ? HorizontalAlignment.Left
            : HorizontalAlignment.Right;
    }
}
