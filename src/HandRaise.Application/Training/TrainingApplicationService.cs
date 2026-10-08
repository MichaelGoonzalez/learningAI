using HandRaise.Application.Inference;
using HandRaise.Domain.Training;

namespace HandRaise.Application.Training;

/// <summary>One application-scoped instance owns edits and jobs. No UI, process or camera dependency.</summary>
public sealed class TrainingApplicationService : IAsyncDisposable
{
    private readonly ITrainingStore _store;
    private readonly ITrainingAssets _assets;
    private readonly ITrainingDatasetExporter _exporter;
    private readonly ITrainingWorker _worker;
    private readonly ITrainingModelPublisher _publisher;
    private readonly ITrainingResourceMonitor _resources;
    private readonly IDisposable? _ownership;
    private readonly SemaphoreSlim _edits = new(1, 1);
    private readonly SemaphoreSlim _queue = new(1, 1);
    private readonly Dictionary<Guid, (CancellationTokenSource Cancellation, Task Task)> _running = [];
    private bool _disposed;

    public TrainingApplicationService(ITrainingStore store, ITrainingAssets assets, ITrainingDatasetExporter exporter,
        ITrainingWorker worker, ITrainingModelPublisher publisher, ITrainingResourceMonitor resources, IDisposable? ownership = null)
        => (_store, _assets, _exporter, _worker, _publisher, _resources, _ownership) = (store, assets, exporter, worker, publisher, resources, ownership);

    public Task<IReadOnlyList<TrainingProject>> ListProjectsAsync(CancellationToken ct = default) => _store.ListProjectsAsync(ct);
    public Task<TrainingProject> GetProjectAsync(Guid id, CancellationToken ct = default) => _store.LoadProjectAsync(id, ct);
    public Task<TrainingJob> GetJobAsync(Guid id, CancellationToken ct = default) => _store.LoadJobAsync(id, ct);
    public Task<IReadOnlyList<TrainingJob>> ListJobsAsync(CancellationToken ct = default) => _store.ListJobsAsync(ct);

    public async Task<TrainingProject> CreateProjectAsync(string name, string description, IEnumerable<string> classes, CancellationToken ct = default)
    {
        var project = new TrainingProject(Guid.NewGuid(), name.Trim(), description,
            classes.Select(n => new TrainingClass(Guid.NewGuid(), n.Trim())).ToArray(), [], TrainingProjectStatus.Draft, DateTimeOffset.UtcNow);
        project.Validate();
        await _edits.WaitAsync(ct);
        try { ThrowIfDisposed(); await _store.SaveProjectAsync(project, ct); }
        finally { _edits.Release(); }
        return project;
    }

    public Task<TrainingProject> ImportAsync(Guid id, string path, TrainingSourceType source = TrainingSourceType.Image,
        string? sourceId = null, double? sourceTimeSeconds = null, CancellationToken ct = default) => EditAsync(id, async p =>
    {
        var image = await _assets.ImportAsync(p, path, source, sourceId, sourceTimeSeconds, ct);
        return p.Images.Any(i => i.Id == image.Id) ? p : p with { Images = p.Images.Append(image).ToArray() };
    }, ct);

    public Task<TrainingProject> ImportFrameAsync(Guid id, ImageFrame frame, TrainingSourceType source,
        string sourceId, double? sourceTimeSeconds = null, CancellationToken ct = default) => EditAsync(id, async p =>
    {
        var image = await _assets.ImportFrameAsync(p, frame, source, sourceId, sourceTimeSeconds, ct);
        return p.Images.Any(i => i.Id == image.Id) ? p : p with { Images = p.Images.Append(image).ToArray() };
    }, ct);

    public Task<TrainingProject> SetAnnotationsAsync(Guid id, Guid imageId, IReadOnlyList<BoundingBoxAnnotation> boxes,
        CancellationToken ct = default) => SetAnnotationsAsync(id, imageId, boxes, true, ct);

    public Task<TrainingProject> SetAnnotationsAsync(Guid id, Guid imageId, IReadOnlyList<BoundingBoxAnnotation> boxes,
        bool reviewed, CancellationToken ct = default) => EditAsync(id, p =>
    {
        if (!p.Images.Any(i => i.Id == imageId)) throw new ArgumentException("No se encontró la imagen.");
        foreach (var box in boxes) box.Validate(p.Classes);
        return Task.FromResult(p with { Images = p.Images.Select(i => i.Id == imageId
            ? i with { Annotations = boxes.ToArray(), Reviewed = reviewed } : i).ToArray() });
    }, ct);

    public Task<TrainingProject> UpdateProjectAsync(Guid id, string name, string description,
        IReadOnlyList<TrainingClass> classes, CancellationToken ct = default) => EditAsync(id,
        p => Task.FromResult(p with { Name = name.Trim(), Description = description, Classes = classes.ToArray() }), ct);

    private async Task<TrainingProject> EditAsync(Guid id, Func<TrainingProject, Task<TrainingProject>> edit, CancellationToken ct)
    {
        await _edits.WaitAsync(ct);
        try
        {
            ThrowIfDisposed();
            await EnsureIdleAsync(id, ct);
            var project = await edit(await _store.LoadProjectAsync(id, ct));
            project = project with { Revision = project.Revision + 1, Status = TrainingProjectStatus.Draft, UpdatedAtUtc = DateTimeOffset.UtcNow };
            project.Validate();
            await _store.SaveProjectAsync(project, ct);
            return project;
        }
        finally { _edits.Release(); }
    }

    public async Task DeleteProjectAsync(Guid id, CancellationToken ct = default)
    {
        await _edits.WaitAsync(ct);
        try { ThrowIfDisposed(); await EnsureIdleAsync(id, ct); await _store.DeleteProjectAsync(id, ct); }
        finally { _edits.Release(); }
    }

    private async Task EnsureIdleAsync(Guid projectId, CancellationToken ct)
    {
        if ((await _store.ListJobsAsync(ct)).Any(j => j.ProjectId == projectId && !j.IsTerminal))
            throw new InvalidOperationException("Espere o cancele el entrenamiento antes de modificar el proyecto.");
    }

    public async Task<TrainingJob> StartAsync(Guid projectId, TrainingProfile profile, string baseModel,
        TrainingResourcePolicy policy = TrainingResourcePolicy.PreferSystemAvailability, CancellationToken ct = default,
        TrainingDevice? device = null)
    {
        await _edits.WaitAsync(ct);
        try
        {
            ThrowIfDisposed();
            await EnsureIdleAsync(projectId, ct);
            foreach (var id in _running.Where(p => p.Value.Task.IsCompletedSuccessfully).Select(p => p.Key).ToArray())
            { _running[id].Cancellation.Dispose(); _running.Remove(id); }
            var project = await _store.LoadProjectAsync(projectId, ct);
            project.Validate();
            // Production workers require an explicit choice; legacy/fake workers retain CPU compatibility.
            if (device == null && _worker is ITrainingDeviceValidator)
                throw new ArgumentException("Seleccione CPU o GPU para este entrenamiento.");
            device ??= new TrainingDevice(TrainingDeviceKind.Cpu);
            var configuration = TrainingProfiles.Resolve(profile, policy) with { Device = device.WorkerDevice };
            var runtimeVersion = _worker is ITrainingDeviceValidator validator
                ? await validator.ValidateDeviceAsync(device, ct) : null;
            new DatasetSplitService().Split(project, configuration.Seed);
            var job = new TrainingJob(Guid.NewGuid(), projectId, TrainingJobStatus.Queued, profile, policy,
                DateTimeOffset.UtcNow, configuration, baseModel, project,
                Warnings: await _resources.AssessAsync(policy, ct)) { SelectedDevice = device,
                    RuntimeVersion = runtimeVersion?.Version, RuntimeManifestSha256 = runtimeVersion?.ManifestSha256 };
            await _store.SaveJobAsync(job, ct);
            var cancellation = new CancellationTokenSource(); // The caller's request token does not own the background job.
            var task = Task.Run(() => ExecuteAsync(job, cancellation.Token));
            _running.Add(job.Id, (cancellation, task));
            return job;
        }
        finally { _edits.Release(); }
    }

    public async Task WaitAsync(Guid id, CancellationToken ct = default)
    {
        Task? task;
        await _edits.WaitAsync(ct);
        try { task = _running.TryGetValue(id, out var running) ? running.Task : null; }
        finally { _edits.Release(); }
        if (task != null) await task.WaitAsync(ct);
    }

    public async Task CancelAsync(Guid id, CancellationToken ct = default)
    {
        Task? task = null;
        await _edits.WaitAsync(ct);
        try
        {
            if (_running.TryGetValue(id, out var running)) { running.Cancellation.Cancel(); task = running.Task; }
        }
        finally { _edits.Release(); }
        if (task != null) await task.WaitAsync(ct);
    }

    private async Task ExecuteAsync(TrainingJob initial, CancellationToken ct)
    {
        var job = initial;
        var acquired = false;
        TrainingProgress Activity(TrainingJobStatus status, string message, bool indeterminate, string processState = "running")
        {
            var recent = (job.Progress?.RecentMessages ?? []).Append(message).TakeLast(8).ToArray();
            return new(status, indeterminate ? 0 : job.Progress?.Percent ?? 0,
                job.Progress?.CurrentEpoch, job.Progress?.TotalEpochs, message, job.Progress?.Metrics,
                job.Progress?.Device ?? job.Configuration.Device, job.Progress?.WorkerVersion)
            {
                IsIndeterminate = indeterminate,
                ActivityAtUtc = DateTimeOffset.UtcNow,
                ProcessState = processState,
                LogPath = job.Progress?.LogPath,
                RecentMessages = recent
            };
        }
        async Task Save(TrainingJobStatus status, string? message = null, bool indeterminate = false,
            string processState = "running")
        {
            job = job.Transition(status);
            if (message != null) job = job with { Progress = Activity(status, message, indeterminate, processState) };
            await _store.SaveJobAsync(job, CancellationToken.None);
        }
        try
        {
            await _queue.WaitAsync(ct);
            acquired = true;
            await Save(TrainingJobStatus.PreparingDataset, "Preparando datos.", true);
            await _store.SaveProjectAsync(job.Snapshot with { Status = TrainingProjectStatus.Preparing }, ct);
            job = job with { Dataset = await _exporter.ExportAsync(job, ct) };
            await Save(TrainingJobStatus.Training, "Iniciando entrenamiento.", true);
            await _store.SaveProjectAsync(job.Snapshot with { Status = TrainingProjectStatus.Training }, ct);
            var result = await _worker.RunAsync(job, async progress =>
            {
                if (!double.IsFinite(progress.Percent) || progress.Percent is < 0 or > 100)
                    throw new InvalidDataException("El worker envió un porcentaje inválido.");
                if (progress.Stage != job.Status) job = job.Transition(progress.Stage);
                var text = string.IsNullOrWhiteSpace(progress.Message) ? HumanProgress(progress.Stage) : progress.Message!;
                var recent = (job.Progress?.RecentMessages ?? []).Append(text).TakeLast(8).ToArray();
                job = job with { Progress = progress with
                {
                    Device = progress.Device ?? job.Progress?.Device ?? job.Configuration.Device,
                    WorkerVersion = progress.WorkerVersion ?? job.Progress?.WorkerVersion,
                    LogPath = progress.LogPath ?? job.Progress?.LogPath,
                    ActivityAtUtc = DateTimeOffset.UtcNow,
                    ProcessState = "running",
                    RecentMessages = recent
                } };
                if (progress.Device != null) job = job with { Configuration = job.Configuration with { Device = progress.Device } };
                await _store.SaveJobAsync(job, ct);
            }, ct);
            ct.ThrowIfCancellationRequested();
            job = job with { Result = result };
            await Save(TrainingJobStatus.Registering, "Publicando modelo.", true);
            // Cancellation before this point prevents publication. The publisher commits atomically;
            // cancellation after publication must not label a successfully published model Cancelled.
            var model = await _publisher.PublishAsync(job, ct);
            job = job with { ModelId = model.Id, Progress = Activity(TrainingJobStatus.Completed, "Entrenamiento completado.", false, "completed") with { Percent = 100 } };
            await Save(TrainingJobStatus.Completed);
            await _store.SaveProjectAsync(job.Snapshot with { Status = TrainingProjectStatus.Completed });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested && !job.IsTerminal)
        {
            await Save(TrainingJobStatus.Cancelled, "Entrenamiento cancelado.", false, "cancelled");
            await _store.SaveProjectAsync(job.Snapshot with { Status = TrainingProjectStatus.Ready });
        }
        catch (Exception ex) when (!job.IsTerminal)
        {
            job = job with { Error = new(ex is TrainingWorkerException worker ? worker.Code : "training_failed",
                "No se pudo completar el entrenamiento. Revise los ejemplos y el entorno del worker.",
                ex.Message, (ex as TrainingWorkerException)?.LogPath) };
            if (job.Progress?.LogPath == null && ex is TrainingWorkerException failure)
                job = job with { Progress = job.Progress is null ? null : job.Progress with { LogPath = failure.LogPath } };
            await Save(TrainingJobStatus.Failed, "El entrenamiento falló.", false, "failed");
            await _store.SaveProjectAsync(job.Snapshot with { Status = TrainingProjectStatus.Failed });
        }
        finally { if (acquired) _queue.Release(); }
    }

    private static string HumanProgress(TrainingJobStatus status) => status switch
    {
        TrainingJobStatus.PreparingDataset => "Preparando datos.",
        TrainingJobStatus.Training => "Entrenando el modelo.",
        TrainingJobStatus.Validating => "Validando resultados.",
        TrainingJobStatus.Exporting => "Exportando modelo.",
        TrainingJobStatus.Registering => "Publicando modelo.",
        _ => status.ToString()
    };

    /// <summary>Call once at composition startup. Never resumes training or starts a worker.</summary>
    public async Task RecoverInterruptedJobsAsync(CancellationToken ct = default)
    {
        await _edits.WaitAsync(ct);
        try
        {
            if (_running.Count > 0) throw new InvalidOperationException("Recupere los jobs antes de iniciar entrenamientos.");
            foreach (var job in (await _store.ListJobsAsync(ct)).Where(j => !j.IsTerminal))
            {
                var published = _publisher.FindPublishedModel(job.Id);
                if (published != null && job.Status == TrainingJobStatus.Registering)
                {
                    await _store.SaveJobAsync(job.Transition(TrainingJobStatus.Completed) with
                    { ModelId = published.Id, Progress = new(TrainingJobStatus.Completed, 100) }, ct);
                    await _store.SaveProjectAsync(job.Snapshot with { Status = TrainingProjectStatus.Completed }, ct);
                    continue;
                }
                await _store.SaveJobAsync(job.Transition(TrainingJobStatus.Failed) with
                { Error = new("edge_interrupted", "Edge se cerró antes de terminar el entrenamiento. Puede volver a entrenar.") }, ct);
                await _store.SaveProjectAsync(job.Snapshot with { Status = TrainingProjectStatus.Failed }, ct);
            }
        }
        finally { _edits.Release(); }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
    public async ValueTask DisposeAsync()
    {
        Task[] tasks;
        await _edits.WaitAsync();
        try
        {
            _disposed = true;
            foreach (var value in _running.Values) value.Cancellation.Cancel();
            tasks = _running.Values.Select(v => v.Task).ToArray();
        }
        finally { _edits.Release(); }
        try { await Task.WhenAll(tasks); }
        finally { foreach (var value in _running.Values) value.Cancellation.Dispose(); _ownership?.Dispose(); }
    }
}
