using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Runtime.CompilerServices;

namespace BridgeManager.Modern;

internal static class PageScrolling
{
    private sealed class WheelState
    {
        public double? Target;
        public bool FlushQueued;
    }
    private static readonly ConditionalWeakTable<ScrollViewer, WheelState> WheelStates = new();

    internal static void Attach(ScrollViewer page)
    {
        var state = WheelStates.GetOrCreateValue(page);
        page.ViewChanged += (_, args) =>
        {
            if (!args.IsIntermediate && !state.FlushQueued) state.Target = null;
        };
        page.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) =>
            state.Target = null), handledEventsToo: true);
        UIElement? wheelHost = null;
        var wheelHandler = new PointerEventHandler((_, args) =>
        {
            if (args.OriginalSource is not DependencyObject source) return;
            var properties = args.GetCurrentPoint(page).Properties;
            if (properties.IsHorizontalMouseWheel) return;
            if (RouteVerticalWheel(page, source, properties.MouseWheelDelta, args.Handled))
                args.Handled = true;
        });
        page.Loaded += (_, _) => UpdateContentWidth();
        page.SizeChanged += (_, _) => UpdateContentWidth();
        UpdateContentWidth();

        void UpdateContentWidth()
        {
            // Handle at content level, before the outer ScrollViewer's native
            // wheel handler. GridView's non-scrolling inner presenter can mark
            // an event handled; observing handled events recovers that case
            // without applying the outer viewer's movement a second time.
            if (page.Content is UIElement host && !ReferenceEquals(wheelHost, host))
            {
                wheelHost?.RemoveHandler(UIElement.PointerWheelChangedEvent, wheelHandler);
                wheelHost = host;
                host.AddHandler(UIElement.PointerWheelChangedEvent, wheelHandler, handledEventsToo: true);
            }
            if (page.ActualWidth <= 0 || page.Content is not FrameworkElement content) return;
            // Give the scroll presenter a measured width, not just a stretched
            // arrange width; otherwise its extent can stay narrow after maximize.
            var width = Math.Min(content.MaxWidth,
                Math.Max(0, page.ActualWidth - page.Padding.Left - page.Padding.Right));
            if (double.IsNaN(content.Width) || Math.Abs(content.Width - width) > 0.5)
                content.Width = width;
        }

    }

    // Shared by the routed handler and offline UI regression checks. Preserve
    // real nested scroll areas (logs, open lists), not inert list presenters.
    internal static bool RouteVerticalWheel(ScrollViewer page, DependencyObject source,
        int wheelDelta, bool alreadyHandled)
    {
        if (wheelDelta == 0 || page.VerticalScrollMode == ScrollMode.Disabled ||
            page.ScrollableHeight <= 0) return false;
        var belongsToPage = false;
        for (DependencyObject? cursor = source; cursor is not null;
             cursor = VisualTreeHelper.GetParent(cursor))
        {
            if (ReferenceEquals(cursor, page)) { belongsToPage = true; break; }
            if (cursor is ComboBox { IsDropDownOpen: true }) return false;
            if (alreadyHandled && cursor is NumberBox or Slider) return false;
            if (cursor is ScrollViewer nested && nested.VerticalScrollMode != ScrollMode.Disabled &&
                nested.ScrollableHeight > 0.5) return false;
        }
        if (!belongsToPage) return false;
        var state = WheelStates.GetOrCreateValue(page);
        // ChangeView updates VerticalOffset asynchronously. Accumulating against
        // that stale offset loses fast notches (or reverses direction). Coalesce
        // one UI-thread flush using the requested target, not the last layout.
        var previous = state.Target ?? page.VerticalOffset;
        var next = Math.Clamp(previous - wheelDelta * 0.8, 0, page.ScrollableHeight);
        if (Math.Abs(next - previous) < 0.5)
            return state.FlushQueued || Math.Abs(previous - page.VerticalOffset) >= 0.5;
        state.Target = next;
        if (!state.FlushQueued)
        {
            state.FlushQueued = true;
            if (!page.DispatcherQueue.TryEnqueue(() =>
            {
                state.FlushQueued = false;
                if (state.Target is not double target) return;
                if (!page.ChangeView(null, Math.Clamp(target, 0, page.ScrollableHeight), null,
                    disableAnimation: true)) state.Target = null;
            }))
            {
                state.FlushQueued = false;
                state.Target = null;
                return false;
            }
        }
        return true;
    }
}
