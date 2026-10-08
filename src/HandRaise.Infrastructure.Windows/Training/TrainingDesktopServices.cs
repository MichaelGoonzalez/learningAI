using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using HandRaise.Application.Inference;
using HandRaise.Application.Training;
using HandRaise.Domain.Training;
using OpenCvSharp;

namespace HandRaise.Infrastructure.Windows.Training;

public static class TrainingDesktopServices
{
    public static ImageFrame Decode(byte[] bytes)
    {
        if (bytes.Length > 40 * 1024 * 1024) throw new InvalidDataException("La imagen supera el tamaño permitido.");
        using var mat = Cv2.ImDecode(bytes, ImreadModes.Color);
        if (mat.Empty() || (long)mat.Width * mat.Height > 40_000_000) throw new InvalidDataException("No se pudo leer la imagen.");
        var pixels = new byte[checked(mat.Width * mat.Height * 3)];
        Marshal.Copy(mat.Data, pixels, 0, pixels.Length);
        return new(mat.Width, mat.Height, pixels);
    }

    public static async Task ImportVideoAsync(TrainingApplicationService service, Guid projectId, string file, CancellationToken ct)
    {
        // Local files only. Never opens a camera index, URL, or network stream.
        if (!Path.IsPathFullyQualified(file) || !File.Exists(file)
            || Path.GetExtension(file).ToLowerInvariant() is not (".mp4" or ".avi" or ".mov" or ".mkv"))
            throw new ArgumentException("Seleccione un archivo de video local.");
        string hash;
        await using (var stream = File.OpenRead(file)) hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
        using var video = new VideoCapture(file);
        if (!video.IsOpened()) throw new InvalidDataException("No se pudo abrir el video.");
        var count = video.FrameCount;
        if (count <= 0 || !double.IsFinite(video.Fps) || video.Fps <= 0) throw new InvalidDataException("El video no permite muestreo seguro.");
        using var mat = new Mat();
        for (var n = 0; n < Math.Min(30, count); n++)
        {
            ct.ThrowIfCancellationRequested();
            var index = (int)((long)n * count / Math.Min(30, count));
            video.Set(VideoCaptureProperties.PosFrames, index);
            if (!video.Read(mat) || mat.Empty()) continue;
            var frame = Decode(mat.ToBytes(".png"));
            await service.ImportFrameAsync(projectId, frame, TrainingSourceType.VideoFrame, "video:" + hash, index / video.Fps, ct);
        }
    }

    public static async Task<(bool Available, bool Gpu, string Detail)> InspectRuntimeAsync(TrainingRuntimeOptions options, CancellationToken ct)
    {
        try
        {
            var devices = await new ManagedTrainingRuntimeProbe().ProbeAsync(options, ct);
            return (devices.Any(d => d.Available && d.Device.Kind == TrainingDeviceKind.Cpu),
                devices.Any(d => d.Available && d.Device.Kind == TrainingDeviceKind.Gpu), "");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return (false, false, ex.Message); }
    }
}