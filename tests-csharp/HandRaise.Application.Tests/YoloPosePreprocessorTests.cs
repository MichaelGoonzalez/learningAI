using HandRaise.Application.Inference;

namespace HandRaise.Application.Tests;

public sealed class YoloPosePreprocessorTests
{
    [Fact]
    public void Preprocess_ConvertsBgrToPlanarRgbAndNormalizes()
    {
        var frame = new ImageFrame(2, 1, new byte[]
        {
            0, 127, 255,
            255, 0, 0
        });

        var result = YoloPosePreprocessor.Preprocess(frame, 2, 1, 114);

        Assert.Equal(1f, result.Tensor[0], precision: 5);
        Assert.Equal(0f, result.Tensor[1], precision: 5);
        Assert.Equal(127f / 255f, result.Tensor[2], precision: 5);
        Assert.Equal(0f, result.Tensor[3], precision: 5);
        Assert.Equal(0f, result.Tensor[4], precision: 5);
        Assert.Equal(1f, result.Tensor[5], precision: 5);
    }

    [Fact]
    public void Preprocess_LetterboxesWithoutDistortingAspectRatio()
    {
        var frame = new ImageFrame(4, 2, Enumerable.Repeat((byte)255, 24).ToArray());

        var result = YoloPosePreprocessor.Preprocess(frame, 4, 4, 114);

        Assert.Equal(1f, result.Transform.Scale);
        Assert.Equal(0, result.Transform.PaddingLeft);
        Assert.Equal(1, result.Transform.PaddingTop);
        Assert.Equal(114f / 255f, result.Tensor[0], precision: 5);
        Assert.Equal(1f, result.Tensor[4], precision: 5);
    }

    [Fact]
    public void LetterboxTransform_InvertsCoordinatesAndClampsThem()
    {
        var transform = new LetterboxTransform(320, 160, 640, 640, 2, 0, 160);

        Assert.Equal(50, transform.ToSourceX(100));
        Assert.Equal(20, transform.ToSourceY(200));
        Assert.Equal(0, transform.ToSourceY(100));
        Assert.Equal(320, transform.ToSourceX(700));
    }
}
