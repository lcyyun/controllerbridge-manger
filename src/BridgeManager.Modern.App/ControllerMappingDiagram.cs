using BridgeManager.Core.FirmwareModules;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace BridgeManager.Modern;

internal sealed class ControllerMappingDiagram : StackPanel
{
    private readonly string? _profile;
    private readonly IReadOnlyDictionary<string, ComboBox> _selectors;
    private readonly IReadOnlyDictionary<string, Button> _captureButtons;
    private readonly Func<string, string> _label;
    private readonly Func<string, string> _sourceLabel;
    private readonly string? _sourceProfile;
    private readonly Action _cancelCapture;
    private readonly Brush _accent;
    private readonly Canvas _stage = new() { Height = 418 };
    private readonly Grid _compactLabels = new() { ColumnSpacing = 12, RowSpacing = 10 };
    private readonly StackPanel _changeList = new() { Spacing = 0 };
    private readonly Expander _changes;
    private readonly SelectorBar _groups;
    private readonly ComboBox _compactGroup;
    private readonly TextBlock _viewCaption;
    private readonly Dictionary<string, Callout> _callouts = new();
    private readonly Dictionary<string, Button> _pins = new();
    private readonly Dictionary<string, Path> _lines = new();
    private readonly MappingGroup[] _definitions;
    private IReadOnlyDictionary<string, string> _device = new Dictionary<string, string>();
    private Flyout? _editor;
    private string? _selectedTarget;
    private bool _loaded;
    private bool _busy;
    private bool _deviceDirty;
    private bool _detached;
    private int _group;
    private double _layoutWidth;
    private bool _layoutPending;

    internal IReadOnlyCollection<string> VisibleTargets => _callouts.Keys;
    internal IReadOnlyCollection<string> AllTargets => _definitions.SelectMany(group => group.Targets).ToArray();
    internal IReadOnlyDictionary<string, Button> CalloutButtons =>
        _callouts.ToDictionary(pair => pair.Key, pair => pair.Value.Button);
    internal FrameworkElement? EditorContent => _editor?.Content as FrameworkElement;
    internal bool IsCompact => _layoutWidth < 760;
    internal int GroupCount => _definitions.Length;
    internal string Summary => _changes.Header?.ToString() ?? "";

    public ControllerMappingDiagram(string? profile,
        IReadOnlyDictionary<string, ComboBox> selectors,
        IReadOnlyDictionary<string, Button> captureButtons,
        Func<string, string> label, Action cancelCapture,
        Func<string, string>? sourceLabel = null, string? sourceProfile = null,
        bool includeCrossIdentityTargets = true)
    {
        _profile = profile;
        // Renderer dictionaries are reused for the next route. Keep the exact
        // controls owned by this diagram so delayed XAML callbacks cannot see
        // a cleared or repopulated dictionary from another page.
        _selectors = new Dictionary<string, ComboBox>(selectors,
            StringComparer.OrdinalIgnoreCase);
        _captureButtons = new Dictionary<string, Button>(captureButtons,
            StringComparer.OrdinalIgnoreCase);
        _label = label;
        _sourceLabel = sourceLabel ?? label;
        _sourceProfile = sourceProfile ?? profile;
        _cancelCapture = cancelCapture;
        _accent = Resource(profile == "ns2pro" ? "NintendoBrush" : "SonyBrush");
        Spacing = 16;
        var ns = profile == "ns2pro";
        _definitions = new[]
        {
            new MappingGroup("正面", new[] { "dpad_up", "dpad_left", "dpad_right", "dpad_down",
                "north", "west", "east", "south" }),
            new MappingGroup("肩键与摇杆", ns
                ? new[] { "left_trigger", "left_shoulder", "left_stick",
                    "right_trigger", "right_shoulder", "right_stick" }
                : new[] { "left_trigger", "left_shoulder", "left_stick", "left_function",
                    "right_trigger", "right_shoulder", "right_stick", "right_function" }),
            new MappingGroup("系统键", ns
                ? new[] { "back", "start", "guide", "capture", "c" }
                : new[] { "back", "start", "guide", "touchpad", "mute" }),
            new MappingGroup(ns ? "背键" : "Edge 背键", new[] { "left_paddle", "right_paddle" }, Rear: true),
            new MappingGroup("跨身份输出", ns
                ? new[] { "touchpad", "mute", "left_function", "right_function" }
                : new[] { "capture", "c" }, Extra: true)
        }.Where(group => includeCrossIdentityTargets || !group.Extra)
            .Select(group => profile == "xbox" ? group with
            {
                Targets = group.Targets.Where(id => BridgeButtonMapping.ControlIds
                    .Take(17).Contains(id)).ToArray()
            } : group)
            .Where(group => group.Targets.Length > 0).ToArray();

        var divider = new Rectangle { Height = 1, Fill = Resource("DividerStrokeColorDefaultBrush") };
        Children.Add(divider);
        _groups = new SelectorBar();
        foreach (var group in _definitions)
            _groups.Items.Add(new SelectorBarItem { Text = group.Title });
        _compactGroup = new ComboBox
        {
            ItemsSource = _definitions.Select(group => group.Title).ToArray(),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Visibility = Visibility.Collapsed
        };
        AutomationProperties.SetName(_compactGroup, "手柄按键区域");
        AutomationProperties.SetName(_groups, "手柄按键区域");
        _groups.SelectionChanged += (_, _) =>
        {
            if (_groups.SelectedItem is null) return;
            CloseEditor();
            _group = _groups.Items.IndexOf(_groups.SelectedItem);
            _compactGroup.SelectedIndex = _group;
            _layoutWidth = 0;
            LayoutDiagram();
        };
        _compactGroup.SelectionChanged += (_, _) =>
        {
            if (_compactGroup.SelectedIndex >= 0)
                SelectGroup(_compactGroup.SelectedIndex);
        };
        Children.Add(_groups);
        Children.Add(_compactGroup);

        var captionRow = new Grid { ColumnSpacing = 16 };
        captionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        captionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _viewCaption = new TextBlock
        {
            FontSize = 12,
            Opacity = 0.66,
            TextWrapping = TextWrapping.Wrap
        };
        captionRow.Children.Add(_viewCaption);
        var relation = new TextBlock
        {
            Text = "目标按键  ←  来源按键",
            FontSize = 12,
            Opacity = 0.66,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(relation, 1);
        captionRow.Children.Add(relation);
        Children.Add(captionRow);
        Children.Add(_stage);
        Children.Add(_compactLabels);
        _changes = new Expander
        {
            Header = "当前映射",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = _changeList
        };
        Children.Add(_changes);
        SizeChanged += (_, args) =>
        {
            if (Math.Abs(args.NewSize.Width - _layoutWidth) < 1 || _layoutPending) return;
            _layoutPending = true;
            DispatcherQueue.TryEnqueue(() =>
            {
                _layoutPending = false;
                if (IsLoaded) LayoutDiagram();
            });
        };
        Unloaded += (_, _) =>
        {
            _detached = true;
            CloseEditor(refresh: false);
        };
        ActualThemeChanged += (_, _) =>
        {
            if (_detached) return;
            CloseEditor();
            _layoutWidth = 0;
            LayoutDiagram();
        };
        SelectGroup(0);
    }

    internal void SelectGroup(int group) => _groups.SelectedItem = _groups.Items[group];

    public void Refresh(bool loaded, bool busy, IReadOnlyDictionary<string, string> device, bool deviceDirty)
    {
        if (_detached) return;
        _loaded = loaded;
        _busy = busy;
        _device = device;
        _deviceDirty = deviceDirty;
        if (!loaded || busy) CloseEditor();
        foreach (var (target, callout) in _callouts)
        {
            var source = Source(target);
            var custom = loaded && target != source;
            var pending = loaded && device.TryGetValue(target, out var saved) && saved != source;
            callout.Value.Text = loaded ? _sourceLabel(source) : "尚未读取";
            callout.Value.Foreground = custom ? _accent : Resource("TextFillColorPrimaryBrush");
            callout.Button.IsEnabled = loaded && !busy;
            callout.Dot.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
            callout.Button.BorderBrush = custom ? _accent : Resource("CardStrokeColorDefaultBrush");
            ToolTipService.SetToolTip(callout.Button, loaded
                ? $"{_label(target)} ← {_sourceLabel(source)}{(pending ? " · 未应用" : "")}"
                : "等待从设备读取映射");
            AutomationProperties.SetName(callout.Button,
                $"{_label(target)} 的来源：{(loaded ? _sourceLabel(source) : "尚未读取")}，更换映射");
            if (_pins.TryGetValue(target, out var pin))
                pin.IsEnabled = loaded && !busy;
        }
        RefreshLineColors();
        RebuildChangeList();
    }

    internal void OpenEditor(string target, FrameworkElement? placement = null)
    {
        if (!_loaded || _busy || XamlRoot is null ||
            !_selectors.TryGetValue(target, out var selector) ||
            !_captureButtons.TryGetValue(target, out var captureButton)) return;
        CloseEditor();
        _selectedTarget = target;
        RefreshLineColors();
        var content = new StackPanel
        {
            Width = Math.Min(284, Math.Max(220, XamlRoot.Size.Width - 64)),
            Spacing = 16,
            RequestedTheme = ActualTheme,
            Background = Resource("CardBackgroundFillColorDefaultBrush")
        };
        var heading = new StackPanel { Spacing = 4 };
        heading.Children.Add(new TextBlock { Text = "目标按键", FontSize = 12, Opacity = 0.65 });
        heading.Children.Add(new TextBlock
        {
            Text = _label(target), FontSize = 20, FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(heading);
        content.Children.Add(selector);
        var shortcuts = new Grid { ColumnSpacing = 6, RowSpacing = 6 };
        for (var column = 0; column < 4; column++)
            shortcuts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        shortcuts.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        shortcuts.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var quickIds = new[] { "south", "east", "west", "north",
            "left_shoulder", "right_shoulder", "left_trigger", "right_trigger" };
        for (var index = 0; index < quickIds.Length; index++)
        {
            var source = quickIds[index];
            var button = new Button
            {
                Content = Token(source), Height = 38,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(4)
            };
            ToolTipService.SetToolTip(button, _sourceLabel(source));
            AutomationProperties.SetName(button, $"来源设为 {_sourceLabel(source)}");
            button.Click += (_, _) =>
            {
                if (_loaded && !_busy) selector.SelectedValue = source;
                CloseEditor();
            };
            Grid.SetRow(button, index / 4);
            Grid.SetColumn(button, index % 4);
            shortcuts.Children.Add(button);
        }
        content.Children.Add(shortcuts);
        var actions = new Grid { ColumnSpacing = 8 };
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var defaults = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6,
                Children = { new SymbolIcon(Symbol.Undo), new TextBlock { Text = "默认" } }
            }
        };
        defaults.Click += (_, _) =>
        {
            if (_loaded && !_busy) selector.SelectedValue = target;
            CloseEditor();
        };
        actions.Children.Add(defaults);
        var disable = new Button { Content = new SymbolIcon(Symbol.BlockContact), Width = 36, Height = 36, Padding = new Thickness(0) };
        ToolTipService.SetToolTip(disable, "不映射此目标键");
        AutomationProperties.SetName(disable, "不映射此目标键");
        disable.Click += (_, _) =>
        {
            if (_loaded && !_busy) selector.SelectedValue = "none";
            CloseEditor();
        };
        Grid.SetColumn(disable, 1);
        actions.Children.Add(disable);
        Grid.SetColumn(captureButton, 2);
        actions.Children.Add(captureButton);
        content.Children.Add(actions);
        var owner = placement ?? (FrameworkElement?)_callouts.GetValueOrDefault(target)?.Button ?? this;
        var flyout = new Flyout
        {
            Content = content,
            XamlRoot = owner.XamlRoot
        };
        flyout.Closed += (_, _) =>
        {
            // Reused controls must be detached before the next editor can open.
            content.Children.Remove(selector);
            actions.Children.Remove(captureButton);
            if (ReferenceEquals(_editor, flyout))
            {
                _editor = null;
                _selectedTarget = null;
                _cancelCapture();
                RefreshLineColors();
            }
        };
        _editor = flyout;
        flyout.ShowAt(owner, new FlyoutShowOptions
        {
            Placement = IsCompact ? FlyoutPlacementMode.Top
                : owner.TransformToVisual(this).TransformPoint(new Point()).X < ActualWidth / 2
                    ? FlyoutPlacementMode.Right : FlyoutPlacementMode.Left
        });
    }

    public void CloseEditor(bool refresh = true)
    {
        var editor = _editor;
        _editor = null;
        _selectedTarget = null;
        if (editor?.Content is StackPanel content)
        {
            foreach (var selector in _selectors.Values)
                content.Children.Remove(selector);
            foreach (var grid in content.Children.OfType<Grid>())
                foreach (var capture in _captureButtons.Values)
                    grid.Children.Remove(capture);
        }
        editor?.Hide();
        _cancelCapture();
        if (refresh && !_detached) RefreshLineColors();
    }

    private void LayoutDiagram()
    {
        if (_detached || ActualWidth <= 0) return;
        var width = ActualWidth;
        var changedSize = Math.Abs(width - _layoutWidth) >= 1;
        if (changedSize) CloseEditor();
        _layoutWidth = width;
        _stage.Children.Clear();
        _compactLabels.Children.Clear();
        _compactLabels.RowDefinitions.Clear();
        _compactLabels.ColumnDefinitions.Clear();
        _callouts.Clear();
        _pins.Clear();
        _lines.Clear();
        var group = _definitions[_group];
        _groups.Visibility = width < 560 ? Visibility.Collapsed : Visibility.Visible;
        _compactGroup.Visibility = width < 560 ? Visibility.Visible : Visibility.Collapsed;
        _viewCaption.Text = group.Extra ? "跨身份扩展"
            : group.Rear ? (_profile == "ns2pro" ? "背面 · GL / GR" : "背面 · DualSense Edge")
            : _group == 1 && _profile == "ds5" ? "正面 · Fn 仅 DualSense Edge" : "正面";
        _compactLabels.Visibility = IsCompact || group.Extra ? Visibility.Visible : Visibility.Collapsed;
        _stage.Visibility = group.Extra ? Visibility.Collapsed : Visibility.Visible;
        if (group.Extra)
        {
            LayoutCompactLabels(group.Targets, width);
            Refresh(_loaded, _busy, _device, _deviceDirty);
            return;
        }
        var scale = IsCompact ? Math.Min(width / ControllerArtwork.Width, 0.9)
            : Math.Min((width - 368) / ControllerArtwork.Width, 1.04);
        var modelWidth = ControllerArtwork.Width * scale;
        var modelHeight = ControllerArtwork.Height * scale;
        _stage.Height = IsCompact ? modelHeight + 24 : Math.Max(392, modelHeight + 48);
        var origin = new Point((width - modelWidth) / 2, (_stage.Height - modelHeight) / 2);
        var artwork = new Viewbox
        {
            Width = modelWidth, Height = modelHeight,
            Child = ControllerArtwork.Create(_profile, group.Rear, edge: _group == 1),
            IsHitTestVisible = false
        };
        Canvas.SetLeft(artwork, origin.X);
        Canvas.SetTop(artwork, origin.Y);
        _stage.Children.Add(artwork);
        var anchors = ControllerArtwork.Anchors(_profile, group.Rear, edge: _group == 1);
        var located = group.Targets.Where(anchors.ContainsKey).ToArray();
        var left = located.Where(target => anchors[target].X < 320).ToList();
        var right = located.Where(target => anchors[target].X >= 320).ToList();
        // Center controls may share X; balance them without changing their physical anchors.
        while (right.Count > Math.Max(4, left.Count + 1))
        {
            var center = right.FirstOrDefault(target => Math.Abs(anchors[target].X - 320) < 10);
            if (center is null) break;
            right.Remove(center);
            left.Add(center);
        }
        AddSide(left, true);
        AddSide(right, false);
        if (IsCompact) LayoutCompactLabels(group.Targets, width);
        Refresh(_loaded, _busy, _device, _deviceDirty);

        void AddSide(List<string> targets, bool onLeft)
        {
            var sorted = targets.OrderBy(target => anchors[target].Y)
                .ThenBy(target => onLeft ? anchors[target].X : -anchors[target].X).ToArray();
            for (var index = 0; index < sorted.Length; index++)
            {
                var target = sorted[index];
                var anchor = anchors[target];
                var point = new Point(origin.X + anchor.X * scale, origin.Y + anchor.Y * scale);
                if (!IsCompact)
                {
                    const double labelWidth = 164;
                    const double labelHeight = 62;
                    var step = 82d;
                    var top = (_stage.Height - sorted.Length * step + (step - labelHeight)) / 2 + index * step;
                    var x = onLeft ? 0 : width - labelWidth;
                    var callout = CreateCallout(target);
                    callout.Button.Width = labelWidth;
                    callout.Button.Height = labelHeight;
                    Canvas.SetLeft(callout.Button, x);
                    Canvas.SetTop(callout.Button, top);
                    var start = new Point(onLeft ? labelWidth : x, top + labelHeight / 2);
                    var direction = onLeft ? 1 : -1;
                    var geometry = new PathGeometry();
                    var figure = new PathFigure { StartPoint = start, IsClosed = false };
                    figure.Segments.Add(new BezierSegment
                    {
                        Point1 = new Point(start.X + direction * 40, start.Y),
                        Point2 = new Point(point.X - direction * 35, point.Y),
                        Point3 = point
                    });
                    geometry.Figures.Add(figure);
                    var line = new Path
                    {
                        Data = geometry, StrokeThickness = 1.25,
                        Stroke = Resource("TextFillColorTertiaryBrush"),
                        Opacity = 0.56, IsHitTestVisible = false
                    };
                    _lines[target] = line;
                    Canvas.SetZIndex(line, 1);
                    Canvas.SetZIndex(callout.Button, 3);
                    _stage.Children.Add(line);
                    _stage.Children.Add(callout.Button);
                }
                var pin = new Button
                {
                    Width = 28, Height = 28, MinWidth = 0, MinHeight = 0,
                    Padding = new Thickness(0), CornerRadius = new CornerRadius(14),
                    BorderThickness = new Thickness(1.5), BorderBrush = _accent,
                    Background = new SolidColorBrush(Colors.Transparent),
                    Content = null
                };
                ToolTipService.SetToolTip(pin, _label(target));
                AutomationProperties.SetName(pin, $"{_label(target)}，更换来源映射");
                Canvas.SetLeft(pin, point.X - 14);
                Canvas.SetTop(pin, point.Y - 14);
                pin.Click += (_, _) => OpenEditor(target, pin);
                pin.PointerEntered += (_, _) => Highlight(target);
                pin.PointerExited += (_, _) => RefreshLineColors();
                _pins[target] = pin;
                Canvas.SetZIndex(pin, 4);
                _stage.Children.Add(pin);
            }
        }
    }

    private void LayoutCompactLabels(IEnumerable<string> targets, double width)
    {
        var columns = width < 350 ? 1 : 2;
        for (var i = 0; i < columns; i++)
            _compactLabels.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var index = 0;
        foreach (var target in targets)
        {
            if (index % columns == 0)
                _compactLabels.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var callout = CreateCallout(target);
            callout.Button.MinHeight = 62;
            Grid.SetRow(callout.Button, index / columns);
            Grid.SetColumn(callout.Button, index % columns);
            _compactLabels.Children.Add(callout.Button);
            index++;
        }
    }

    private Callout CreateCallout(string target)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = ShortLabel(target),
            FontSize = 11, Opacity = 0.68,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        var value = new TextBlock
        {
            Text = "尚未读取", FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        text.Children.Add(value);
        grid.Children.Add(text);
        var trailing = new StackPanel { Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        var dot = new Ellipse { Width = 5, Height = 5, Fill = _accent, Visibility = Visibility.Collapsed };
        trailing.Children.Add(dot);
        trailing.Children.Add(new FontIcon { Glyph = "\uE70D", FontSize = 10, Opacity = 0.55 });
        Grid.SetColumn(trailing, 1);
        grid.Children.Add(trailing);
        var button = new Button
        {
            Content = grid, Padding = new Thickness(12, 8, 10, 8),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            Background = Resource("CardBackgroundFillColorDefaultBrush")
        };
        button.Click += (_, _) => OpenEditor(target, button);
        button.PointerEntered += (_, _) => Highlight(target);
        button.PointerExited += (_, _) => RefreshLineColors();
        var callout = new Callout(button, value, dot);
        _callouts[target] = callout;
        return callout;
    }

    private void RebuildChangeList()
    {
        _changeList.Children.Clear();
        var custom = _selectors.Keys.Where(target => _loaded && Source(target) != target).ToArray();
        var changed = _selectors.Keys.Where(target => _loaded &&
            _device.TryGetValue(target, out var saved) && saved != Source(target)).ToArray();
        var targets = custom.Union(changed).ToArray();
        _changes.Header = _loaded
            ? $"当前映射 · {custom.Length} 项自定义 · {changed.Length} 项未应用{(_deviceDirty ? " · 设备尚未保存" : "")}"
            : "当前映射 · 尚未读取";
        if (targets.Length == 0)
        {
            _changeList.Children.Add(new TextBlock
            {
                Text = _loaded ? "默认映射" : "尚未读取设备配置",
                Margin = new Thickness(0, 8, 0, 8), Opacity = 0.66
            });
            return;
        }
        foreach (var target in targets)
        {
            var pending = changed.Contains(target);
            var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 10, 0, 10) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock
            {
                Text = $"{_label(target)}  ←  {_sourceLabel(Source(target))}",
                TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center
            });
            var status = new TextBlock
            {
                Text = pending ? "未应用" : _deviceDirty ? "未保存" : "已保存", FontSize = 12, Opacity = 0.66,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(status, 1);
            row.Children.Add(status);
            var edit = new Button { Content = new SymbolIcon(Symbol.Edit), Width = 32, Height = 32, Padding = new Thickness(0) };
            ToolTipService.SetToolTip(edit, $"编辑 {_label(target)}");
            edit.IsEnabled = _loaded && !_busy;
            edit.Click += (_, _) => OpenEditor(target, edit);
            Grid.SetColumn(edit, 2);
            row.Children.Add(edit);
            _changeList.Children.Add(row);
        }
    }

    private void Highlight(string target)
    {
        if (_detached) return;
        RefreshLineColors();
        if (_lines.TryGetValue(target, out var line))
        {
            line.Stroke = _accent;
            line.StrokeThickness = 2;
            line.Opacity = 1;
        }
        if (_pins.TryGetValue(target, out var pin))
        {
            pin.BorderThickness = new Thickness(2.5);
            pin.Opacity = 1;
        }
    }

    private void RefreshLineColors()
    {
        if (_detached) return;
        foreach (var (target, line) in _lines)
        {
            var active = _selectedTarget == target || _loaded && Source(target) != target;
            line.Stroke = active ? _accent : Resource("TextFillColorTertiaryBrush");
            line.StrokeThickness = active ? 1.8 : 1.1;
            line.Opacity = active ? 0.9 : 0.5;
        }
        foreach (var (target, pin) in _pins)
        {
            var active = _selectedTarget == target || _loaded && Source(target) != target;
            pin.BorderThickness = new Thickness(active ? 2.5 : 1.2);
            pin.Opacity = active ? 1 : 0.65;
        }
    }

    private string Source(string target) =>
        _selectors.TryGetValue(target, out var selector)
            ? selector.SelectedValue?.ToString() ?? "none"
            : "none";

    private string ShortLabel(string target) => target switch
    {
        "left_function" when _profile != "ns2pro" => "左 Fn · Edge",
        "right_function" when _profile != "ns2pro" => "右 Fn · Edge",
        "left_paddle" when _profile != "ns2pro" => "左背键 · Edge",
        "right_paddle" when _profile != "ns2pro" => "右背键 · Edge",
        _ => _label(target)
    };

    private string Token(string target) => target switch
    {
        "south" => _sourceProfile == "ns2pro" ? "B" : "×",
        "east" => _sourceProfile == "ns2pro" ? "A" : "○",
        "west" => _sourceProfile == "ns2pro" ? "Y" : "□",
        "north" => _sourceProfile == "ns2pro" ? "X" : "△",
        _ => _sourceLabel(target)
    };

    private Brush Resource(string key)
    {
        var dark = ActualTheme == ElementTheme.Dark;
        uint? rgb = key switch
        {
            "TextFillColorPrimaryBrush" => dark ? 0xEEF0F6u : 0x232730u,
            "TextFillColorTertiaryBrush" => dark ? 0x8B93A3u : 0x7A8393u,
            "CardStrokeColorDefaultBrush" => dark ? 0x3B404Au : 0xD8DEE7u,
            "CardBackgroundFillColorDefaultBrush" => dark ? 0x242831u : 0xFFFFFFu,
            _ => null
        };
        return rgb.HasValue
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(255,
                (byte)(rgb.Value >> 16), (byte)(rgb.Value >> 8), (byte)rgb.Value))
            : (Brush)Application.Current.Resources[key];
    }
    private sealed record Callout(Button Button, TextBlock Value, Ellipse Dot);
    private sealed record MappingGroup(string Title, string[] Targets, bool Rear = false, bool Extra = false);
}
