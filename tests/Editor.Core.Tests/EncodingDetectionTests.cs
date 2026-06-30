using System.Text;
using Editor.Core.Buffer;

namespace Editor.Core.Tests;

public class EncodingDetectionTests
{
    static EncodingDetectionTests()
    {
        // Mirror VimBuffer's static init so GetEncoding("shift-jis"/"euc-jp") resolves in tests
        // even if no VimBuffer has been constructed yet.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    private const string Japanese = "こんにちは世界。日本語のテキストです。";

    [Fact]
    public void DetectEncoding_Utf8Bom_ReturnsUtf8Bom()
    {
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetPreamble()
            .Concat(Encoding.UTF8.GetBytes(Japanese)).ToArray();
        Assert.Equal("utf-8-bom", VimBuffer.DetectEncoding(bytes));
    }

    [Fact]
    public void DetectEncoding_Utf16LeBom_ReturnsUtf16Le()
    {
        var bytes = Encoding.Unicode.GetPreamble()
            .Concat(Encoding.Unicode.GetBytes(Japanese)).ToArray();
        Assert.Equal("utf-16le", VimBuffer.DetectEncoding(bytes));
    }

    [Fact]
    public void DetectEncoding_PureAscii_ReturnsUtf8()
    {
        var bytes = Encoding.ASCII.GetBytes("plain ascii text\nsecond line\n");
        Assert.Equal("utf-8", VimBuffer.DetectEncoding(bytes));
    }

    [Fact]
    public void DetectEncoding_BomlessUtf8_ReturnsUtf8()
    {
        var bytes = Encoding.UTF8.GetBytes(Japanese);
        Assert.Equal("utf-8", VimBuffer.DetectEncoding(bytes));
    }

    [Fact]
    public void DetectEncoding_BomlessShiftJis_ReturnsShiftJis()
    {
        var bytes = Encoding.GetEncoding("shift-jis").GetBytes(Japanese);
        Assert.Equal("shift-jis", VimBuffer.DetectEncoding(bytes));
    }

    [Fact]
    public void DetectEncoding_BomlessEucJp_ReturnsEucJp()
    {
        var bytes = Encoding.GetEncoding("euc-jp").GetBytes(Japanese);
        Assert.Equal("euc-jp", VimBuffer.DetectEncoding(bytes));
    }

    [Fact]
    public void DetectEncoding_RoundTripsShiftJisThroughBuffer()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, Encoding.GetEncoding("shift-jis").GetBytes(Japanese));
            var buf = new VimBuffer(path);
            Assert.Equal("shift-jis", buf.FileEncoding);
            Assert.Equal(Japanese, buf.Text.GetText());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void DetectEncoding_RoundTripsEucJpThroughBuffer()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, Encoding.GetEncoding("euc-jp").GetBytes(Japanese));
            var buf = new VimBuffer(path);
            Assert.Equal("euc-jp", buf.FileEncoding);
            Assert.Equal(Japanese, buf.Text.GetText());
        }
        finally { File.Delete(path); }
    }
}
