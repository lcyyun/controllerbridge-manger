using BridgeManager.Core.FirmwareModules;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BridgeManager.Modern;

public sealed partial class MainWindow
{
    private string _mappingInput = "ds5";
    private string _mappingOutput = "ds5";
    private ComboBox? _mappingInputChoice;
    private ComboBox? _mappingOutputChoice;
    private StackPanel? _mappingRouteHost;
    private int _mappingWorkspaceVersion;

    private static bool IsMappingPage(BridgeModulePageDefinition page) =>
        page.Sections.SelectMany(section => section.Controls).Any(control =>
            control.Type == BridgeModuleControlType.MappingEditor);

    private async Task RenderMappingWorkspaceAsync()
    {
        if (_dynamicPageRenderer.IsApplyingMapping) return;
        var version = ++_mappingWorkspaceVersion;
        DynamicModulePageContent.Children.Clear();
        var choices = new[]
        {
            new MappingChoice("ds5", "DualSense / PS"),
            new MappingChoice("ns2pro", "Nintendo NS2Pro")
        };
        var header = new Grid { ColumnSpacing = 16, Margin = new Thickness(0, 0, 0, 8) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _mappingInputChoice = new ComboBox
        {
            Header = "实际输入手柄", ItemsSource = choices,
            DisplayMemberPath = nameof(MappingChoice.Label), SelectedValuePath = nameof(MappingChoice.Id),
            SelectedValue = _mappingInput, HorizontalAlignment = HorizontalAlignment.Stretch
        };
        _mappingOutputChoice = new ComboBox
        {
            Header = "USB 输出身份", ItemsSource = choices,
            DisplayMemberPath = nameof(MappingChoice.Label), SelectedValuePath = nameof(MappingChoice.Id),
            SelectedValue = _mappingOutput, HorizontalAlignment = HorizontalAlignment.Stretch
        };
        Grid.SetColumn(_mappingOutputChoice, 1);
        header.Children.Add(_mappingInputChoice);
        header.Children.Add(_mappingOutputChoice);
        DynamicModulePageContent.Children.Add(header);
        _mappingRouteHost = new StackPanel { Spacing = 16 };
        DynamicModulePageContent.Children.Add(_mappingRouteHost);
        _mappingInputChoice.SelectionChanged += async (_, _) => await SelectRouteAsync();
        _mappingOutputChoice.SelectionChanged += async (_, _) => await SelectRouteAsync();
        await SelectRouteAsync();

        async Task SelectRouteAsync()
        {
            if (_inputClosing || version != _mappingWorkspaceVersion ||
                _dynamicPageRenderer.IsApplyingMapping) return;
            _mappingInput = _mappingInputChoice.SelectedValue?.ToString() ?? "ds5";
            _mappingOutput = _mappingOutputChoice.SelectedValue?.ToString() ?? "ds5";
            var page = _module.Pages.FirstOrDefault(candidate =>
                candidate.Sections.SelectMany(section => section.Controls).Any(control =>
                    control.Type == BridgeModuleControlType.MappingEditor &&
                    control.MappingProfile == _mappingInput && control.MappingOutput == _mappingOutput));
            if (page is null)
            {
                _dynamicPageRenderer.SuspendCapture();
                _mappingRouteHost.Children.Clear();
                _mappingRouteHost.Children.Add(new TextBlock
                {
                    Text = "当前固件模块未提供此组合的独立映射。",
                    TextWrapping = TextWrapping.Wrap
                });
                return;
            }
            DynamicModulePage.ChangeView(null, 0, null, disableAnimation: true);
            await _dynamicPageRenderer.RenderAsync(page, _mappingRouteHost);
        }
    }

    private void MappingWriteStateChanged(bool busy)
    {
        if (_inputClosing) return;
        if (_mappingInputChoice is not null) _mappingInputChoice.IsEnabled = !busy;
        if (_mappingOutputChoice is not null) _mappingOutputChoice.IsEnabled = !busy;
    }

    private sealed record MappingChoice(string Id, string Label);
}
