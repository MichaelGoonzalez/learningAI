using System.Runtime.InteropServices;
using HandRaise.Application.Capture;
using OpenCvSharp;

namespace HandRaise.Infrastructure.Windows.Capture;

public static class OpenCvFrameJpegEncoder
{
    public static byte[] Encode(VideoFrame frame, int quality = 85)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (quality is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(quality));
        using var image = new Mat(frame.Height, frame.Width, MatType.CV_8UC3);
        var pixels = frame.Pixels.ToArray();
        Marshal.Copy(pixels, 0, image.Data, pixels.Length);
        if (!Cv2.ImEncode(".jpg", image, out var encoded,
                [new ImageEncodingParam(ImwriteFlags.JpegQuality, quality)]))
            throw new IOException("OpenCV no pudo comprimir el frame JPEG.");
        return encoded;
    }
}
