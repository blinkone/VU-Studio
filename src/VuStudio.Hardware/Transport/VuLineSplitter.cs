using System.Text;
using VuStudio.Hardware.Protocol;

namespace VuStudio.Hardware.Transport;

/// <summary>
/// Splits the hub's byte stream into lines. Lines end at <c>\r</c> or <c>\n</c>. A <c>&lt;</c> always starts a new
/// line, so a fragment left over from discarded input can never swallow the next reply.
/// </summary>
internal sealed class VuLineSplitter
{
    /// <summary>Longer runs without a terminator are noise and are dropped.</summary>
    public const int MaxLineLength = 4096;

    private readonly StringBuilder _line = new();

    public void Feed(ReadOnlySpan<byte> data, Action<string> emit)
    {
        foreach (var b in data)
        {
            var c = (char)b;
            if (c is '\r' or '\n')
            {
                Flush(emit);
                continue;
            }

            if (c == VuProtocol.ResponseStart)
            {
                Flush(emit);
            }
            else if (_line.Length >= MaxLineLength)
            {
                _line.Clear();
            }

            _line.Append(c);
        }
    }

    public void Reset() => _line.Clear();

    private void Flush(Action<string> emit)
    {
        if (_line.Length == 0)
        {
            return;
        }

        var line = _line.ToString();
        _line.Clear();
        emit(line);
    }
}
