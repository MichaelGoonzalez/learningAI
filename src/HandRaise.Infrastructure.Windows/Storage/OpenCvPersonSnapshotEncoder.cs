using System.Runtime.InteropServices;
using HandRaise.Application.Capture;
using HandRaise.Application.Inference;
using HandRaise.Application.Storage;
using OpenCvSharp;

namespace HandRaise.Infrastructure.Windows.Storage;

public sealed class OpenCvPersonSnapshotEncoder : IPersonSnapshotEncoder
{
    public byte[] Encode(VideoFrame frame, BoundingBox box, SnapshotEncodingOptions options)
    {
        ArgumentNullException.ThrowIfNull(frame);
        options.Validate();
        var marginX = box.Width * options.MarginRatio;
        var marginY = box.Height * options.MarginRatio;
        var left = Math.Clamp((int)Math.Floor(box.Left - marginX), 0, frame.Width - 1);
        var top = Math.Clamp((int)Math.Floor(box.Top - marginY), 0, frame.Height - 1);
        var right = Math.Clamp((int)Math.Ceiling(box.Right + marginX), left + 1, frame.Width);
        var bottom = Math.Clamp((int)Math.Ceiling(box.Bottom + marginY), top + 1, frame.Height);

        using var source = new Mat(frame.Height, frame.Width, MatType.CV_8UC3);
        var pixels = frame.Pixels.ToArray();
        Marshal.Copy(pixels, 0, source.Data, pixels.Length);
        using var crop = new Mat(source, new Rect(left, top, right - left, bottom - top));
        if (!Cv2.ImEncode(
                ".jpg",
                crop,
                out var encoded,
                [new ImageEncodingParam(ImwriteFlags.JpegQuality, options.JpegQuality)]))
        {
            throw new IOException("OpenCV no pudo comprimir el snapshot JPEG.");
        }

        return encoded;
    }
}
