using System.Diagnostics;
using System.Globalization;

namespace HandRaise.Infrastructure.Windows.Hardware;

public sealed class NvidiaSmiProbe
{
    private const string QueryArguments =
        "--query-gpu=index,uuid,name,memory.total,driver_version --format=csv,noheader,nounits";

    private readonly TimeSpan _timeout;
    private readonly string _executable;

    public NvidiaSmiProbe(TimeSpan timeout, string executable = "nvidia-smi")
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        _timeout = timeout;
        _executable = executable;
    }

    internal static IReadOnlyList<NvidiaGpu> Parse(string output)
    {
        var devices = new List<NvidiaGpu>();
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split(',', StringSplitOptions.TrimEntries);
            if (fields.Length != 5 ||
                !int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
            {
                continue;
            }

            long? vram = long.TryParse(
                fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedVram)
                ? parsedVram
                : null;
            devices.Add(new NvidiaGpu(index, fields[1], fields[2], vram, fields[4]));
        }

        return devices;
    }

    internal async Task<(IReadOnlyList<NvidiaGpu> Devices, string? Warning)> DetectAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _executable,
                    Arguments = QueryArguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            if (!process.Start())
            {
                return ([], "nvidia-smi no pudo iniciarse.");
            }

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_timeout);

            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                process.Kill(entireProcessTree: true);
                return ([], $"nvidia-smi excedió el tiempo límite de {_timeout.TotalSeconds:0.#} s.");
            }

            var output = await outputTask;
            var error = await errorTask;
            if (process.ExitCode != 0)
            {
                var detail = string.IsNullOrWhiteSpace(error) ? "sin detalle" : error.Trim();
                return ([], $"nvidia-smi terminó con código {process.ExitCode}: {detail}");
            }

            return (Parse(output), null);
        }
        catch (Exception exception) when (
            exception is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            return ([], "nvidia-smi no está instalado o no está disponible en PATH.");
        }
    }
}
