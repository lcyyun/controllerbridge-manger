using System.Globalization;
using System.Text.Json;
using BridgeManager.Core;
using BridgeManager.Core.FirmwareModules;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BridgeManager.Modern;

internal sealed partial class DynamicModulePageRenderer
{
    private static readonly IReadOnlyDictionary<string, string> NoParameters =
        new Dictionary<string, string>();
    private static readonly string[] ControlIds = BridgeButtonMapping.ControlIds.ToArray();

    private static readonly string[] ControlLabels =
    {
        "南侧（A / Cross / B）", "东侧（B / Circle / A）",
        "西侧（X / Square / Y）", "北侧（Y / Triangle / X）",
        "方向键上", "方向键下", "方向键左", "方向键右",
        "左肩键", "右肩键", "左扳机", "右扳机",
        "返回 / Create / Minus", "开始 / Options / Plus",
        "左摇杆按下", "右摇杆按下", "Guide / PS / Home", "触摸板",
        "静音", "截图", "左背键 / GL", "右背键 / GR",
        "左功能键", "右功能键", "C"
    };
    private static readonly string[] Ds5TargetLabels =
    {
        "×  Cross", "○  Circle", "□  Square", "△  Triangle",
        "方向键上", "方向键下", "方向键左", "方向键右",
        "L1", "R1", "L2", "R2", "Create", "Options",
        "L3", "R3", "PS", "触摸板", "静音", "截图（NS 输出）",
        "左背键（Edge）", "右背键（Edge）", "左 Fn（Edge）", "右 Fn（Edge）", "C（NS 输出）"
    };
    private static readonly string[] Ns2ProTargetLabels =
    {
        "B", "A", "Y", "X",
        "方向键上", "方向键下", "方向键左", "方向键右",
        "L", "R", "ZL", "ZR", "Minus", "Plus",
        "左摇杆按下", "右摇杆按下", "Home", "触摸板（PS 输出）", "静音（PS 输出）", "截图",
        "GL", "GR", "左 Fn（PS 输出）", "右 Fn（PS 输出）", "C"
    };
    private static readonly string[] XboxTargetLabels =
    {
        "A", "B", "X", "Y", "方向键上", "方向键下", "方向键左", "方向键右",
        "LB", "RB", "LT", "RT", "Back", "Start", "LS", "RS", "Xbox",
        "触摸板", "静音", "截图", "左背键", "右背键", "左 Fn", "右 Fn", "C"
    };
    private readonly Func<string, IReadOnlyDictionary<string, string>,
        Task<JsonElement>> _executeActionAsync;
    private readonly Action<string, bool> _showStatus;
    private readonly Dictionary<string, RenderedControl> _controls =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ComboBox> _mappingSelectors =
        new(StringComparer.OrdinalIgnoreCase);
    private BridgeModuleControlDefinition? _mappingDefinition;
    private TextBlock? _mappingStatusText;
    private TextBlock? _mappingCountText;
    private InfoBar? _mappingError;
    private ControllerMappingDiagram? _mappingDiagram;
    private AppBarButton? _mappingSaveButton;
    private CommandBar? _mappingToolbar;
    private Action? _refreshMappingCombo;
    private readonly Dictionary<string, Button> _captureButtons = new();
    private Dictionary<string, string> _deviceMapping = new();
    private readonly Dictionary<string, Dictionary<string, string>> _mappingDrafts = new();
    private readonly DispatcherTimer _captureTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    private bool _mappingLoaded;
    private bool _mappingBusy;
    private bool _mappingDeviceDirty;
    private bool _updatingSelectors;
    private int _mappingVersion;
    private BridgePhysicalInput _physicalInput;
    private string _usbReportSource = "";
    private string? _captureTarget;
    private uint _previousButtons;
    public bool IsApplyingMapping { get; private set; }
    public event Action<bool>? MappingWriteStateChanged;

    public DynamicModulePageRenderer(
        Func<string, IReadOnlyDictionary<string, string>, Task<JsonElement>>
            executeActionAsync,
        Action<string, bool> showStatus)
    {
        _executeActionAsync = executeActionAsync;
        _showStatus = showStatus;
        _captureTimer.Tick += (_, _) =>
        {
            CancelCapture();
            UpdateMappingStatus("监听超时");
        };
    }

    public async Task RenderAsync(BridgeModulePageDefinition page,
                                  StackPanel host)
    {
        if (IsApplyingMapping) return;
        RememberDraft();
        _mappingVersion++;
        CancelCapture();
        _mappingDiagram?.CloseEditor();
        host.Children.Clear();
        _controls.Clear();
        _mappingSelectors.Clear();
        _mappingDefinition = null;
        _mappingStatusText = null;
        _mappingCountText = null;
        _mappingError = null;
        _mappingDiagram = null;
        _mappingSaveButton = null;
        _mappingToolbar = null;
        _refreshMappingCombo = null;
        _mappingComboExpander = null;
        _captureButtons.Clear();
        _mappingLoaded = false;
        _mappingBusy = false;
        _mappingDeviceDirty = false;
        _deviceMapping = new();

        foreach (var section in page.Sections)
        {
            var sectionPanel = new StackPanel { Spacing = 12 };
            var mappingOnly = section.Controls.All(control =>
                control.Type == BridgeModuleControlType.MappingEditor);
            if (!mappingOnly) sectionPanel.Children.Add(new TextBlock
            {
                Text = section.Label,
                FontSize = 20,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            });
            if (!mappingOnly && !string.IsNullOrWhiteSpace(section.Description))
            {
                sectionPanel.Children.Add(new TextBlock
                {
                    Text = section.Description,
                    Opacity = 0.68,
                    TextWrapping = TextWrapping.Wrap
                });
            }

            foreach (var definition in section.Controls)
            {
                sectionPanel.Children.Add(CreateControl(definition));
            }

            var regular = section.Controls.Where(control =>
                control.Type is not BridgeModuleControlType.ActionButton and
                    not BridgeModuleControlType.MappingEditor).ToArray();
            if (regular.Any(control => !string.IsNullOrWhiteSpace(
                                            control.ReadAction) ||
                                       !string.IsNullOrWhiteSpace(
                                            control.ApplyAction)))
            {
                var actions = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8
                };
                if (regular.Any(control =>
                        !string.IsNullOrWhiteSpace(control.ReadAction)))
                {
                    var read = new Button { Content = "从设备读取" };
                    read.Click += async (_, _) => await ReadControlsAsync(regular);
                    actions.Children.Add(read);
                }
                if (regular.Any(control =>
                        !string.IsNullOrWhiteSpace(control.ApplyAction)))
                {
                    var apply = new Button { Content = "应用" };
                    apply.Click += async (_, _) => await ApplyControlsAsync(regular);
                    actions.Children.Add(apply);
                }
                sectionPanel.Children.Add(actions);
            }

            host.Children.Add(sectionPanel);
            host.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Height = 1,
                Fill = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
                    "DividerStrokeColorDefaultBrush"]
            });
        }

        var readable = page.Sections.SelectMany(section => section.Controls)
            .Where(control => control.Type != BridgeModuleControlType.MappingEditor &&
                              !string.IsNullOrWhiteSpace(control.ReadAction))
            .ToArray();
        if (readable.Length > 0)
        {
            await ReadControlsAsync(readable, reportErrors: false);
        }
        if (_mappingDefinition is not null)
        {
            await LoadMappingAsync(_mappingDefinition, reportErrors: false);
        }
    }

    public void OnInputSnapshot(ControllerInputSnapshot input,
                                BridgePhysicalInput physicalInput, uint? pressedButtons = null)
    {
        if (_physicalInput != physicalInput || _usbReportSource != input.Source)
        {
            _physicalInput = physicalInput;
            _usbReportSource = input.Source;
            RefreshMappingEnabledState();
        }
        var pressed = input.Buttons;
        if (_captureTarget is not null && CanCapture())
        {
            var newlyPressed = pressedButtons ?? (pressed & ~_previousButtons);
            if (newlyPressed != 0U)
            {
                var index = System.Numerics.BitOperations.TrailingZeroCount(
                    newlyPressed);
                if (index < ControlIds.Length &&
                    _mappingSelectors.TryGetValue(_captureTarget,
                                                  out var selector))
                {
                    selector.SelectedValue = ControlIds[index];
                    _showStatus(
                        $"已将 {MappingTargetLabel(_mappingDefinition, Array.IndexOf(ControlIds, _captureTarget))} 的来源设为 {MappingTargetLabel(_mappingDefinition, index)}。",
                        false);
                    UpdateMappingStatus("已捕获按键 · 未应用");
                }
                CancelCapture();
            }
        }
        _previousButtons = pressed;
    }

    public void SuspendCapture()
    {
        CancelCapture();
        _mappingDiagram?.CloseEditor();
    }

    public void InvalidateConnection()
    {
        _mappingVersion++;
        _mappingLoaded = false;
        _mappingBusy = false;
        _mappingDrafts.Clear();
        _physicalInput = BridgePhysicalInput.Unknown;
        _usbReportSource = "";
        CancelCapture();
        _mappingDiagram?.CloseEditor();
        RefreshMappingEnabledState();
        UpdateMappingStatus("设备已断开 · 尚未读取");
    }

    private FrameworkElement CreateControl(BridgeModuleControlDefinition definition)
    {
        if (definition.Type == BridgeModuleControlType.MappingEditor)
        {
            return CreateMappingEditor(definition);
        }

        var panel = new StackPanel { Spacing = 5 };
        if (definition.Type is not BridgeModuleControlType.Toggle and
            not BridgeModuleControlType.ActionButton)
        {
            panel.Children.Add(new TextBlock
            {
                Text = BuildLabel(definition),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            });
        }

        FrameworkElement element;
        Func<string?> readValue;
        Action<JsonElement> writeValue;
        switch (definition.Type)
        {
        case BridgeModuleControlType.Toggle:
            var toggle = new ToggleSwitch { Header = definition.Label };
            element = toggle;
            readValue = () => toggle.IsOn ? "on" : "off";
            writeValue = value => toggle.IsOn = value.ValueKind ==
                JsonValueKind.True || value.ValueKind == JsonValueKind.String &&
                value.GetString() is "on" or "true" or "1";
            break;
        case BridgeModuleControlType.Number:
            var number = new NumberBox
            {
                Minimum = (double)(definition.Min ?? decimal.MinValue),
                Maximum = (double)(definition.Max ?? decimal.MaxValue),
                SmallChange = (double)(definition.Step ?? 1M),
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                Value = DefaultNumber(definition)
            };
            element = number;
            readValue = () => number.Value.ToString(CultureInfo.InvariantCulture);
            writeValue = value => number.Value = value.TryGetDouble(out var parsed)
                ? parsed : number.Value;
            break;
        case BridgeModuleControlType.Slider:
            var slider = new Slider
            {
                Minimum = (double)(definition.Min ?? 0M),
                Maximum = (double)(definition.Max ?? 100M),
                StepFrequency = (double)(definition.Step ?? 1M),
                Value = DefaultNumber(definition)
            };
            element = slider;
            readValue = () => slider.Value.ToString(CultureInfo.InvariantCulture);
            writeValue = value => slider.Value = value.TryGetDouble(out var parsed)
                ? parsed : slider.Value;
            break;
        case BridgeModuleControlType.Select:
            var select = new ComboBox
            {
                ItemsSource = definition.Options,
                DisplayMemberPath = nameof(BridgeModuleControlOptionDefinition.Label),
                SelectedValuePath = nameof(BridgeModuleControlOptionDefinition.Value),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            select.SelectedValue = definition.Default is { ValueKind:
                JsonValueKind.String } optionDefault
                ? optionDefault.GetString() : definition.Options.FirstOrDefault()?.Value;
            element = select;
            readValue = () => select.SelectedValue?.ToString();
            writeValue = value => select.SelectedValue = JsonScalar(value);
            break;
        case BridgeModuleControlType.Text:
        case BridgeModuleControlType.Color:
            var text = new TextBox
            {
                Text = definition.Default is { ValueKind: JsonValueKind.String } textDefault
                    ? textDefault.GetString() ?? "" : "",
                PlaceholderText = definition.Type == BridgeModuleControlType.Color
                    ? "#RRGGBB" : null
            };
            element = text;
            readValue = () => text.Text;
            writeValue = value => text.Text = JsonScalar(value);
            break;
        case BridgeModuleControlType.ActionButton:
            var button = new Button { Content = definition.Label };
            button.Click += async (_, _) =>
                await ExecuteActionAsync(definition.ApplyAction, NoParameters);
            element = button;
            readValue = () => null;
            writeValue = _ => { };
            break;
        case BridgeModuleControlType.Status:
        case BridgeModuleControlType.Table:
        case BridgeModuleControlType.Group:
        case BridgeModuleControlType.Tabs:
            var status = new TextBlock
            {
                Text = definition.Description ?? "等待设备数据",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.74
            };
            element = status;
            readValue = () => status.Text;
            writeValue = value => status.Text = value.ValueKind is
                JsonValueKind.Object or JsonValueKind.Array
                ? JsonSerializer.Serialize(value,
                    new JsonSerializerOptions { WriteIndented = true })
                : JsonScalar(value);
            break;
        default:
            var unsupported = new InfoBar
            {
                IsOpen = true,
                Severity = InfoBarSeverity.Warning,
                Title = definition.Label,
                Message = $"当前管理器不支持控件类型 {definition.Type}。"
            };
            element = unsupported;
            readValue = () => null;
            writeValue = _ => { };
            break;
        }

        panel.Children.Add(element);
        if (!string.IsNullOrWhiteSpace(definition.Description) &&
            definition.Type is not BridgeModuleControlType.Status)
        {
            panel.Children.Add(new TextBlock
            {
                Text = definition.Description,
                Opacity = 0.64,
                TextWrapping = TextWrapping.Wrap
            });
        }
        _controls[definition.Id] = new(definition, readValue, writeValue);
        return panel;
    }

    private FrameworkElement CreateMappingEditor(
        BridgeModuleControlDefinition definition)
    {
        _mappingDefinition = definition;
        var panel = new StackPanel { Spacing = 18 };
        var profile = definition.MappingProfile switch
        {
            "ns2pro" => "Nintendo NS2Pro",
            "ds5" => "PS / DualSense",
            _ => definition.Label
        };
        var profileGrid = new Grid { ColumnSpacing = 14, Margin = new Thickness(0, 4, 0, 0) };
        profileGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        profileGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        profileGrid.Children.Add(new Border
        {
            Width = 4,
            Height = 48,
            CornerRadius = new CornerRadius(2),
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
                definition.MappingProfile == "ns2pro" ? "NintendoBrush" : "SonyBrush"]
        });
        var profileText = new StackPanel { Spacing = 3 };
        profileText.Children.Add(new TextBlock
        {
            Text = definition.MappingProfile == "ns2pro" ? "NINTENDO" : "PLAYSTATION",
            FontSize = 11,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
                definition.MappingProfile == "ns2pro" ? "NintendoBrush" : "SonyBrush"]
        });
        profileText.Children.Add(new TextBlock
        {
            Text = definition.MappingOutput is not null
                ? $"{ProfileLabel(definition.MappingProfile)} → {ProfileLabel(definition.MappingOutput)}"
                : ProfileLabel(definition.MappingProfile),
            FontSize = 22,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        _mappingStatusText = new TextBlock
        {
            Text = "等待从设备读取",
            Opacity = 0.72,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12
        };
        profileText.Children.Add(_mappingStatusText);
        Grid.SetColumn(profileText, 1);
        profileGrid.Children.Add(profileText);
        panel.Children.Add(profileGrid);
        _mappingError = new InfoBar
        {
            IsOpen = false,
            IsClosable = false,
            Severity = InfoBarSeverity.Warning
        };
        panel.Children.Add(_mappingError);
        _mappingCountText = new TextBlock { Text = "尚未读取", VerticalAlignment = VerticalAlignment.Center };
        _mappingToolbar = new CommandBar
        {
            DefaultLabelPosition = CommandBarDefaultLabelPosition.Right,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Center,
            Height = 52,
            Content = _mappingCountText
        };
        var reload = MappingActionButton("重新读取", Symbol.Refresh, async () =>
        {
            _mappingDrafts.Remove(definition.Id);
            await LoadMappingAsync(definition);
        });
        _mappingSaveButton = MappingActionButton("应用并保存", Symbol.Save,
            () => ApplyMappingAsync(definition));
        _mappingSaveButton.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
            definition.MappingProfile == "ns2pro" ? "NintendoBrush" : "SonyBrush"];
        _mappingToolbar.PrimaryCommands.Add(reload);
        _mappingToolbar.PrimaryCommands.Add(_mappingSaveButton);
        var reset = MappingActionButton("恢复默认", Symbol.Undo, () =>
        {
            SetDraft(ControlIds.ToDictionary(id => id, id => id));
            return Task.CompletedTask;
        });
        var ns = (definition.MappingOutput ?? definition.MappingProfile) == "ns2pro";
        var xbox = definition.MappingOutput == "xbox";
        var swapAb = MappingActionButton(ns || xbox ? "交换 A / B" : "交换 × / ○", Symbol.Sync, () =>
        {
            SwapMappingValues("south", "east");
            return Task.CompletedTask;
        });
        var swapXy = MappingActionButton(ns || xbox ? "交换 X / Y" : "交换 □ / △", Symbol.Sync, () =>
        {
            SwapMappingValues("west", "north");
            return Task.CompletedTask;
        });
        _mappingToolbar.SecondaryCommands.Add(swapAb);
        _mappingToolbar.SecondaryCommands.Add(swapXy);
        _mappingToolbar.SecondaryCommands.Add(reset);
        panel.Children.Add(_mappingToolbar);

        var options = new[]
        {
            new MappingSourceOption("none", "不映射", true)
        }.Concat(ControlIds.Select((id, optionIndex) =>
            new MappingSourceOption(id, MappingSourceLabel(definition, optionIndex) +
                (definition.MappingOutput is not null && !IsPhysicalControl(definition.MappingProfile, id)
                    ? "（此输入手柄无此键）" : ""),
                definition.MappingOutput is null || IsPhysicalControl(definition.MappingProfile, id)))).ToArray();
        foreach (var targetId in ControlIds)
        {
            var labelIndex = Array.IndexOf(ControlIds, targetId);
            var capture = new Button
            {
                Content = new SymbolIcon(Symbol.Play),
                Width = 36,
                Height = 36,
                Padding = new Thickness(0),
                Tag = targetId
            };
            ToolTipService.SetToolTip(capture, "监听并按下来源按键");
            capture.Click += (_, _) =>
            {
                if (!CanCapture()) return;
                var cancel = _captureTarget == targetId;
                CancelCapture();
                if (cancel) return;
                _captureTarget = targetId;
                capture.Content = new SymbolIcon(Symbol.Stop);
                _captureTimer.Start();
                _showStatus($"正在监听 {MappingTargetLabel(definition, labelIndex)} 的来源按键。", false);
                UpdateMappingStatus("正在监听来源按键...");
            };
            _captureButtons[targetId] = capture;
            var selector = new ComboBox
            {
                Header = "来源按键",
                ItemsSource = options,
                DisplayMemberPath = nameof(MappingSourceOption.Label),
                SelectedValuePath = nameof(MappingSourceOption.Value),
                SelectedValue = targetId,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                MinWidth = 0
            };
            selector.ItemContainerStyle = new Style(typeof(ComboBoxItem))
            {
                Setters = { new Setter(Control.IsEnabledProperty,
                    new Microsoft.UI.Xaml.Data.Binding
                    { Path = new PropertyPath(nameof(MappingSourceOption.Available)) }) }
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
                selector, $"{profile} {MappingTargetLabel(definition, labelIndex)} 来源");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
                capture, $"{MappingTargetLabel(definition, labelIndex)} 监听来源");
            selector.SelectionChanged += (_, _) =>
            {
                if (!_updatingSelectors) UpdateDraftStatus();
            };
            _mappingSelectors[targetId] = selector;
        }
        _mappingDiagram = new ControllerMappingDiagram(definition.MappingOutput ?? definition.MappingProfile,
            _mappingSelectors, _captureButtons,
            id => id == "none" ? "不映射"
                : MappingTargetLabel(definition, Array.IndexOf(ControlIds, id)),
            CancelCapture,
            id => id == "none" ? "不映射"
                : MappingSourceLabel(definition, Array.IndexOf(ControlIds, id)),
            definition.MappingProfile,
            includeCrossIdentityTargets: definition.MappingOutput is null);
        panel.Children.Add(_mappingDiagram);
        panel.Children.Add(CreateMappingComboEditor(definition));
        RefreshMappingEnabledState();
        return panel;
    }

    private async Task ReadControlsAsync(
        IReadOnlyCollection<BridgeModuleControlDefinition> definitions,
        bool reportErrors = true)
    {
        try
        {
            foreach (var group in definitions
                         .Where(control => !string.IsNullOrWhiteSpace(
                             control.ReadAction))
                         .GroupBy(control => control.ReadAction!,
                                  StringComparer.OrdinalIgnoreCase))
            {
                var response = await _executeActionAsync(group.Key,
                    new Dictionary<string, string>());
                foreach (var definition in group)
                {
                    if (_controls.TryGetValue(definition.Id, out var rendered) &&
                        TrySelect(response, definition.Binding, out var value))
                    {
                        rendered.WriteValue(value);
                    }
                }
            }
            if (reportErrors) _showStatus("模块设置已从设备读取。", false);
        }
        catch (Exception ex)
        {
            if (reportErrors) _showStatus($"读取模块设置失败：{ex.Message}", true);
        }
    }

    private async Task ApplyControlsAsync(
        IEnumerable<BridgeModuleControlDefinition> definitions)
    {
        try
        {
            foreach (var definition in definitions.Where(control =>
                         !string.IsNullOrWhiteSpace(control.ApplyAction)))
            {
                if (!_controls.TryGetValue(definition.Id, out var rendered))
                {
                    continue;
                }
                var value = rendered.ReadValue() ?? "";
                await _executeActionAsync(definition.ApplyAction!,
                    new Dictionary<string, string>
                    {
                        ["id"] = definition.Id,
                        ["value"] = value,
                        ["rawValue"] = value is "on" or "off"
                            ? (value == "on").ToString().ToLowerInvariant() : value
                    });
            }
            _showStatus("模块设置已应用。", false);
        }
        catch (Exception ex)
        {
            _showStatus($"应用模块设置失败：{ex.Message}", true);
        }
    }

    private async Task LoadMappingAsync(BridgeModuleControlDefinition definition,
                                        bool reportErrors = true)
    {
        if (_mappingBusy) return;
        var version = _mappingVersion;
        _mappingBusy = true;
        _mappingLoaded = false;
        CancelCapture();
        RefreshMappingEnabledState();
        UpdateMappingStatus("正在读取设备配置");
        if (_mappingError is not null) _mappingError.IsOpen = false;
        try
        {
            var response = await _executeActionAsync(definition.ReadAction!,
                BridgeButtonMapping.Parameters(definition));
            if (version != _mappingVersion) return;
            _deviceMapping = BridgeButtonMapping.ReadReply(definition, response);
            _mappingLoaded = true;
            _mappingDeviceDirty = response.ValueKind == JsonValueKind.Object &&
                response.TryGetProperty("dirty", out var dirtyValue) &&
                dirtyValue.ValueKind == JsonValueKind.True;
            SetDraft(_mappingDrafts.TryGetValue(definition.Id, out var draft)
                ? draft : _deviceMapping);
            UpdateMappingStatus(HasDraftChanges() ? "本地草稿 · 未应用"
                : _mappingDeviceDirty ? "已读取 · 设备有未保存修改" : "已与设备同步");
            if (reportErrors) _showStatus("按键映射已读取。", false);
        }
        catch (Exception ex)
        {
            if (version != _mappingVersion) return;
            var legacy = definition.MappingProfile is not null &&
                (ex.Message.Contains("usage: mapping", StringComparison.OrdinalIgnoreCase) ||
                 ex.Message.Contains("独立映射", StringComparison.Ordinal));
            var message = legacy
                ? "板上固件尚未确认“输入手柄 × USB 身份”独立配置。请先更新配套固件；旧版两份映射不会当作四份配置写入。"
                : ex.Message;
            UpdateMappingStatus(legacy ? "固件需要更新" : "映射尚未读取");
            if (_mappingError is not null)
            {
                _mappingError.Title = legacy ? "独立映射需要配套固件" : "无法读取映射";
                _mappingError.Message = message;
                _mappingError.IsOpen = true;
            }
            if (reportErrors) _showStatus($"读取按键映射失败：{message}", true);
        }
        finally
        {
            if (version == _mappingVersion)
            {
                _mappingBusy = false;
                RefreshMappingEnabledState();
            }
        }
    }

    private async Task ApplyMappingAsync(BridgeModuleControlDefinition definition)
    {
        if (_mappingBusy || !_mappingLoaded) return;
        var version = _mappingVersion;
        var desired = _mappingSelectors.ToDictionary(
            pair => pair.Key, pair => pair.Value.SelectedValue?.ToString() ?? "none");
        var changes = desired.Where(pair => _deviceMapping[pair.Key] != pair.Value).ToArray();
        _mappingBusy = true;
        IsApplyingMapping = true;
        MappingWriteStateChanged?.Invoke(true);
        CancelCapture();
        RefreshMappingEnabledState();
        UpdateMappingStatus("正在应用配置");
        try
        {
            foreach (var (target, source) in changes)
            {
                var response = await _executeActionAsync(definition.ApplyAction!,
                    BridgeButtonMapping.Parameters(definition, target, source));
                if (version != _mappingVersion) return;
                var confirmed = BridgeButtonMapping.ReadReply(definition, response);
                if (confirmed[target] != source)
                    throw new InvalidDataException("设备未确认按键修改。");
            }
            var saved = await _executeActionAsync(definition.SaveAction!,
                BridgeButtonMapping.Parameters(definition));
            if (version != _mappingVersion) return;
            var actual = BridgeButtonMapping.ReadReply(definition, saved);
            if (desired.Any(pair => actual[pair.Key] != pair.Value) ||
                (saved.TryGetProperty("dirty", out var dirty) &&
                 dirty.ValueKind == JsonValueKind.True))
                throw new InvalidDataException("设备保存结果与本地配置不一致。");
            _deviceMapping = actual;
            _mappingDeviceDirty = false;
            _mappingDrafts.Remove(definition.Id);
            UpdateDraftStatus();
            UpdateMappingStatus("已应用并保存到设备");
            _showStatus("按键映射已应用并保存到设备。", false);
        }
        catch (Exception ex)
        {
            if (version != _mappingVersion) return;
            _mappingLoaded = false;
            _mappingDrafts[definition.Id] = desired;
            UpdateMappingStatus("写入未完成 · 部分按键可能已生效，请重新读取");
            _showStatus($"应用按键映射失败：{ex.Message}", true);
        }
        finally
        {
            IsApplyingMapping = false;
            MappingWriteStateChanged?.Invoke(false);
            if (version == _mappingVersion)
            {
                _mappingBusy = false;
                RefreshMappingEnabledState();
            }
        }
    }

    private async Task ExecuteActionAsync(
        string? action,
        IReadOnlyDictionary<string, string> parameters)
    {
        if (string.IsNullOrWhiteSpace(action)) return;
        await _executeActionAsync(action, parameters);
    }

    private AppBarButton MappingActionButton(string label, Symbol symbol,
                                             Func<Task> action)
    {
        var button = new AppBarButton
        {
            Label = label,
            Icon = new SymbolIcon(symbol)
        };
        ToolTipService.SetToolTip(button, label);
        button.Click += async (_, _) =>
        {
            try { await action(); }
            catch (Exception ex) { _showStatus($"映射操作失败：{ex.Message}", true); }
        };
        return button;
    }

    private void SetDraft(IReadOnlyDictionary<string, string> entries)
    {
        _updatingSelectors = true;
        foreach (var (target, source) in entries)
            _mappingSelectors[target].SelectedValue = source;
        _updatingSelectors = false;
        UpdateDraftStatus();
    }

    private void SwapMappingValues(string firstTarget, string secondTarget)
    {
        if (!_mappingSelectors.TryGetValue(firstTarget, out var first) ||
            !_mappingSelectors.TryGetValue(secondTarget, out var second))
        {
            return;
        }
        var value = first.SelectedValue;
        first.SelectedValue = second.SelectedValue;
        second.SelectedValue = value;
        UpdateDraftStatus();
    }

    private bool HasDraftChanges() => _mappingLoaded && _mappingSelectors.Any(
        pair => pair.Value.SelectedValue?.ToString() != _deviceMapping[pair.Key]);

    private void RememberDraft()
    {
        if (_mappingDefinition is not null && HasDraftChanges())
            _mappingDrafts[_mappingDefinition.Id] = _mappingSelectors.ToDictionary(
                pair => pair.Key, pair => pair.Value.SelectedValue?.ToString() ?? "none");
    }

    private void UpdateDraftStatus()
    {
        _refreshMappingCombo?.Invoke();
        _mappingDiagram?.Refresh(_mappingLoaded, _mappingBusy, _deviceMapping, _mappingDeviceDirty);
        if (!_mappingLoaded) return;
        var custom = _mappingSelectors.Count(pair =>
            pair.Value.SelectedValue?.ToString() != pair.Key);
        var pending = _mappingSelectors.Count(pair =>
            pair.Value.SelectedValue?.ToString() != _deviceMapping[pair.Key]);
        if (_mappingCountText is not null)
            _mappingCountText.Text = $"{custom} 个自定义 · {pending} 个未应用";
        UpdateMappingStatus(pending > 0 ? "本地草稿 · 未应用"
            : _mappingDeviceDirty ? "设备有未保存修改" : "已与设备同步");
        if (pending == 0 && _mappingDefinition is not null)
            _mappingDrafts.Remove(_mappingDefinition.Id);
    }

    private bool CanCapture() => _mappingLoaded && !_mappingBusy &&
        BridgeButtonMapping.CanCapture(_mappingDefinition?.MappingProfile,
            _mappingDefinition?.MappingOutput ?? _mappingDefinition?.MappingProfile,
            _physicalInput, _usbReportSource, _deviceMapping);

    private void CancelCapture()
    {
        _captureTimer.Stop();
        if (_captureTarget is not null &&
            _captureButtons.TryGetValue(_captureTarget, out var button))
            button.Content = new SymbolIcon(Symbol.Play);
        _captureTarget = null;
    }

    private void RefreshMappingEnabledState()
    {
        _refreshMappingCombo?.Invoke();
        _mappingDiagram?.Refresh(_mappingLoaded, _mappingBusy, _deviceMapping, _mappingDeviceDirty);
        if (!_mappingLoaded && _mappingCountText is not null)
            _mappingCountText.Text = "尚未读取";
        foreach (var selector in _mappingSelectors.Values)
            selector.IsEnabled = _mappingLoaded && !_mappingBusy;
        if (_mappingToolbar is not null)
        {
            _mappingToolbar.IsEnabled = !_mappingBusy;
            foreach (var command in _mappingToolbar.SecondaryCommands.OfType<AppBarButton>())
                command.IsEnabled = _mappingLoaded && !_mappingBusy;
        }
        if (_mappingSaveButton is not null)
            _mappingSaveButton.IsEnabled = _mappingLoaded && !_mappingBusy;
        var canCapture = CanCapture();
        if (!canCapture) CancelCapture();
        foreach (var button in _captureButtons.Values)
        {
            button.IsEnabled = canCapture;
            ToolTipService.SetToolTip(button, canCapture ? "监听 USB 来源按键"
                : "仅原生 USB 身份、对应输入手柄和设备默认映射支持监听");
        }
    }

    private void UpdateMappingStatus(string text)
    {
        if (_mappingStatusText is not null)
        {
            _mappingStatusText.Text = text;
        }
    }

    private static string MappingTargetLabel(
        BridgeModuleControlDefinition? definition, int index)
    {
        if (index < 0 || index >= ControlIds.Length)
        {
            return "按键";
        }
        var labels = (definition?.MappingOutput ?? definition?.MappingProfile) switch
        {
            "ns2pro" => Ns2ProTargetLabels,
            "ds5" => Ds5TargetLabels,
            "xbox" => XboxTargetLabels,
            _ => ControlLabels
        };
        return labels[index];
    }

    private static string MappingSourceLabel(BridgeModuleControlDefinition? definition, int index) =>
        index < 0 || index >= ControlIds.Length ? "按键"
            : definition?.MappingProfile == "ns2pro" ? Ns2ProTargetLabels[index]
            : definition?.MappingProfile == "ds5" ? Ds5TargetLabels[index] : ControlLabels[index];

    private static string ProfileLabel(string? profile) =>
        profile == "ns2pro" ? "Nintendo NS2Pro" : profile == "ds5" ? "DualSense" :
        profile == "xbox" ? "Xbox 360" : "通用手柄";

    private static bool IsPhysicalControl(string? profile, string id) =>
        profile == "ds5" ? id is not ("capture" or "c")
            : profile != "ns2pro" || id is not ("touchpad" or "mute" or "left_function" or "right_function");

    private sealed record MappingSourceOption(string Value, string Label, bool Available);

    private static bool TrySelect(JsonElement root, string? pointer,
                                  out JsonElement value)
    {
        value = root;
        if (string.IsNullOrEmpty(pointer)) return true;
        foreach (var escaped in pointer.Split('/', StringSplitOptions.None).Skip(1))
        {
            var token = escaped.Replace("~1", "/").Replace("~0", "~");
            if (value.ValueKind == JsonValueKind.Object &&
                value.TryGetProperty(token, out var property))
            {
                value = property;
                continue;
            }
            if (value.ValueKind == JsonValueKind.Array &&
                int.TryParse(token, out var index) && index >= 0 &&
                index < value.GetArrayLength())
            {
                value = value[index];
                continue;
            }
            value = default;
            return false;
        }
        return true;
    }

    private static string JsonScalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.Null => "",
        _ => value.GetRawText()
    };

    private static string BuildLabel(BridgeModuleControlDefinition definition) =>
        string.IsNullOrWhiteSpace(definition.Unit)
            ? definition.Label : $"{definition.Label}（{definition.Unit}）";

    private static double DefaultNumber(BridgeModuleControlDefinition definition)
    {
        if (definition.Default is { ValueKind: JsonValueKind.Number } value &&
            value.TryGetDouble(out var parsed))
        {
            return parsed;
        }
        return (double)(definition.Min ?? 0M);
    }

    private sealed record RenderedControl(
        BridgeModuleControlDefinition Definition,
        Func<string?> ReadValue,
        Action<JsonElement> WriteValue);
}
