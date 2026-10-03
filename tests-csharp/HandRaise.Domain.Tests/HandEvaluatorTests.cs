using HandRaise.Domain.Detection;

namespace HandRaise.Domain.Tests;

public sealed class HandEvaluatorTests
{
    [Fact]
    public void HandsBelowShouldersAreNotRaised()
    {
        var state = HandEvaluator.Evaluate(TestData.Pose(), TestData.Options());

        Assert.False(state.LeftRaised);
        Assert.False(state.RightRaised);
        Assert.Equal(0, state.Confidence);
    }

    [Fact]
    public void WristAtShoulderHeightIsNotRaised()
    {
        var points = TestData.Pose();
        points[PoseKeypoints.LeftWrist] = points[PoseKeypoints.LeftWrist] with
        {
            Y = points[PoseKeypoints.LeftShoulder].Y,
        };

        Assert.False(HandEvaluator.Evaluate(points, TestData.Options()).LeftRaised);
    }

    [Fact]
    public void LeftHandAboveShoulderIsRaised()
    {
        var points = TestData.Pose();
        points[PoseKeypoints.LeftWrist] = points[PoseKeypoints.LeftWrist] with { Y = 100 };

        var state = HandEvaluator.Evaluate(points, TestData.Options());

        Assert.True(state.LeftRaised);
        Assert.False(state.RightRaised);
        Assert.Equal(HandSide.Left, state.Hand);
    }

    [Fact]
    public void RightHandAboveShoulderIsRaised()
    {
        var points = TestData.Pose();
        points[PoseKeypoints.RightWrist] = points[PoseKeypoints.RightWrist] with { Y = 100 };

        var state = HandEvaluator.Evaluate(points, TestData.Options());

        Assert.False(state.LeftRaised);
        Assert.True(state.RightRaised);
        Assert.Equal(HandSide.Right, state.Hand);
    }

    [Fact]
    public void BothHandsCanBeRaised()
    {
        var points = TestData.Pose();
        points[PoseKeypoints.LeftWrist] = points[PoseKeypoints.LeftWrist] with { Y = 100 };
        points[PoseKeypoints.RightWrist] = points[PoseKeypoints.RightWrist] with { Y = 100 };

        var state = HandEvaluator.Evaluate(points, TestData.Options());

        Assert.True(state.LeftRaised);
        Assert.True(state.RightRaised);
        Assert.Equal(HandSide.Both, state.Hand);
        Assert.Equal(0.9, state.Confidence, 6);
    }

    [Fact]
    public void MissingWristIsIgnored()
    {
        var points = TestData.Pose();
        points[PoseKeypoints.LeftWrist] = new Keypoint(0, 0, 0);

        Assert.False(HandEvaluator.Evaluate(points, TestData.Options()).LeftRaised);
    }

    [Fact]
    public void LowConfidenceWristAndShoulderAreIgnored()
    {
        var points = TestData.Pose();
        points[PoseKeypoints.LeftWrist] = new Keypoint(100, 50, 0.49);
        points[PoseKeypoints.RightWrist] = points[PoseKeypoints.RightWrist] with { Y = 50 };
        points[PoseKeypoints.RightShoulder] = points[PoseKeypoints.RightShoulder] with
        {
            Confidence = 0.49,
        };

        var state = HandEvaluator.Evaluate(points, TestData.Options());

        Assert.False(state.LeftRaised);
        Assert.False(state.RightRaised);
    }

    [Fact]
    public void StrictModeRequiresWristAboveFace()
    {
        var points = TestData.Pose();
        points[PoseKeypoints.LeftWrist] = points[PoseKeypoints.LeftWrist] with { Y = 100 };

        Assert.True(HandEvaluator.Evaluate(points, TestData.Options()).LeftRaised);
        Assert.False(HandEvaluator.Evaluate(
            points,
            TestData.Options(strictMode: true)).LeftRaised);
    }

    [Fact]
    public void StrictModeAcceptsWristAboveEyesAndNose()
    {
        var points = TestData.Pose();
        points[PoseKeypoints.LeftWrist] = points[PoseKeypoints.LeftWrist] with { Y = 60 };

        Assert.True(HandEvaluator.Evaluate(
            points,
            TestData.Options(strictMode: true)).LeftRaised);
    }

    [Fact]
    public void FractionalMarginIsScaleInvariant()
    {
        var near = TestData.Pose();
        near[PoseKeypoints.LeftWrist] = near[PoseKeypoints.LeftWrist] with { Y = 120 };
        var far = TestData.Pose(0.5);
        far[PoseKeypoints.LeftWrist] = far[PoseKeypoints.LeftWrist] with { Y = 60 };

        Assert.True(HandEvaluator.Evaluate(near, TestData.Options()).LeftRaised);
        Assert.True(HandEvaluator.Evaluate(far, TestData.Options()).LeftRaised);
    }
}

