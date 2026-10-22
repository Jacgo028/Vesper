using UnityTranslator.App;

namespace UnityTranslator.App.Tests;

public sealed class OcrWorkerHostTests
{
    [Fact]
    public void ParsesWordBoxesAndConfidence()
    {
        const string tsv = "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext\n" +
                           "5\t1\t1\t1\t1\t1\t10\t20\t50\t15\t91.5\tHello\n" +
                           "5\t1\t1\t1\t1\t2\t65\t20\t30\t15\t85.0\tworld\n" +
                           "5\t1\t1\t1\t2\t1\t10\t50\t80\t20\t25.0\tNoise\n";
        var boxes = OcrWorkerHost.ParseTsv(tsv);
        Assert.Equal(2, boxes.Count);
        Assert.Equal("Hello world", boxes[0].Text);
        Assert.Equal(10, boxes[0].X);
        Assert.Equal(85, boxes[0].Width);
        Assert.InRange(boxes[0].Confidence, 0.85f, 0.92f);
    }
}
