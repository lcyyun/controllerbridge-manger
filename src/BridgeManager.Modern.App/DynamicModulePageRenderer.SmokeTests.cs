using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using BridgeManager.Core.FirmwareModules;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace BridgeManager.Modern;

internal sealed partial class DynamicModulePageRenderer
{
    // Explicit opt-in UI tests; never constructs a transport or discovers hardware.
    internal static async Task RunSmokeTestsAsync(Window window, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var module = BridgeModulePackageLoader.LoadFile(Path.Combine(
            AppContext.BaseDirectory, "modules", "sf32-unified", "module.json"));
        var psPage = module.Pages.Single(page => page.Id == "ps-mapping");
        var nsPage = module.Pages.Single(page => page.Id == "ns-mapping");
        var ps = psPage.Sections.Single().Controls.Single();
        var ns = nsPage.Sections.Single().Controls.Single();
        var configurations = new[] { "ds5/ds5", "ds5/ns2pro", "ns2pro/ds5", "ns2pro/ns2pro", "ds5/xbox", "ns2pro/xbox" }
            .ToDictionary(route => route, _ => ControlIds.ToDictionary(id => id, id => id));
        var commands = new List<string>();
        var statuses = new List<string>();
        string? failingAction = null;
        string failureDetail = "simulated device failure";
        bool deviceDirty = false;
        TaskCompletionSource<JsonElement>? pendingRead = null;
        JsonElement Reply(string profile, string output) => JsonSerializer.SerializeToElement(new
        {
            ok = true, profile, output, mapping_schema = 4, dirty = deviceDirty,
            entries = configurations[$"{profile}/{output}"]
        });
        Task<JsonElement> Execute(string action, IReadOnlyDictionary<string, string> parameters)
        {
            var command = BridgeModuleOperationEngine.BuildRequest(module.Operations[action], parameters);
            commands.Add(command);
            if (action == failingAction) throw new IOException(failureDetail);
            var profile = parameters["profile"];
            var output = parameters["output"];
            if (action == "mapping.read" && pendingRead is not null && profile == "ds5")
                return pendingRead.Task;
            if (action == "mapping.set")
                configurations[$"{profile}/{output}"][parameters["target"]] = parameters["source"];
            return Task.FromResult(Reply(profile, output));
        }
        void Require(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException(description);
        }
        var renderer = new DynamicModulePageRenderer(Execute,
            (text, error) => statuses.Add($"{error}: {text}"));
        var host = new StackPanel { Spacing = 14 };
        var frame = new Grid
        {
            Padding = new Thickness(24),
            RowSpacing = 16,
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
                "ApplicationPageBackgroundThemeBrush"]
        };
        frame.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        frame.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        frame.Children.Add(new TextBlock
        {
            Text = "按键映射 · 界面测试（模拟传输）",
            FontSize = 24,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        var scroll = new ScrollViewer
        {
            Content = host,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Grid.SetRow(scroll, 1);
        frame.Children.Add(scroll);
        window.Content = frame;
        window.AppWindow.Resize(new SizeInt32(1120, 1000));
        await Task.Delay(250);
        await renderer.RenderAsync(psPage, host);
        Require(renderer._mappingLoaded, "PS mapping failed to load");
        Require(commands.Last() == "mapping get ds5 ds5", "Read omitted input/output pair");
        Require(renderer._mappingSelectors.Count == 25, "Missing mapping controls");
        Require(renderer._mappingDiagram!.AllTargets.Count == 23 &&
                renderer._mappingDiagram.AllTargets.Distinct().Count() == 23,
            "DS5 diagram does not expose its native targets exactly once");
        await CaptureAsync(frame, "ps-default.png");
        new ButtonAutomationPeer(renderer._mappingDiagram.CalloutButtons["south"]).Invoke();
        await Task.Delay(150);
        Require(renderer._mappingDiagram.EditorContent is not null &&
                renderer._mappingSelectors["south"].ActualWidth > 150,
            "Callout did not open a usable mapping picker");
        await CaptureAsync(renderer._mappingDiagram.EditorContent!, "ps-picker.png");
        var beforeDraft = commands.Count;
        var picker = (StackPanel)renderer._mappingDiagram.EditorContent!;
        var quickButtons = picker.Children.OfType<Grid>().First().Children.OfType<Button>().ToArray();
        new ButtonAutomationPeer(quickButtons[1]).Invoke();
        await Task.Delay(80);
        Require(renderer._mappingSelectors["south"].SelectedValue?.ToString() == "east" &&
                renderer._mappingDiagram.EditorContent is null,
            "Quick source button did not change the draft and close its picker");
        renderer._mappingSelectors["east"].SelectedValue = "south";
        Require(commands.Count == beforeDraft, "Local picker changes wrote to hardware");
        renderer._mappingDiagram.CloseEditor();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            renderer._mappingDiagram.OpenEditor("south");
            await Task.Delay(80);
            Require(renderer._mappingDiagram.EditorContent is not null,
                "Repeated picker opening lost its shared selector");
            renderer._mappingDiagram.CloseEditor();
            await Task.Delay(80);
        }
        await CaptureAsync(frame, "ps-wide.png");
        await CheckDiagramGroupsAsync("ps");
        Require(renderer._mappingSaveButton?.IsEnabled == true,
            "Loaded PS page has a disabled save button");
        Require(renderer._mappingToolbar!.SecondaryCommands.OfType<AppBarButton>().All(button => button.IsEnabled),
            "Loaded PS page has disabled quick actions");
        await renderer.RenderAsync(nsPage, host);
        Require(renderer._mappingSelectors["south"].SelectedValue?.ToString() == "south",
            "PS draft leaked to NS");
        renderer._mappingSelectors["north"].SelectedValue = "west";
        await CaptureAsync(frame, "ns-wide.png");
        await CheckDiagramGroupsAsync("ns");
        await renderer.RenderAsync(psPage, host);
        Require(renderer._mappingSelectors["south"].SelectedValue?.ToString() == "east",
            "PS draft was lost during navigation");
        await renderer.ApplyMappingAsync(ps);
        Require(configurations["ds5/ds5"]["south"] == "east" &&
                configurations.Where(pair => pair.Key != "ds5/ds5").All(pair =>
                    pair.Value["south"] == "south"), "Save crossed route boundary");
        Require(commands.Count(command => command.StartsWith("mapping set ds5")) == 2,
            "Save should write only changed bindings");
        Require(statuses.Last().StartsWith("False:"), "Save reported failure");
        await renderer.RenderAsync(nsPage, host);
        Require(renderer._mappingSelectors["north"].SelectedValue?.ToString() == "west",
            "NS draft was lost");
        window.AppWindow.Resize(new SizeInt32(500, 900));
        await CaptureAsync(frame, "ns-narrow.png");
        Require(renderer._mappingDiagram!.IsCompact &&
                renderer._mappingDiagram.CalloutButtons.Values.All(button => button.ActualWidth > 100),
            "Narrow layout collapsed callouts");
        renderer._mappingDiagram.OpenEditor("north");
        await CaptureAsync(renderer._mappingDiagram.EditorContent!, "ns-picker-narrow.png");
        renderer._mappingDiagram.CloseEditor();
        failingAction = "mapping.save";
        await renderer.ApplyMappingAsync(ns);
        Require(!renderer._mappingLoaded && renderer._mappingSaveButton?.IsEnabled == false,
            "Failed save allowed further writes without rereading");
        Require(statuses.Last().StartsWith("True:"), "Failed save reported success");
        failingAction = null;
        pendingRead = new TaskCompletionSource<JsonElement>();
        var slowPs = renderer.RenderAsync(psPage, host);
        await renderer.RenderAsync(nsPage, host);
        pendingRead.SetResult(Reply("ds5", "ds5"));
        await slowPs;
        Require(renderer._mappingDefinition == ns && renderer._deviceMapping["south"] == "south",
            "Late PS reply overwrote NS page");
        pendingRead = null;
        failingAction = "mapping.read";
        await renderer.RenderAsync(psPage, host);
        Require(!renderer._mappingLoaded &&
                renderer._mappingSelectors.Values.All(selector => !selector.IsEnabled),
            "Failed read left default-looking controls writable");
        Require(renderer._mappingDiagram!.CalloutButtons.Values.All(button => !button.IsEnabled),
            "Failed read left callouts writable");
        await CaptureAsync(frame, "read-error.png");
        failureDetail = "Device rejected operation mapping.read: usage: mapping get|set target source|none|reset|save";
        await renderer.RenderAsync(nsPage, host);
        Require(renderer._mappingError?.IsOpen == true &&
                renderer._mappingError.Title == "独立映射需要配套固件" &&
                renderer._mappingStatusText?.Text == "固件需要更新",
            "Legacy firmware rejection was not explained");
        await CaptureAsync(frame, "legacy-firmware.png");
        var failedReadCommands = commands.Count;
        renderer._mappingDiagram!.OpenEditor("south");
        Require(renderer._mappingDiagram.EditorContent is null &&
                commands.Count == failedReadCommands, "Legacy firmware allowed mapping editing");
        failingAction = null;
        deviceDirty = true;
        await renderer.RenderAsync(psPage, host);
        Require(renderer._mappingDiagram!.Summary.Contains("设备尚未保存"),
            "Unsaved device state was presented as saved");
        deviceDirty = false;
        await renderer.RenderAsync(psPage, host);
        window.AppWindow.Resize(new SizeInt32(1120, 1000));
        frame.RequestedTheme = ElementTheme.Dark;
        frame.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            Windows.UI.Color.FromArgb(255, 26, 28, 33));
        await CaptureAsync(frame, "ps-dark.png");
        await renderer.RenderAsync(nsPage, host);
        await CaptureAsync(frame, "ns-dark.png");
        frame.RequestedTheme = ElementTheme.Light;
        frame.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            Windows.UI.Color.FromArgb(255, 249, 249, 249));
        for (var repeat = 0; repeat < 12; repeat++)
        {
            await renderer.RenderAsync(psPage, host);
            await Task.Delay(35);
            var retiredDiagram = renderer._mappingDiagram!;
            retiredDiagram.OpenEditor("south");
            await renderer.RenderAsync(nsPage, host);
            // Reproduce a delayed Unloaded/flyout callback after the renderer
            // has cleared and populated its dictionaries for another route.
            retiredDiagram.CloseEditor();
            await Task.Delay(35);
        }
        foreach (var route in module.Pages.Where(page => page.Sections.SelectMany(section => section.Controls)
                     .Any(control => control.Type == BridgeModuleControlType.MappingEditor)))
        {
            await renderer.RenderAsync(route, host);
            var definition = route.Sections.Single().Controls.Single();
            Require(renderer._mappingComboExpander is not null, "Missing combination editor");
            renderer._mappingComboExpander!.IsExpanded = true;
            var comboBody = (StackPanel)renderer._mappingComboExpander.Content;
            var comboSource = comboBody.Children.OfType<ComboBox>().Single();
            var comboTargets = comboBody.Children.OfType<Grid>().Single().Children.OfType<CheckBox>().ToArray();
            comboSource.SelectedValue = "east";
            comboTargets[0].IsChecked = true;
            comboTargets[2].IsChecked = true;
            Require(renderer._mappingSelectors["south"].SelectedValue?.ToString() == "east" &&
                    renderer._mappingSelectors["west"].SelectedValue?.ToString() == "east",
                "Combination did not assign two outputs to one physical source");
            comboTargets[0].IsChecked = false;
            Require(renderer._mappingSelectors["south"].SelectedValue?.ToString() == "none",
                "Removing a combination output did not disable that target");
            comboTargets[0].IsChecked = true;
            if (definition.MappingOutput == "xbox")
                Require(renderer._mappingDiagram!.AllTargets.Count == 17 && comboTargets.Length == 17,
                    "Xbox mapping exposes unsupported output buttons");
            renderer._mappingSelectors["west"].SelectedValue = "east";
            await renderer.ApplyMappingAsync(definition);
            Require(configurations[$"{definition.MappingProfile}/{definition.MappingOutput}"]["west"] == "east",
                "Pair write did not reach selected route");
            await CaptureAsync(frame, $"route-{definition.MappingProfile}-{definition.MappingOutput}.png");
        }
        window.AppWindow.Resize(new SizeInt32(1920, 1080));
        await CaptureAsync(frame, "mapping-large.png");
        window.AppWindow.Resize(new SizeInt32(860, 650));
        await CaptureAsync(frame, "mapping-short.png");
        Require(scroll.ScrollableHeight > 0, "Short viewport has no usable vertical scroll");
        scroll.ChangeView(null, scroll.ScrollableHeight, null, disableAnimation: true);
        await CaptureAsync(frame, "mapping-scrolled.png");
        Require(scroll.VerticalOffset > 0, "Mapping page failed to scroll");
        renderer._mappingDiagram!.OpenEditor("south");
        renderer.InvalidateConnection();
        Require(!renderer._mappingLoaded && renderer._mappingSaveButton?.IsEnabled == false,
            "Disconnect retained writable mapping");
        Require(renderer._mappingDiagram!.EditorContent is null, "Disconnect left an editor open");
        await File.WriteAllLinesAsync(Path.Combine(outputDirectory, "commands.txt"), commands);
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "result.txt"),
            "PASS six-route parameters and writes, combinations, draft isolation, delta save, picker navigation stress, native output targets, failure gating, legacy firmware, stale replies, disconnect, large/narrow/short/scrolled/light/dark rendering");

        async Task CheckDiagramGroupsAsync(string prefix)
        {
            for (var group = 0; group < renderer._mappingDiagram!.GroupCount; group++)
            {
                renderer._mappingDiagram!.SelectGroup(group);
                await Task.Delay(80);
                frame.UpdateLayout();
                var buttons = renderer._mappingDiagram.CalloutButtons.Values.ToArray();
                Require(buttons.Length > 0, $"Empty {prefix} diagram group {group}");
                var bounds = buttons.Select(button =>
                {
                    var origin = button.TransformToVisual(renderer._mappingDiagram)
                        .TransformPoint(new Windows.Foundation.Point(0, 0));
                    return new Windows.Foundation.Rect(origin,
                        new Windows.Foundation.Size(button.ActualWidth, button.ActualHeight));
                }).ToArray();
                for (var i = 0; i < bounds.Length; i++)
                {
                    Require(bounds[i].Width >= 100 && bounds[i].Height >= 50 &&
                            bounds[i].X >= -1 &&
                            bounds[i].Right <= renderer._mappingDiagram.ActualWidth + 1,
                        $"Out-of-bounds {prefix} callout in group {group}");
                    for (var j = i + 1; j < bounds.Length; j++)
                    {
                        var intersection = bounds[i];
                        intersection.Intersect(bounds[j]);
                        Require(intersection.IsEmpty, $"Overlapping {prefix} callouts in group {group}");
                    }
                }
                await CaptureAsync(frame, $"{prefix}-group-{group}.png");
            }
            renderer._mappingDiagram!.SelectGroup(0);
        }

        async Task CaptureAsync(FrameworkElement element, string fileName)
        {
            await Task.Delay(300);
            element.UpdateLayout();
            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync(element);
            Require(bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0, "Blank XAML surface");
            var pixels = (await bitmap.GetPixelsAsync()).ToArray();
            Require(pixels.Distinct().Count() > 8, "XAML rendering contains no visible controls");
            var file = await StorageFile.GetFileFromPathAsync(CreateFile(fileName));
            using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels);
            await encoder.FlushAsync();
        }
        string CreateFile(string name)
        {
            var path = Path.GetFullPath(Path.Combine(outputDirectory, name));
            using var file = File.Create(path);
            return path;
        }
    }
}
