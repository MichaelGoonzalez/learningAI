using System.Runtime.InteropServices;
using HandRaise.Application.Capture;
using OpenCvSharp;

namespace HandRaise.Infrastructure.Windows.Capture;

public sealed class OpenCvVideoWriterSink : IVideoFrameSink
{
    private readonly string _path;
    private readonly Func<double> _framesPerSecond;
    private VideoWriter? _writer;
    private bool _disposed;

    public OpenCvVideoWriterSink(string path, double framesPerSecond)
        : this(path, () => framesPerSecond)
    {
    }

    public OpenCvVideoWriterSink(string path, Func<double> framesPerSecond)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(framesPerSecond);

        _path = Path.GetFullPath(path);
        _framesPerSecond = framesPerSecond;
    }

    public ValueTask WriteAsync(VideoFrame frame, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (_writer is null)
        {
            var framesPerSecond = _framesPerSecond();
            if (framesPerSecond <= 0)
            {
                throw new InvalidOperationException("El FPS del video de salida debe ser positivo.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            _writer = new VideoWriter(
                _path,
                FourCC.MP4V,
                framesPerSecond,
                new Size(frame.Width, frame.Height));
            if (!_writer.IsOpened())
            {
                _writer.Dispose();
                _writer = null;
                throw new IOException($"No fue posible abrir el video de salida '{_path}'.");
            }
        }

        using var mat = new Mat(frame.Height, frame.Width, MatType.CV_8UC3);
        var pixels = frame.Pixels.ToArray();
        Marshal.Copy(pixels, 0, mat.Data, pixels.Length);
        _writer.Write(mat);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _writer?.Dispose();
            _writer = null;
        }

        return ValueTask.CompletedTask;
    }
}
