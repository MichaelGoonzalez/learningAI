using HandRaise.Application.Inference;

namespace HandRaise.Application.Tests;

public sealed class YoloPosePostprocessorTests
{
    [Fact]
    public void Process_EndToEndYolo26_MapsBoxAndKeypointsToSource()
    {
        var model = Model(confidence: 0.5f);
        var attributes = 6 + model.KeypointCount * 3;
        var output = new float[attributes];
        output[0] = 100;
        output[1] = 180;
        output[2] = 300;
        output[3] = 500;
        output[4] = 0.9f;
        output[5] = 0;
        for (var index = 0; index < model.KeypointCount; index++)
        {
            output[6 + index * 3] = 200;
            output[6 + index * 3 + 1] = 260;
            output[6 + index * 3 + 2] = 0.8f;
        }

        var people = YoloPosePostprocessor.Process(
            output,
            [1, 1, attributes],
            new LetterboxTransform(320, 160, 640, 640, 2, 0, 160),
            model);

        var person = Assert.Single(people);
        Assert.Equal(new BoundingBox(50, 10, 150, 160), person.Box);
        Assert.Equal(100, person.Keypoints[0].X);
        Assert.Equal(50, person.Keypoints[0].Y);
        Assert.Equal(0.8, person.Keypoints[0].Confidence, precision: 5);
    }

    [Fact]
    public void Process_EndToEndYolo26_DropsLowConfidenceCandidates()
    {
        var model = Model(confidence: 0.5f);
        var attributes = 6 + model.KeypointCount * 3;
        var output = new float[attributes];
        output[4] = 0.49f;

        var people = YoloPosePostprocessor.Process(output, [1, 1, attributes], Identity(), model);

        Assert.Empty(people);
    }

    [Fact]
    public void Process_LegacyChannelFirst_AppliesNms()
    {
        var model = Model(confidence: 0.25f, iou: 0.5f);
        var attributes = 4 + model.ClassCount + model.KeypointCount * 3;
        const int candidates = 2;
        var output = new float[attributes * candidates];
        SetChannelFirst(output, candidates, 0, 0, 100);
        SetChannelFirst(output, candidates, 0, 1, 100);
        SetChannelFirst(output, candidates, 0, 2, 80);
        SetChannelFirst(output, candidates, 0, 3, 80);
        SetChannelFirst(output, candidates, 0, 4, 0.9f);
        SetChannelFirst(output, candidates, 1, 0, 102);
        SetChannelFirst(output, candidates, 1, 1, 102);
        SetChannelFirst(output, candidates, 1, 2, 80);
        SetChannelFirst(output, candidates, 1, 3, 80);
        SetChannelFirst(output, candidates, 1, 4, 0.8f);

        var people = YoloPosePostprocessor.Process(
            output, [1, attributes, candidates], Identity(), model);

        var person = Assert.Single(people);
        Assert.Equal(0.9f, person.Confidence);
        Assert.Equal(new BoundingBox(60, 60, 140, 140), person.Box);
    }

    [Fact]
    public void Process_LegacyCandidateFirst_IsSupported()
    {
        var model = Model(confidence: 0.25f);
        var attributes = 4 + model.ClassCount + model.KeypointCount * 3;
        var output = new float[attributes];
        output[0] = 100;
        output[1] = 100;
        output[2] = 20;
        output[3] = 40;
        output[4] = 0.7f;

        var people = YoloPosePostprocessor.Process(output, [1, 1, attributes], Identity(), model);

        Assert.Equal(new BoundingBox(90, 80, 110, 120), Assert.Single(people).Box);
    }

    [Fact]
    public void Process_WithUnexpectedShape_ExplainsTheContract()
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            YoloPosePostprocessor.Process(new float[10], [1, 2, 5], Identity(), Model()));

        Assert.Contains("no coincide con YOLO Pose", exception.Message);
    }

    private static ModelDescriptor Model(float confidence = 0.25f, float iou = 0.7f) =>
        new("model.onnx", 640, 640, 1, 17, confidence, iou, 300);

    private static LetterboxTransform Identity() =>
        new(640, 640, 640, 640, 1, 0, 0);

    private static void SetChannelFirst(
        float[] output,
        int candidates,
        int candidate,
        int attribute,
        float value) => output[attribute * candidates + candidate] = value;
}
