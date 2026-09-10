namespace BridgeManager.Core;

public sealed record InputReportFrame(
    object Source, int Generation, ControllerInputSnapshot Input,
    uint PressedButtons, long ReportCount, DateTimeOffset ReceivedAt);

// One pending paint, regardless of the HID rate. Preserve short button presses
// between paints so mapping capture does not depend on the display frame rate.
public sealed class InputReportBuffer
{
    private readonly object _gate = new();
    private InputReportFrame? _pending;
    private object? _source;
    private int _generation;
    private uint _previousButtons;
    private long _count;

    public void Publish(object source, int generation, ControllerInputSnapshot input)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(source, _source) || generation != _generation)
            {
                _pending = null;
                _previousButtons = 0;
                _count = 0;
                _source = source;
                _generation = generation;
            }
            var pressed = input.Buttons & ~_previousButtons;
            _previousButtons = input.Buttons;
            _pending = new(source, generation, input,
                (_pending?.PressedButtons ?? 0) | pressed, ++_count, DateTimeOffset.UtcNow);
        }
    }

    public InputReportFrame? Drain()
    {
        lock (_gate)
        {
            var frame = _pending;
            _pending = null;
            return frame;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _pending = null;
            _source = null;
            _previousButtons = 0;
            _count = 0;
        }
    }
}
