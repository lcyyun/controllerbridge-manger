using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BridgeManager.Modern;

public sealed partial class MainWindow
{
    private void PageContentHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var contentWidth = Math.Min(1180, Math.Max(0, e.NewSize.Width - 12));
        HomePageContent.Width = contentWidth;
        InputPageContent.Width = contentWidth;
        FeedbackPageContent.Width = contentWidth;
        FirmwarePageContent.Width = contentWidth;
        AdvancedPageContent.Width = contentWidth;
        DynamicModulePageContent.Width = contentWidth;
    }

    private void MainNavigation_SizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateResponsiveLayout(e.NewSize.Width);

    private void UpdateResponsiveLayout(double width)
    {
        var compact = width < 780;
        var singleColumn = width < 980;
        var phone = width < 600;

        var paneMode = width < 720
            ? NavigationViewPaneDisplayMode.LeftCompact
            : NavigationViewPaneDisplayMode.Auto;
        if (MainNavigation.PaneDisplayMode != paneMode)
            MainNavigation.PaneDisplayMode = paneMode;

        PageShell.Padding = compact
            ? new Thickness(12, 16, 8, 0)
            : new Thickness(24, 20, 12, 0);

        ManagerUpdateActionColumn.Width = compact ? new GridLength(0) :
            GridLength.Auto;
        Place(ManagerUpdateActions, compact ? 1 : 0, compact ? 1 : 2);
        ManagerUpdateActions.HorizontalAlignment = compact
            ? HorizontalAlignment.Left : HorizontalAlignment.Right;

        Place(HeaderActions, compact ? 1 : 0, compact ? 0 : 1);
        HeaderActions.HorizontalAlignment = compact ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        Place(HomeSelfTestButton, compact ? 1 : 0, compact ? 0 : 1);
        HomeSelfTestButton.HorizontalAlignment = compact ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;

        SetHomeMetrics(compact);
        SetInputHealth(compact);
        SetInputLayout(singleColumn, phone);
        SetFeedbackLayout(compact, phone);
        SetFirmwareLayout(singleColumn);
        SetTuningLayout(compact);
        SetAdvancedLayout(singleColumn);
    }

    private void SetHomeMetrics(bool compact)
    {
        HomeMetricColumn1.Width = new GridLength(1, GridUnitType.Star);
        HomeMetricColumn2.Width = compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        HomeMetricColumn3.Width = compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);

        Place(RoleMetricPanel, 0, 0);
        Place(InputMetricPanel, compact ? 1 : 0, compact ? 0 : 1);
        Place(UsbMetricPanel, compact ? 2 : 0, compact ? 0 : 2);
        InputMetricPanel.Padding = compact ? new Thickness(0, 16, 0, 0) : new Thickness(24, 0, 0, 0);
        UsbMetricPanel.Padding = compact ? new Thickness(0, 16, 0, 0) : new Thickness(24, 0, 0, 0);
        InputMetricPanel.BorderThickness = compact ? new Thickness(0, 1, 0, 0) : new Thickness(1, 0, 0, 0);
        UsbMetricPanel.BorderThickness = compact ? new Thickness(0, 1, 0, 0) : new Thickness(1, 0, 0, 0);
    }

    private void SetInputHealth(bool compact)
    {
        Place(InputHealthPanel, 0, 0);
        Place(BatteryText, compact ? 1 : 0, compact ? 0 : 1);
        Place(InputHealthRefreshButton, 0, 2);
    }

    private void SetInputLayout(bool singleColumn, bool phone)
    {
        InputMainColumn.Width = new GridLength(1, GridUnitType.Star);
        InputDetailColumn.Width = singleColumn ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Place(InputPrimaryPanel, 0, 0);
        Place(InputDetailPanel, singleColumn ? 1 : 0, singleColumn ? 0 : 1);

        LeftStickColumn.Width = new GridLength(1, GridUnitType.Star);
        RightStickColumn.Width = phone ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Place(LeftStickPanel, 0, 0);
        Place(RightStickPanel, phone ? 1 : 0, phone ? 0 : 1);
    }

    private void SetFeedbackLayout(bool compact, bool phone)
    {
        FeedbackColumn1.Width = new GridLength(1, GridUnitType.Star);
        FeedbackColumn2.Width = phone ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        FeedbackColumn3.Width = compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        FeedbackColumn4.Width = compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        if (!compact)
        {
            Place(RumbleLeftButton, 0, 0);
            Place(RumbleRightButton, 0, 1);
            Place(RumbleBothButton, 0, 2);
            Place(RumbleStopButton, 0, 3);
            return;
        }

        Place(RumbleLeftButton, 0, 0);
        Place(RumbleRightButton, phone ? 1 : 0, phone ? 0 : 1);
        Place(RumbleBothButton, phone ? 2 : 1, 0);
        Place(RumbleStopButton, phone ? 3 : 1, phone ? 0 : 1);
    }

    private void SetTuningLayout(bool compact)
    {
        TuningColumn1.Width = new GridLength(1, GridUnitType.Star);
        TuningColumn2.Width = compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Place(ScaleBox, 0, 0);
        Place(HoldBox, compact ? 1 : 0, compact ? 0 : 1);
        Place(TickBox, compact ? 2 : 1, 0);
        Place(StopsBox, compact ? 3 : 1, compact ? 0 : 1);
        Place(ApplyTuningButton, compact ? 4 : 2, 0);
        Grid.SetColumnSpan(ApplyTuningButton, compact ? 1 : 2);
    }

    private void SetFirmwareLayout(bool singleColumn)
    {
        FirmwareUpdateColumn.Width = new GridLength(1.05, GridUnitType.Star);
        FirmwareFlashColumn.Width = singleColumn ? new GridLength(0) :
            new GridLength(1, GridUnitType.Star);
        Place(FirmwareUpdatePanel, 0, 0);
        Place(FirmwareFlashPanel, singleColumn ? 1 : 0, singleColumn ? 0 : 1);
    }

    private void SetAdvancedLayout(bool singleColumn)
    {
        AdvancedLeftColumn.Width = new GridLength(1.1, GridUnitType.Star);
        AdvancedRightColumn.Width = singleColumn ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Place(AdvancedDevicesPanel, 0, 0);
        Place(DeviceSettingsPanel, singleColumn ? 1 : 0, singleColumn ? 0 : 1);
    }

    private static void Place(FrameworkElement element, int row, int column)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
    }
}
