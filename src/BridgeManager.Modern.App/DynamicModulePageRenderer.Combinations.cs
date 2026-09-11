using BridgeManager.Core.FirmwareModules;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BridgeManager.Modern;

internal sealed partial class DynamicModulePageRenderer
{
    private Expander? _mappingComboExpander;
    private FrameworkElement CreateMappingComboEditor(BridgeModuleControlDefinition definition)
    {
        var version = _mappingVersion;
        var updating = false;
        var selectors = new Dictionary<string, ComboBox>(_mappingSelectors);
        var body = new StackPanel { Spacing = 12 };
        var source = new ComboBox
        {
            Header = "来源按键",
            ItemsSource = ControlIds.Where(id => IsPhysicalControl(definition.MappingProfile, id))
                .Select(id => new MappingSourceOption(id,
                    MappingSourceLabel(definition, Array.IndexOf(ControlIds, id)), true)).ToArray(),
            DisplayMemberPath = nameof(MappingSourceOption.Label),
            SelectedValuePath = nameof(MappingSourceOption.Value),
            SelectedValue = "south",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        body.Children.Add(source);
        var targets = new Grid { ColumnSpacing = 16, RowSpacing = 8 };
        targets.ColumnDefinitions.Add(new ColumnDefinition());
        targets.ColumnDefinitions.Add(new ColumnDefinition());
        var checks = new Dictionary<string, CheckBox>();
        var output = definition.MappingOutput ?? definition.MappingProfile;
        foreach (var id in ControlIds.Where(id => output == "xbox"
                     ? Array.IndexOf(ControlIds, id) < 17 : IsPhysicalControl(output, id)))
        {
            var index = checks.Count;
            if (index % 2 == 0) targets.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var check = new CheckBox
            {
                Content = new TextBlock
                {
                    Text = MappingTargetLabel(definition, Array.IndexOf(ControlIds, id)),
                    TextWrapping = TextWrapping.Wrap
                },
                HorizontalAlignment = HorizontalAlignment.Stretch,
                MinWidth = 0
            };
            Grid.SetRow(check, index / 2);
            Grid.SetColumn(check, index % 2);
            targets.Children.Add(check);
            checks[id] = check;
            check.Checked += Changed;
            check.Unchecked += Changed;

            void Changed(object sender, RoutedEventArgs args)
            {
                if (updating || version != _mappingVersion || !_mappingLoaded || _mappingBusy ||
                    source.SelectedValue is not string selected) return;
                selectors[id].SelectedValue = check.IsChecked == true ? selected : "none";
            }
        }
        body.Children.Add(targets);
        var summary = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.72 };
        body.Children.Add(summary);
        var expander = new Expander
        {
            Header = "组合输出",
            Content = body,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        source.SelectionChanged += (_, _) => Refresh();
        _refreshMappingCombo = Refresh;
        _mappingComboExpander = expander;
        Refresh();
        return expander;

        void Refresh()
        {
            if (version != _mappingVersion || updating) return;
            updating = true;
            try
            {
                var selected = source.SelectedValue?.ToString() ?? "south";
                source.IsEnabled = _mappingLoaded && !_mappingBusy;
                foreach (var (id, check) in checks)
                {
                    check.IsChecked = selectors[id].SelectedValue?.ToString() == selected;
                    check.IsEnabled = _mappingLoaded && !_mappingBusy;
                }
                var enabled = checks.Where(pair => pair.Value.IsChecked == true)
                    .Select(pair => MappingTargetLabel(definition, Array.IndexOf(ControlIds, pair.Key)));
                summary.Text = string.Join(" + ", enabled) is { Length: > 0 } text ? text : "无输出";
            }
            finally { updating = false; }
        }
    }
}
