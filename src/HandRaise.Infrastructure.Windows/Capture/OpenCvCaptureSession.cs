using System.Runtime.InteropServices;
using OpenCvSharp;
using System.Diagnostics;

namespace HandRaise.Infrastructure.Windows.Capture;

internal sealed class OpenCvCaptureSession : ICaptureSession
{
    private readonly VideoCapture _capture;

    public OpenCvCaptureSession(string source, CaptureOptions options)
    {
        _capture = new VideoCapture();
        ConfigureTimeouts(options);
        _capture.Open(source, VideoCaptureAPIs.ANY);
    }

    public OpenCvCaptureSession(int index, CaptureOptions options)
    {
        _capture = new VideoCapture();
        ConfigureTimeouts(options);
        _capture.Open(index, VideoCaptureAPIs.ANY);
    }

    public bool IsOpened => _capture.IsOpened();

    public double FramesPerSecond => _capture.Get(VideoCaptureProperties.Fps);

    public double PositionMilliseconds => _capture.Get(VideoCaptureProperties.PosMsec);

    public bool TryRead(out CapturedPixels? pixels)
    {
        var started = Stopwatch.GetTimestamp();
        using var mat = new Mat();
        if (!_capture.Read(mat) || mat.Empty())
        {
            pixels = null;
            return false;
        }

        using var bgr = EnsureBgrContinuous(mat);
        var length = checked(bgr.Rows * bgr.Cols * 3);
        var buffer = new byte[length];
        Marshal.Copy(bgr.Data, buffer, 0, length);
        pixels = new CapturedPixels(
            bgr.Cols,
            bgr.Rows,
            buffer,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return true;
    }

    public bool Restart() => _capture.Set(VideoCaptureProperties.PosFrames, 0);

    public void Dispose() => _capture.Dispose();

    private void ConfigureTimeouts(CaptureOptions options)
    {
        _capture.Set(VideoCaptureProperties.OpenTimeoutMsec, options.OpenTimeoutMilliseconds);
        _capture.Set(VideoCaptureProperties.ReadTimeoutMsec, options.ReadTimeoutMilliseconds);
    }

    private static Mat EnsureBgrContinuous(Mat source)
    {
        Mat converted;
        if (source.Channels() == 3)
        {
            converted = source.Clone();
        }
        else
        {
            converted = new Mat();
            var conversion = source.Channels() == 4
                ? ColorConversionCodes.BGRA2BGR
                : ColorConversionCodes.GRAY2BGR;
            Cv2.CvtColor(source, converted, conversion);
        }

        if (converted.IsContinuous())
        {
            return converted;
        }

        using (converted)
        {
            return converted.Clone();
        }
    }
}
