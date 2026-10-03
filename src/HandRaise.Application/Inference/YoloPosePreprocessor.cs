namespace HandRaise.Application.Inference;

public static class YoloPosePreprocessor
{
    public static PreprocessedImage Preprocess(
        ImageFrame frame,
        int targetWidth,
        int targetHeight,
        byte paddingValue)
    {
        if (targetWidth <= 0 || targetHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetWidth));
        }

        var scale = Math.Min(
            targetWidth / (float)frame.Width,
            targetHeight / (float)frame.Height);
        var resizedWidth = Math.Max(1, (int)MathF.Round(frame.Width * scale));
        var resizedHeight = Math.Max(1, (int)MathF.Round(frame.Height * scale));
        var paddingLeft = (int)MathF.Round((targetWidth - resizedWidth) / 2f - 0.1f);
        var paddingTop = (int)MathF.Round((targetHeight - resizedHeight) / 2f - 0.1f);
        var resizeScaleX = frame.Width / (float)resizedWidth;
        var resizeScaleY = frame.Height / (float)resizedHeight;
        var planeSize = checked(targetWidth * targetHeight);
        var tensor = new float[checked(planeSize * 3)];
        Array.Fill(tensor, paddingValue / 255f);

        var pixels = frame.BgrPixels.Span;
        for (var targetY = 0; targetY < resizedHeight; targetY++)
        {
            var sourceY = (targetY + 0.5f) * resizeScaleY - 0.5f;
            var y0 = Math.Clamp((int)MathF.Floor(sourceY), 0, frame.Height - 1);
            var y1 = Math.Min(y0 + 1, frame.Height - 1);
            var yWeight = Math.Clamp(sourceY - MathF.Floor(sourceY), 0, 1);

            for (var targetX = 0; targetX < resizedWidth; targetX++)
            {
                var sourceX = (targetX + 0.5f) * resizeScaleX - 0.5f;
                var x0 = Math.Clamp((int)MathF.Floor(sourceX), 0, frame.Width - 1);
                var x1 = Math.Min(x0 + 1, frame.Width - 1);
                var xWeight = Math.Clamp(sourceX - MathF.Floor(sourceX), 0, 1);
                var outputIndex = (targetY + paddingTop) * targetWidth + targetX + paddingLeft;

                for (var bgrChannel = 0; bgrChannel < 3; bgrChannel++)
                {
                    var top = Lerp(
                        pixels[(y0 * frame.Width + x0) * 3 + bgrChannel],
                        pixels[(y0 * frame.Width + x1) * 3 + bgrChannel],
                        xWeight);
                    var bottom = Lerp(
                        pixels[(y1 * frame.Width + x0) * 3 + bgrChannel],
                        pixels[(y1 * frame.Width + x1) * 3 + bgrChannel],
                        xWeight);
                    var rgbPlane = 2 - bgrChannel;
                    tensor[rgbPlane * planeSize + outputIndex] = Lerp(top, bottom, yWeight) / 255f;
                }
            }
        }

        return new PreprocessedImage(
            tensor,
            new LetterboxTransform(
                frame.Width,
                frame.Height,
                targetWidth,
                targetHeight,
                scale,
                paddingLeft,
                paddingTop));
    }

    private static float Lerp(float left, float right, float weight) =>
        left + (right - left) * weight;
}
