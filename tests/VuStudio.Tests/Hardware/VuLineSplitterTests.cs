using System.Text;
using VuStudio.Hardware.Transport;

namespace VuStudio.Tests.Hardware;

public class VuLineSplitterTests
{
    private readonly VuLineSplitter _splitter = new();
    private readonly List<string> _lines = [];

    [Fact]
    public void Joins_fragments_and_accepts_any_line_ending()
    {
        Feed("<03050004");
        Assert.Empty(_lines);
        Feed("00000000\r\n<1305000400000001\n<0C0500040000\r0000\r\n");
        Assert.Equal(["<0305000400000000", "<1305000400000001", "<0C0500040000", "0000"], _lines);
    }

    [Fact]
    public void A_reply_start_ends_a_dangling_fragment()
    {
        Feed("<030500040000<0305000400000000\r\n");
        Assert.Equal(["<030500040000", "<0305000400000000"], _lines);
    }

    [Fact]
    public void Reset_drops_the_partial_line()
    {
        Feed("<0305000400");
        _splitter.Reset();
        Feed("dbg: hello\r\n");
        Assert.Equal(["dbg: hello"], _lines);
    }

    [Fact]
    public void Runaway_noise_is_bounded()
    {
        Feed(new string('x', VuLineSplitter.MaxLineLength * 3) + "\r\n<0305000400000000\r\n");
        Assert.All(_lines, l => Assert.True(l.Length <= VuLineSplitter.MaxLineLength));
        Assert.Equal("<0305000400000000", _lines[^1]);
    }

    private void Feed(string text) => _splitter.Feed(Encoding.ASCII.GetBytes(text), _lines.Add);
}
