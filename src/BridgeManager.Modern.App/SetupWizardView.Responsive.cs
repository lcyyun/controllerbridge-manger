using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace BridgeManager.Modern;

public sealed partial class SetupWizardView
{
    private void CreationCard_ContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue) StyleCreationCard(args.ItemContainer);
    }

    private void RefreshCreationSelections()
    {
        foreach (var list in new[] { WizardBoardList, WizardFirmwareList })
        {
            if (list is null) continue;
            for (var index = 0; index < list.Items.Count; index++)
                if (list.ContainerFromIndex(index) is SelectorItem item) StyleCreationCard(item);
        }
    }

    private void StyleCreationCard(Microsoft.UI.Xaml.Controls.Primitives.SelectorItem item)
    {
        // Explicit selection outline stays visible even when the default GridView
        // template paints its own selected fill. Native focus/keyboard behavior remains.
        var brush = (Brush)Application.Current.Resources[
            item.IsSelected ? "BridgeAccentInkBrush" : "BridgeStrokeBrush"];
        var thickness = new Thickness(item.IsSelected ? 2 : 1);
        if (!ReferenceEquals(item.BorderBrush, brush)) item.BorderBrush = brush;
        if (item.BorderThickness != thickness) item.BorderThickness = thickness;
    }

    private void WizardLayout_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var compact = e.NewSize.Width < 600;
        WizardContent.Padding = compact
            ? new Thickness(18, 18, 6, 16)
            : new Thickness(28, 24, 16, 20);
        WizardContent.RowSpacing = compact ? 16 : 22;
        WizardWelcomeTitle.FontSize = compact ? 21 : 24;
        WizardProgress.Spacing = e.NewSize.Width < 400 ? 14 : 18;
    }
}
