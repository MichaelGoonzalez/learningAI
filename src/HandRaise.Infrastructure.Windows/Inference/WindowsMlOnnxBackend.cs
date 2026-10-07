using System.Diagnostics;
using HandRaise.Application.Hardware;
using HandRaise.Application.Inference;
using HandRaise.Domain.Models;
using Microsoft.ML.OnnxRuntime;

namespace HandRaise.Infrastructure.Windows.Inference;

public sealed class WindowsMlOnnxBackend : IInferenceBackend
{
    private readonly SemaphoreSlim _inferenceLock = new(1, 1);
    private InferenceSession? _session;
    private ModelDescriptor? _model;
    private string? _inputName;
    private string? _outputName;
    private InferenceExecutionInfo _executionInfo;

    public WindowsMlOnnxBackend(DeviceInfo device)
    {
        Device = device ?? throw new ArgumentNullException(nameof(device));
        _executionInfo = new InferenceExecutionInfo(
            "not-loaded", device.Id, device.Name, false, "La sesión aún no fue creada.");
    }

    public DeviceInfo Device { get; }

    public InferenceExecutionInfo ExecutionInfo => _executionInfo;

    public ValueTask LoadAsync(ModelDescriptor model, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        model.Validate();
        if (!File.Exists(model.Path))
        {
            throw new FileNotFoundException("No se encontró el modelo ONNX.", model.Path);
        }

        var (sessionOptions, executionInfo) = CreateSessionOptions(Device);
        using (sessionOptions)
        {
            var newSession = new InferenceSession(model.Path, sessionOptions);
            try
            {
                ValidateModelContract(newSession, model);
                var inputName = newSession.InputNames.Single();
                var outputName = newSession.OutputNames.First();
                Warmup(newSession, model, inputName, outputName);

                var previous = Interlocked.Exchange(ref _session, newSession);
                _model = model;
                _inputName = inputName;
                _outputName = outputName;
                _executionInfo = executionInfo;
                previous?.Dispose();
                newSession = null!;
            }
            finally
            {
                newSession?.Dispose();
            }
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask<PoseBatch> InferAsync(
        ImageFrame frame,
        CancellationToken cancellationToken = default)
    {
        var session = _session ?? throw new InvalidOperationException("El backend no tiene un modelo cargado.");
        var model = _model ?? throw new InvalidOperationException("Falta el descriptor del modelo.");
        var inputName = _inputName!;
        var outputName = _outputName!;
        var preprocessWatch = Stopwatch.StartNew();
        var preprocessed = YoloPosePreprocessor.Preprocess(
            frame,
            model.InputWidth,
            model.InputHeight,
            paddingValue: 114);
        preprocessWatch.Stop();

        await _inferenceLock.WaitAsync(cancellationToken);
        try
        {
            using var input = OrtValue.CreateTensorValueFromMemory(
                preprocessed.Tensor,
                [1, 3, model.InputHeight, model.InputWidth]);
            var inputs = new Dictionary<string, OrtValue> { [inputName] = input };
            using var runOptions = new RunOptions();
            var stopwatch = Stopwatch.StartNew();
            using var outputs = session.Run(runOptions, inputs, [outputName]);
            stopwatch.Stop();

            var output = outputs[0];
            var tensorInfo = output.GetTensorTypeAndShape();
            var postprocessWatch = Stopwatch.StartNew();
            var people = YoloPosePostprocessor.Process(
                output.GetTensorDataAsSpan<float>(),
                tensorInfo.Shape,
                preprocessed.Transform,
                model);
            postprocessWatch.Stop();
            return new PoseBatch(
                people,
                preprocessWatch.Elapsed,
                stopwatch.Elapsed,
                postprocessWatch.Elapsed);
        }
        finally
        {
            _inferenceLock.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _session, null)?.Dispose();
        _inferenceLock.Dispose();
        return ValueTask.CompletedTask;
    }

    private static (SessionOptions Options, InferenceExecutionInfo Info) CreateSessionOptions(
        DeviceInfo device)
    {
        if (!device.RuntimeAvailable)
        {
            throw new InvalidOperationException(
                device.UnavailableReason ?? $"El runtime de '{device.Id}' no está disponible.");
        }

        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
        };

        if (device.Backend == InferenceBackend.Cpu)
        {
            return (
                options,
                new InferenceExecutionInfo(
                    "CPUExecutionProvider",
                    device.Id,
                    device.Name,
                    false,
                    "CPUExecutionProvider es el provider predeterminado de esta sesión Windows ML."));
        }

        if (device.Backend != InferenceBackend.DirectMl)
        {
            options.Dispose();
            throw new NotSupportedException(
                "C3 admite CPU y DirectML. Los providers CUDA específicos se integrarán después de validar su distribución.");
        }

        var environment = OrtEnv.Instance();
        var expectedVendor = ParseHexComponent(device.Id, 1);
        var expectedDevice = ParseHexComponent(device.Id, 2);
        var epDevice = environment.GetEpDevices().FirstOrDefault(candidate =>
            candidate.HardwareDevice.Type == OrtHardwareDeviceType.GPU &&
            (expectedVendor is null || candidate.HardwareDevice.VendorId == expectedVendor) &&
            (expectedDevice is null || candidate.HardwareDevice.DeviceId == expectedDevice));

        if (epDevice is null)
        {
            options.Dispose();
            throw new InvalidOperationException(
                $"Windows ML no publicó un Execution Provider GPU compatible con '{device.Name}'.");
        }

        options.EnableMemoryPattern = false;
        options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
        options.AppendExecutionProvider(environment, [epDevice], new Dictionary<string, string>());
        return (
            options,
            new InferenceExecutionInfo(
                epDevice.EpName,
                device.Id,
                device.Name,
                true,
                $"EpDevice seleccionado explícitamente: {epDevice.EpName}, " +
                $"vendor=0x{epDevice.HardwareDevice.VendorId:X4}, " +
                $"device=0x{epDevice.HardwareDevice.DeviceId:X4}."));
    }

    private static uint? ParseHexComponent(string deviceId, int index)
    {
        var components = deviceId.Split(':');
        return components.Length > index &&
            uint.TryParse(components[index], System.Globalization.NumberStyles.HexNumber, null, out var value)
            ? value
            : null;
    }

    private static void ValidateModelContract(InferenceSession session, ModelDescriptor model)
    {
        if (session.InputNames.Count != 1)
        {
            throw new InvalidDataException("El modelo debe tener exactamente una entrada.");
        }

        var metadata = session.InputMetadata[session.InputNames.Single()];
        var dimensions = metadata.Dimensions;
        if (dimensions.Length != 4 ||
            (dimensions[1] > 0 && dimensions[1] != 3) ||
            (dimensions[2] > 0 && dimensions[2] != model.InputHeight) ||
            (dimensions[3] > 0 && dimensions[3] != model.InputWidth))
        {
            throw new InvalidDataException(
                $"La entrada ONNX [{string.Join(',', dimensions)}] no coincide con " +
                $"[1,3,{model.InputHeight},{model.InputWidth}].");
        }

        if (session.OutputNames.Count == 0)
        {
            throw new InvalidDataException("El modelo ONNX no tiene salidas.");
        }
    }

    private static void Warmup(InferenceSession session, ModelDescriptor model, string inputName, string outputName)
    {
        try
        {
            var dummyTensor = new float[1 * 3 * model.InputHeight * model.InputWidth];
            using var input = OrtValue.CreateTensorValueFromMemory(
                dummyTensor,
                [1, 3, model.InputHeight, model.InputWidth]);
            var inputs = new Dictionary<string, OrtValue> { [inputName] = input };
            using var runOptions = new RunOptions();
            using var outputs = session.Run(runOptions, inputs, [outputName]);
        }
        catch
        {
            // El calentamiento es de mejor esfuerzo para precompilar shaders DirectML antes del primer frame en vivo
        }
    }
}
