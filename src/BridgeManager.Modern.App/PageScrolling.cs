using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace BridgeManager.Modern;

internal static class PageScrolling
{
    internal static void Attach(ScrollViewer page)
    {
        page.Loaded += (_, _) => UpdateContentWidth();
        page.SizeChanged += (_, _) => UpdateContentWidth();
        UpdateContentWidth();

        void UpdateContentWidth()
        {
            if (page.ActualWidth <= 0 || page.Content is not FrameworkElement content) return;
            // Give the scroll presenter a measured width, not just a stretched
            // arrange width; otherwise its extent can stay narrow after maximize.
            var width = Math.Min(content.MaxWidth,
                Math.Max(0, page.ActualWidth - page.Padding.Left - page.Padding.Right));
            if (double.IsNaN(content.Width) || Math.Abs(content.Width - width) > 0.5)
                content.Width = width;
        }

        page.AddHandler(UIElement.PointerWheelChangedEvent,
            new PointerEventHandler((_, args) =>
            {
                if (args.Handled || args.OriginalSource is not DependencyObject source) return;
                var properties = args.GetCurrentPoint(page).Properties;
                if (properties.IsHorizontalMouseWheel) return;
                // A nested log/list retains its native scroll owner. Only wheel
                // events from passive page content are routed to the page.
                for (var cursor = source; cursor is not null && cursor != page;
                     cursor = VisualTreeHelper.GetParent(cursor))
                {
                    if (cursor is ScrollViewer or ComboBox or NumberBox or Slider) return;
                }
                var next = Math.Clamp(page.VerticalOffset - properties.MouseWheelDelta * 0.8,
                    0, page.ScrollableHeight);
                if (Math.Abs(next - page.VerticalOffset) < 0.5) return;
                page.ChangeView(null, next, null, disableAnimation: true);
                args.Handled = true;
            }), handledEventsToo: false);
    }
}
