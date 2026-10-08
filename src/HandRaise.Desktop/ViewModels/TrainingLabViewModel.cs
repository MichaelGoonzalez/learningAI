using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HandRaise.Application.Training;
using HandRaise.Desktop.Services;
using HandRaise.Domain.Training;
using HandRaise.Infrastructure.Windows.Training;
using Microsoft.Win32;

namespace HandRaise.Desktop.ViewModels;

public sealed record TrainingProjectRow(Guid Id, string Name, string Objects, string State, string Modified, string Version);
public sealed record TrainingVersion(string Id, string Label);

public static class TrainingClassEdits
{
    public static IReadOnlyList<TrainingClass> Apply(IReadOnlyList<TrainingClass> existing, IReadOnlyList<string> names)
    {
        var matches = names.Select(n => existing.FirstOrDefault(c => string.Equals(c.Name, n, StringComparison.OrdinalIgnoreCase))).ToArray();
        var reserved = matches.Where(c => c != null).Select(c => c!.Id).ToHashSet();
        return names.Select((name, index) =>
        {
            var id = matches[index]?.Id ?? (index < existing.Count && !reserved.Contains(existing[index].Id) ? existing[index].Id : Guid.NewGuid());
            reserved.Add(id);
            return new TrainingClass(id, name);
        }).ToArray();
    }
}

public sealed class TrainingLabViewModel : ObservableObject, IAsyncDisposable
{
    private readonly NodeHostController _host;
    private readonly Action _use;
    private readonly TrainingPaths _paths;
    private readonly CancellationTokenSource _closing = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private TrainingApplicationService? _core;
    private TrainingProject? _project;
    private TrainingJob? _job;
    private TrainingProjectRow? _selectedProject;
    private TrainingVersion? _version;
    private TrainingClass? _activeClass;
    private BitmapSource? _image;
    private int _index, _boxIndex = -1, _profile = 1;
    private bool _busy, _polling, _dirty, _loading, _runtimeAvailable, _preview, _canResumePreparation;
    private bool _checkingDevices, _deviceProbeFailed, _deviceAvailabilityConfirmed;
    private TrainingRuntimeState _runtimeState = TrainingRuntimeState.Checking;
    private bool _runtimeRequiresPreparation;
    private string _name = "", _classNames = "", _message = "", _details = "", _runtime = "Entorno pendiente de comprobar";
    private Task? _operation;
    private Task? _poll;
    private readonly ITrainingRuntimeManager _provisioner;
    private Task? _runtimeCheck;
    private Task? _deviceCheck;
    private CancellationTokenSource? _preparation;
    private TrainingDeviceCapability? _selectedDevice;
    private double _preparationPercent;
    private string _preparationSize = "El tamaño se comprobará al preparar los componentes.";

    public TrainingLabViewModel(NodeHostController host, Action use, TrainingPaths? paths = null,
        ITrainingRuntimeManager? provisioner = null)
    {
        _host = host; _use = use;
        _paths = paths ?? new TrainingPaths();
        _provisioner = provisioner ?? new TrainingRuntimeProvisioner(_paths);
        PrepareRuntimeCommand = Command(PrepareRuntimeAsync);
        CancelPreparationCommand = new RelayCommand(() => _preparation?.Cancel());
        UseCpuCommand = new RelayCommand(() => SelectedDevice = Devices.FirstOrDefault(d => d.Device.Kind == TrainingDeviceKind.Cpu && d.Available));
        OpenTrainingLogCommand = new RelayCommand(OpenTrainingLog);
        RefreshCommand = Command(InitializeAsync);
        DeepCheckCommand = new AsyncRelayCommand(() => { BeginRuntimeCheck(true); return Task.CompletedTask; });
        CheckDevicesCommand = new AsyncRelayCommand(() => { BeginDeviceCheck(); return Task.CompletedTask; });
        NewCommand = Command(async () => { await SaveDraftAsync(); _project = null; _job = null; _preview = false; _dirty = false;
            Name = ""; ClassNames = ""; Image = null; Boxes.Clear(); Classes.Clear(); Versions.Clear(); Version = null; History.Clear(); Notify(); });
        SaveProjectCommand = Command(SaveProjectAsync);
        ImportImagesCommand = Command(ImportImagesAsync);
        ImportFolderCommand = Command(ImportFolderAsync);
        ImportVideoCommand = Command(ImportVideoAsync);
        PreviousCommand = Command(() => ChangeImageAsync(-1));
        NextCommand = Command(() => ChangeImageAsync(1));
        ReviewCommand = Command(async () => { await SaveImageAsync(true); Message = "Imagen revisada y guardada."; });
        DeleteBoxCommand = new RelayCommand(() => { if (SelectedBox >= 0 && SelectedBox < Boxes.Count) Boxes.RemoveAt(SelectedBox); });
        ChangeClassCommand = new RelayCommand(() => { if (ActiveClass != null && SelectedBox >= 0 && SelectedBox < Boxes.Count) Boxes[SelectedBox] = Boxes[SelectedBox] with { ClassId = ActiveClass.Id }; });
        PrepareCommand = Command(PrepareAsync);
        TrainCommand = Command(TrainAsync);
        CancelCommand = new AsyncRelayCommand(async () => { try { if (_core != null && _job is { IsTerminal: false }) await _core.CancelAsync(_job.Id, _closing.Token); } catch (Exception ex) { ShowError(ex); } });
        TestCommand = Command(TestAsync);
        UseCommand = new RelayCommand(() => { if (Version != null) _use(); });
        ReturnToEditorCommand = Command(async () => { _preview = false; await LoadImageAsync(); });
        Boxes.CollectionChanged += (_, _) => { if (_project != null && !_loading && !_preview) { _dirty = true; OnPropertyChanged(nameof(ImageStatus)); } };
        _timer.Tick += (_, _) => { if (!_busy && !_polling && _core != null) _poll = PollSafeAsync(); };
    }

    public ObservableCollection<TrainingProjectRow> Projects { get; } = [];
    public ObservableCollection<TrainingClass> Classes { get; } = [];
    public ObservableCollection<BoundingBoxAnnotation> Boxes { get; } = [];
    public ObservableCollection<TrainingVersion> Versions { get; } = [];
    public ObservableCollection<string> History { get; } = [];
    public ObservableCollection<TrainingDeviceCapability> Devices { get; } = [];
    public TrainingDeviceCapability? SelectedDevice
    {
        get => _selectedDevice;
        set { if (value is { Available: false }) return; SetProperty(ref _selectedDevice, value); Notify(); }
    }
    public bool IsPreparingRuntime => _preparation != null;
    public bool IsCheckingRuntime => _runtimeState == TrainingRuntimeState.Checking;
    public bool NeedsPreparation => _runtimeState == TrainingRuntimeState.NotInstalled
        || _runtimeState == TrainingRuntimeState.Error && _runtimeRequiresPreparation;
    public string PreparationActionText => _canResumePreparation ? "Continuar preparación" : "Preparar entorno / Reintentar";
    public double PreparationPercent { get => _preparationPercent; private set => SetProperty(ref _preparationPercent, value); }
    public string PreparationSize { get => _preparationSize; private set => SetProperty(ref _preparationSize, value); }
    public bool IsCheckingDevices => _checkingDevices;
    public bool DeviceProbeFailed => _deviceProbeFailed;
    public bool GpuUnavailable => _runtimeAvailable && _deviceAvailabilityConfirmed && !_checkingDevices
        && !Devices.Any(d => d.Device.Kind == TrainingDeviceKind.Gpu && d.Available);
    public IAsyncRelayCommand PrepareRuntimeCommand { get; }
    public IRelayCommand CancelPreparationCommand { get; }
    public IRelayCommand UseCpuCommand { get; }
    public TrainingProjectRow? SelectedProject { get => _selectedProject; set { if (SetProperty(ref _selectedProject, value) && value != null && !_loading && !_busy) _operation = RunAsync(() => OpenAsync(value.Id)); } }
    public TrainingVersion? Version { get => _version; set { SetProperty(ref _version, value); OnPropertyChanged(nameof(HasResult)); OnPropertyChanged(nameof(VersionMetrics)); } }
    public string VersionMetrics => Version == null ? "" : MetricsText(_host.ModelRegistry?.GetModel(Version.Id)?.CustomTraining?.ValidationMetrics);
    public TrainingClass? ActiveClass { get => _activeClass; set => SetProperty(ref _activeClass, value); }
    public int SelectedBox { get => _boxIndex; set => SetProperty(ref _boxIndex, value); }
    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public string ClassNames { get => _classNames; set => SetProperty(ref _classNames, value); }
    public int ProfileIndex { get => _profile; set => SetProperty(ref _profile, value); }
    public BitmapSource? Image { get => _image; private set => SetProperty(ref _image, value); }
    public string Message { get => _message; private set => SetProperty(ref _message, value); }
    public string Details { get => _details; private set { if (SetProperty(ref _details, value)) OnPropertyChanged(nameof(TechnicalDetails)); } }
    public string Runtime { get => _runtime; private set => SetProperty(ref _runtime, value); }
    public bool IsBusy => _busy;
    public bool CanEdit => !_busy && _job is not { IsTerminal: false };
    public bool CanSelectProject => !_busy;
    public bool CanLabel => CanEdit && !_preview && _project?.Images.Count > 0;
    public bool CanTrain => CanEdit && !_checkingDevices && _project != null && _runtimeAvailable && SelectedDevice?.Available == true;
    public bool HasProject => _project != null;
    public bool HasResult => Version != null;
    public bool IsTraining => _job is { IsTerminal: false };
    public bool IsPreview => _preview;
    public double Progress => _job?.Progress?.Percent ?? 0;
    public bool IsTrainingProgressIndeterminate => _job is { IsTerminal: false } && (_job.Progress?.IsIndeterminate ?? true);
    public string TrainingProgressText => IsTrainingProgressIndeterminate ? "" : $"{Progress:F0}%";
    public string TrainingActivity => _job?.Progress is { Stage: TrainingJobStatus.Training, IsIndeterminate: false, TotalEpochs: > 0 } progress
        ? $"Entrenando · época {progress.CurrentEpoch ?? 0} de {progress.TotalEpochs}."
        : _job?.Progress?.Message ?? (_job is { IsTerminal: false } ? "Iniciando entrenamiento." : "");
    public string? WorkerLogPath => _job?.Progress?.LogPath ?? _job?.Error?.LogPath;
    public bool HasWorkerLog => !string.IsNullOrWhiteSpace(WorkerLogPath) && File.Exists(WorkerLogPath);
    public string TechnicalDetails
    {
        get
        {
            if (_job == null) return Details;
            var progress = _job.Progress;
            var lines = new List<string>
            {
                $"Etapa: {HumanState(_job.Status)}",
                $"Proceso: {progress?.ProcessState ?? (_job.IsTerminal ? _job.Status.ToString().ToLowerInvariant() : "iniciando")}",
                $"Última actividad: {(progress == null ? "Pendiente" : progress.ActivityAtUtc.ToLocalTime().ToString("G"))}",
                $"Dispositivo: {progress?.Device ?? _job.Configuration.Device}"
            };
            if (progress?.TotalEpochs is > 0) lines.Add($"Época: {progress.CurrentEpoch ?? 0} de {progress.TotalEpochs}");
            if (progress?.RecentMessages.Count > 0)
            {
                lines.Add("Mensajes recientes:");
                lines.AddRange(progress.RecentMessages.Select(message => "• " + message));
            }
            if (_job.Error != null) lines.Add($"Error: {_job.Error.TechnicalDetails ?? _job.Error.FriendlyMessage}");
            if (!string.IsNullOrWhiteSpace(WorkerLogPath)) lines.Add("Registro completo: " + WorkerLogPath);
            return string.Join(Environment.NewLine, lines);
        }
    }
    public string Stage => ProjectState(_project, _job);
    public string Summary => _project == null ? "Cree un modelo para comenzar." : $"{_project.Images.Count} imágenes · {_project.Images.Count(i => i.Reviewed)} revisadas · {_project.Classes.Count} objetos";
    public string ImageStatus => _preview ? "Prueba del modelo seleccionado" : _project?.Images.Count > 0
        ? $"Imagen {_index + 1} de {_project.Images.Count} · {(_dirty ? "Cambios pendientes" : _project.Images[_index].Reviewed ? "Revisada" : "Sin revisar")}" : "Agregue imágenes para marcar los objetos.";
    public string ResourceWarning => _host.Metrics?.Cameras().Any(c => c.Online) == true
        ? SelectedDevice?.Device.Kind == TrainingDeviceKind.Cpu
            ? "El entrenamiento aumentará la carga del procesador y puede reducir temporalmente el rendimiento de las cámaras."
            : "El entrenamiento puede reducir temporalmente el rendimiento de las cámaras." : "";
    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand DeepCheckCommand { get; }
    public IAsyncRelayCommand CheckDevicesCommand { get; }
    public IAsyncRelayCommand NewCommand { get; }
    public IAsyncRelayCommand SaveProjectCommand { get; }
    public IAsyncRelayCommand ImportImagesCommand { get; }
    public IAsyncRelayCommand ImportFolderCommand { get; }
    public IAsyncRelayCommand ImportVideoCommand { get; }
    public IAsyncRelayCommand PreviousCommand { get; }
    public IAsyncRelayCommand NextCommand { get; }
    public IAsyncRelayCommand ReviewCommand { get; }
    public IRelayCommand DeleteBoxCommand { get; }
    public IRelayCommand ChangeClassCommand { get; }
    public IAsyncRelayCommand PrepareCommand { get; }
    public IAsyncRelayCommand TrainCommand { get; }
    public IAsyncRelayCommand CancelCommand { get; }
    public IAsyncRelayCommand TestCommand { get; }
    public IRelayCommand UseCommand { get; }
    public IRelayCommand OpenTrainingLogCommand { get; }
    public IAsyncRelayCommand ReturnToEditorCommand { get; }

    private AsyncRelayCommand Command(Func<Task> action) => new(() => _operation = RunAsync(action));
    private async Task RunAsync(Func<Task> action)
    {
        if (_busy || _closing.IsCancellationRequested) return;
        _busy = true; Notify();
        try { if (_poll != null) await _poll; await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError(ex); }
        finally { _busy = false; Notify(); }
    }
    private void ShowError(Exception ex)
    {
        Message = ex is TrainingWorkerException worker ? worker.Message : ex.Message;
        Details = ex is TrainingWorkerException ? ex.Message : ex.ToString();
    }
    public async Task InitializeAsync()
    {
        _core ??= await _host.GetTrainingAsync();
        await SaveDraftAsync(); await RefreshAsync(); _timer.Start();
        BeginRuntimeCheck();
    }
    public Task RuntimeCheckTask => _runtimeCheck ?? Task.CompletedTask;
    public Task DeviceCheckTask => _deviceCheck ?? Task.CompletedTask;
    private void BeginRuntimeCheck(bool deep = false)
    {
        if (_runtimeCheck is { IsCompleted: false } || _deviceCheck is { IsCompleted: false }
            || !deep && _runtimeState == TrainingRuntimeState.Ready) return;
        _runtimeState = TrainingRuntimeState.Checking;
        Runtime = deep ? "Ejecutando verificación profunda del entorno..." : "Comprobando el entorno instalado...";
        PreparationPercent = 0;
        Notify();
        var progress = new Progress<TrainingRuntimeProgress>(p =>
        {
            Runtime = p.Message;
            PreparationPercent = p.Percent;
            Notify();
        });
        _runtimeCheck = CheckRuntimeSafeAsync(progress, deep);
    }
    private async Task CheckRuntimeSafeAsync(IProgress<TrainingRuntimeProgress> progress, bool deep)
    {
        try
        {
            var result = deep
                ? await Task.Run(() => _provisioner.DeepCheckAsync(_closing.Token, progress))
                : await Task.Run(() => _provisioner.CheckAsync(_closing.Token, progress));
            ApplyRuntime(result);
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested) { }
        catch (Exception ex)
        {
            ApplyRuntime(new(TrainingRuntimeState.Error, [], "No se pudo validar el entorno. Puede seguir trabajando con sus proyectos.", ex.Message));
        }
    }
    private void ApplyRuntime(TrainingRuntimeStatus state)
    {
        var chosen = SelectedDevice?.Device;
        _runtimeState = state.State;
        _runtimeRequiresPreparation = state.RequiresPreparation;
        _runtimeAvailable = state.State == TrainingRuntimeState.Ready;
        _canResumePreparation = state.CanResume;
        _deviceProbeFailed = state.DeviceProbeFailed;
        _deviceAvailabilityConfirmed = state.DeviceAvailabilityConfirmed;
        Runtime = state.Message; Details = state.Detail ?? "";
        Devices.Clear(); foreach (var device in state.Devices) Devices.Add(device);
        // Never select a different resource on behalf of the user, including on reprobe.
        SelectedDevice = Devices.FirstOrDefault(d => d.Available && chosen != null
            && d.Device.Kind == chosen.Kind && d.Device.DeviceId == chosen.DeviceId);
        Notify();
        if (state.RequiresDeviceProbe) BeginDeviceCheck();
    }
    private void BeginDeviceCheck()
    {
        if (!_runtimeAvailable || _deviceCheck is { IsCompleted: false }) return;
        _checkingDevices = true;
        _deviceProbeFailed = false;
        _deviceAvailabilityConfirmed = false;
        Runtime = "Comprobando dispositivos disponibles...";
        Notify();
        var progress = new Progress<TrainingRuntimeProgress>(p => { Runtime = p.Message; Notify(); });
        _deviceCheck = CheckDevicesSafeAsync(progress);
    }
    private async Task CheckDevicesSafeAsync(IProgress<TrainingRuntimeProgress> progress)
    {
        try { ApplyRuntime(await Task.Run(() => _provisioner.ProbeDevicesAsync(_closing.Token, progress))); }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested) { }
        catch (Exception ex)
        {
            var fallback = Devices.Select(device => device.Device.Kind == TrainingDeviceKind.Gpu
                ? device with { Available = false, UnavailableReason = "No se pudo confirmar la GPU en esta sesión." }
                : device).ToArray();
            ApplyRuntime(new(TrainingRuntimeState.Ready, fallback,
                "Error al comprobar GPU. Puede reintentar; CPU continúa disponible.", ex.Message,
                CapabilitiesAreLastKnown: true, DeviceProbeFailed: true));
        }
        finally { _checkingDevices = false; Notify(); }
    }
    private async Task PrepareRuntimeAsync()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        _preparation = cancellation; PreparationPercent = 0; Notify();
        var progress = new Progress<TrainingRuntimeProgress>(p =>
        {
            Runtime = p.Message; PreparationPercent = p.Percent;
            if (p.TotalBytes.HasValue) PreparationSize = p.DownloadedBytes.HasValue
                ? $"Descarga: {p.DownloadedBytes.Value / (1024.0 * 1024):F0} de {p.TotalBytes.Value / (1024.0 * 1024):F0} MB"
                : $"Descarga: {p.TotalBytes.Value / (1024.0 * 1024):F0} MB";
        });
        try { ApplyRuntime(await Task.Run(() => _provisioner.PrepareAsync(progress, cancellation.Token))); }
        catch (OperationCanceledException) { Runtime = "Preparación pausada. Puede continuarla después."; _canResumePreparation = true; }
        catch (IOException ex) { Runtime = "No se pudo preparar el entorno. Espere a que termine el entrenamiento activo y reintente."; Details = ex.Message; }
        finally { _preparation = null; Notify(); }
    }
    private async Task RefreshAsync()
    {
        if (_core == null) return;
        var projects = await _core.ListProjectsAsync(_closing.Token);
        IReadOnlyList<TrainingJob> jobs = [];
        try { jobs = await _core.ListJobsAsync(_closing.Token); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            Message = "Los proyectos se cargaron, pero no se pudo leer parte del historial de entrenamiento.";
            Details = ex.Message;
        }
        _loading = true;
        try
        {
            Projects.Clear();
            foreach (var p in projects.OrderByDescending(p => p.UpdatedAtUtc ?? p.CreatedAtUtc))
            {
                var job = jobs.Where(j => j.ProjectId == p.Id).OrderByDescending(j => j.CreatedAtUtc).FirstOrDefault();
                var model = _host.ModelRegistry?.ListModels().Where(m => m.CustomTraining?.TrainingProjectId == p.Id).OrderByDescending(m => m.CustomTraining!.CreatedAtUtc).FirstOrDefault();
                Projects.Add(new(p.Id, p.Name, string.Join(", ", p.Classes.Select(c => c.Name)), ProjectState(p, job),
                    (p.UpdatedAtUtc ?? p.CreatedAtUtc).ToLocalTime().ToString("g"), model == null ? "Sin versión" : "Versión " + model.Version));
            }
            if (_project != null)
            {
                _job = jobs.Where(j => j.ProjectId == _project.Id).OrderByDescending(j => j.CreatedAtUtc).FirstOrDefault();
                var selectedId = Version?.Id;
                Versions.Clear();
                foreach (var model in (_host.ModelRegistry?.ListModels() ?? []).Where(m => m.CustomTraining?.TrainingProjectId == _project.Id).OrderByDescending(m => m.CustomTraining!.CreatedAtUtc))
                    Versions.Add(new(model.Id, $"{model.DisplayName} · Versión {model.Version}"));
                Version = Versions.FirstOrDefault(v => v.Id == selectedId) ?? Versions.FirstOrDefault();
                History.Clear();
                foreach (var job in jobs.Where(j => j.ProjectId == _project.Id).OrderByDescending(j => j.CreatedAtUtc))
                    History.Add($"{job.CreatedAtUtc.ToLocalTime():g} · {HumanState(job.Status)}");
            }
        }
        finally { _loading = false; Notify(); }
    }
    private async Task OpenAsync(Guid id)
    {
        await SaveDraftAsync(); _project = await _core!.GetProjectAsync(id, _closing.Token); _preview = false;
        Name = _project.Name; ClassNames = string.Join(Environment.NewLine, _project.Classes.Select(c => c.Name));
        _index = 0; Version = null; await RefreshAsync(); await LoadImageAsync();
        Message = _job?.Status == TrainingJobStatus.Completed ? "Modelo listo." : "";
    }
    private async Task SaveProjectAsync()
    {
        _core ??= await _host.GetTrainingAsync();
        var names = ClassNames.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (_project == null) _project = await _core.CreateProjectAsync(Name, "", names, _closing.Token);
        else
        {
            await SaveDraftAsync();
            var classes = TrainingClassEdits.Apply(_project.Classes, names);
            _project = await _core.UpdateProjectAsync(_project.Id, Name, _project.Description, classes, _closing.Token);
        }
        await RefreshAsync(); await LoadImageAsync(); Message = "Modelo guardado. Agregue ejemplos y marque los objetos.";
    }
    private async Task ImportImagesAsync()
    {
        if (_project == null) return;
        var dialog = new OpenFileDialog { Filter = "Imágenes|*.jpg;*.jpeg;*.png", Multiselect = true };
        if (dialog.ShowDialog() != true) return;
        await SaveDraftAsync(); var errors = new List<string>();
        foreach (var file in dialog.FileNames)
            try { _project = await Task.Run(() => _core!.ImportAsync(_project.Id, file, ct: _closing.Token)); }
            catch (Exception ex) when (ex is not OperationCanceledException) { errors.Add(Path.GetFileName(file) + ": " + ex.Message); }
        await LoadImageAsync(); Message = errors.Count == 0 ? "Imágenes agregadas." : "Algunas imágenes no se pudieron agregar."; Details = string.Join(Environment.NewLine, errors);
    }
    private async Task ImportFolderAsync()
    {
        if (_project == null) return;
        var dialog = new OpenFolderDialog(); if (dialog.ShowDialog() != true) return;
        await SaveDraftAsync();
        var result = await Task.Run(() => TrainingAssetService.ImportFolderAsync(_core!, _project.Id, dialog.FolderName, _closing.Token));
        _project = result.Project; await LoadImageAsync();
        Message = result.Failures.Count == 0 ? "Carpeta importada." : "Algunas imágenes no se pudieron agregar.";
        Details = string.Join(Environment.NewLine, result.Failures.Select(f => f.FileName + ": " + f.Message));
    }
    private async Task ImportVideoAsync()
    {
        if (_project == null) return;
        var dialog = new OpenFileDialog { Filter = "Videos locales|*.mp4;*.avi;*.mov;*.mkv" }; if (dialog.ShowDialog() != true) return;
        await SaveDraftAsync();
        await Task.Run(() => TrainingDesktopServices.ImportVideoAsync(_core!, _project.Id, dialog.FileName, _closing.Token));
        _project = await _core!.GetProjectAsync(_project.Id); await LoadImageAsync();
        Message = "Video agregado (hasta 30 ejemplos). Agregue otro origen independiente antes de entrenar.";
    }
    private async Task LoadImageAsync()
    {
        _preview = false;
        Image = null;
        _loading = true;
        try
        {
            Boxes.Clear(); Classes.Clear(); SelectedBox = -1;
            if (_project == null) { Image = null; return; }
            foreach (var cls in _project.Classes) Classes.Add(cls);
            ActiveClass = Classes.FirstOrDefault();
            if (_project.Images.Count == 0) { Image = null; return; }
            _index = Math.Clamp(_index, 0, _project.Images.Count - 1);
            var asset = _project.Images[_index];
            Image = await ReadBitmapAsync(TrainingPaths.Within(_paths.Project(_project.Id), asset.StoredPath));
            foreach (var box in asset.Annotations) Boxes.Add(box);
        }
        finally { _dirty = false; _loading = false; Notify(); }
    }
    private Task SaveDraftAsync() => _dirty && !_preview ? SaveImageAsync(false) : Task.CompletedTask;
    private async Task SaveImageAsync(bool reviewed)
    {
        if (_project?.Images.Count > 0 && !_preview)
        {
            _project = await _core!.SetAnnotationsAsync(_project.Id, _project.Images[_index].Id, Boxes.ToArray(), reviewed, _closing.Token);
            _dirty = false; Notify();
        }
    }
    private async Task ChangeImageAsync(int delta) { await SaveDraftAsync(); _preview = false; _index += delta; await LoadImageAsync(); }
    private async Task PrepareAsync()
    {
        await SaveDraftAsync(); if (_project == null) return;
        if (Name.Trim() != _project.Name || !ClassNames.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).SequenceEqual(_project.Classes.Select(c => c.Name)))
            throw new InvalidOperationException("Guarde el nombre y los objetos del modelo antes de entrenar.");
        try
        {
            var split = new DatasetSplitService().Split(_project);
            foreach (var asset in _project.Images)
            {
                var path = TrainingPaths.Within(_paths.Project(_project.Id), asset.StoredPath);
                if (!File.Exists(path)) throw new IOException("Falta una imagen del proyecto. Vuelva a agregar los ejemplos.");
            }
            Message = "Ejemplos preparados. " + string.Join(" ", split.Warnings);
        }
        catch (ArgumentException ex) { throw new ArgumentException("Agrega más ejemplos o revisa las imágenes antes de entrenar. " + ex.Message); }
    }
    private async Task TrainAsync()
    {
        await PrepareAsync(); if (_project == null) return;
        var device = SelectedDevice?.Device ?? throw new ArgumentException("Seleccione CPU o GPU para este entrenamiento.");
        // Core rechecks the device before saving the job; the worker rechecks again at execution.
        var projectId = _project.Id; var profile = (TrainingProfile)ProfileIndex;
        _job = await Task.Run(() => _core!.StartAsync(projectId, profile, TrainingProfiles.BaseModel,
            ct: _closing.Token, device: device));
        Message = "Entrenamiento en curso. El resultado se publicará al superar la validación; ninguna cámara se activará.";
        _timer.Start(); Notify();
    }
    private async Task PollSafeAsync()
    {
        _polling = true;
        try
        {
            if (_job is { IsTerminal: false })
            {
                _job = await _core!.GetJobAsync(_job.Id, _closing.Token);
                if (_job.Progress?.Device != null) Runtime = _job.Progress.Device == "cpu" ? "Entrenamiento por CPU" : "GPU disponible";
                if (_job.IsTerminal)
                {
                    _project = await _core.GetProjectAsync(_job.ProjectId, _closing.Token);
                    if (_job.Status == TrainingJobStatus.Completed) Version = null;
                    await RefreshAsync();
                    Message = _job.Status == TrainingJobStatus.Completed ? "Modelo listo." : _job.Status == TrainingJobStatus.Cancelled ? "Entrenamiento cancelado."
                        : _job.Error?.ErrorCode == "training_gpu_unavailable" ? "La GPU seleccionada ya no está disponible. Puede elegir CPU e iniciar un nuevo entrenamiento."
                        : _job.Error?.ErrorCode == "training_runtime_missing" ? "Prepare el entorno de entrenamiento." : "No se pudo completar el entrenamiento con el recurso elegido.";
                    Details = _job.Error?.TechnicalDetails ?? _job.Error?.LogPath ?? "";
                }
                Notify();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError(ex); }
        finally { _polling = false; }
    }
    private async Task TestAsync()
    {
        if (Version == null || _host.ModelRegistry == null) return;
        var dialog = new OpenFileDialog { Filter = "Imágenes|*.jpg;*.jpeg;*.png" }; if (dialog.ShowDialog() != true) return;
        await SaveDraftAsync();
        if (new FileInfo(dialog.FileName).Length > 40 * 1024 * 1024) throw new InvalidDataException("La imagen supera el tamaño permitido.");
        var bytes = await File.ReadAllBytesAsync(dialog.FileName, _closing.Token);
        var frame = await Task.Run(() => TrainingDesktopServices.Decode(bytes));
        IReadOnlyList<CustomModelDetection> detections;
        try { detections = await new CustomModelTestService(_host.ModelRegistry).TestAsync(Version.Id, frame, _closing.Token); }
        catch (Exception ex) when (ex is not OperationCanceledException) { throw new InvalidOperationException("VisionControl no pudo cargar este modelo.", ex); }
        Classes.Clear();
        foreach (var cls in _host.ModelRegistry.GetModel(Version.Id)!.CustomTraining!.Classes) Classes.Add(cls);
        _preview = true; Image = await ReadBitmapAsync(dialog.FileName); _loading = true;
        try { Boxes.Clear(); foreach (var d in detections) Boxes.Add(new(d.ClassId, (d.Box.Left + d.Box.Right) / (2.0 * frame.Width), (d.Box.Top + d.Box.Bottom) / (2.0 * frame.Height), d.Box.Width / frame.Width, d.Box.Height / frame.Height)); }
        finally { _loading = false; }
        Message = detections.Count == 0 ? "No se detectaron objetos en esta imagen." : string.Join(" · ", detections.Select(d => $"{d.ClassName}: {d.Confidence:P0}")); Notify();
    }
    private void OpenTrainingLog()
    {
        if (HasWorkerLog) Process.Start(new ProcessStartInfo(WorkerLogPath!) { UseShellExecute = true });
    }
    private static Task<BitmapSource> ReadBitmapAsync(string path) => Task.Run<BitmapSource>(() =>
    {
        if (new FileInfo(path).Length > 40 * 1024 * 1024) throw new InvalidDataException("La imagen supera el tamaño permitido.");
        // Same decoder/orientation and pixel aspect ratio as asset import and inference.
        var frame = TrainingDesktopServices.Decode(File.ReadAllBytes(path));
        var bitmap = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgr24, null,
            frame.BgrPixels.ToArray(), frame.Width * 3);
        bitmap.Freeze(); return bitmap;
    });
    private static string MetricsText(IReadOnlyDictionary<string, double>? metrics) => string.Join(" · ", (metrics ?? new Dictionary<string, double>()).Select(m =>
        (m.Key switch { "map50" => "Calidad de detección", "map50_95" => "Calidad estricta", "precision" => "Precisión", "recall" => "Objetos encontrados", _ => m.Key }) + $": {m.Value:P1}"));
    public static string HumanState(TrainingJobStatus status) => status switch
    { TrainingJobStatus.Queued or TrainingJobStatus.PreparingDataset => "Preparando", TrainingJobStatus.Training => "Entrenando", TrainingJobStatus.Validating => "Validando", TrainingJobStatus.Exporting or TrainingJobStatus.Registering => "Publicando", TrainingJobStatus.Completed => "Listo", TrainingJobStatus.Cancelled => "Cancelado", _ => "Error" };
    private static string ProjectState(TrainingProject? project, TrainingJob? job) => job == null || job.IsTerminal && project?.Revision > job.Snapshot.Revision ? "Borrador" : HumanState(job.Status);
    private void Notify()
    {
        foreach (var name in new[] { nameof(IsPreparingRuntime), nameof(IsCheckingRuntime), nameof(IsCheckingDevices), nameof(DeviceProbeFailed), nameof(NeedsPreparation), nameof(PreparationActionText), nameof(GpuUnavailable), nameof(IsBusy), nameof(CanSelectProject), nameof(CanEdit), nameof(CanLabel), nameof(CanTrain), nameof(HasProject), nameof(HasResult), nameof(IsTraining), nameof(IsPreview), nameof(Progress), nameof(IsTrainingProgressIndeterminate), nameof(TrainingProgressText), nameof(TrainingActivity), nameof(TechnicalDetails), nameof(WorkerLogPath), nameof(HasWorkerLog), nameof(Stage), nameof(Summary), nameof(ImageStatus), nameof(ResourceWarning) }) OnPropertyChanged(name);
    }
    public async ValueTask DisposeAsync()
    {
        _timer.Stop(); _closing.Cancel();
        if (_operation != null) await _operation;
        if (_poll != null) await _poll;
        if (_runtimeCheck != null) try { await _runtimeCheck; } catch (OperationCanceledException) { }
        if (_deviceCheck != null) try { await _deviceCheck; } catch (OperationCanceledException) { }
        if (_dirty && !_preview && _project?.Images.Count > 0 && _core != null)
            await _core.SetAnnotationsAsync(_project.Id, _project.Images[_index].Id, Boxes.ToArray(), false);
        _closing.Dispose();
    }
}
