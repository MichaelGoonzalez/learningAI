using HandRaise.Application.Capture;
using HandRaise.Application.Events;
using HandRaise.Application.Storage;
using HandRaise.Infrastructure.Windows.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using HandRaise.Application.Inference;
using OpenCvSharp;

namespace HandRaise.Infrastructure.Windows.Tests;

public sealed class StorageTests
{
    [Fact]
    public void SnapshotEncoder_CopiesOnlyCropAsJpeg()
    {
        using var frame = new VideoFrame(
            20, 20, Enumerable.Repeat((byte)127, 20 * 20 * 3).ToArray(), 0, 0, 0,
            DateTimeOffset.UtcNow);

        var jpeg = new OpenCvPersonSnapshotEncoder().Encode(
            frame,
            new BoundingBox(5, 5, 15, 15),
            new SnapshotEncodingOptions(0.1, 85));
        using var decoded = Cv2.ImDecode(jpeg, ImreadModes.Color);

        Assert.False(decoded.Empty());
        Assert.Equal(12, decoded.Width);
        Assert.Equal(12, decoded.Height);
    }

    [Fact]
    public async Task Migration_CreatesEmptyDatabaseAndIndexes()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var command = database.Connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type IN ('table','index')";
        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        Assert.Contains("events", names);
        Assert.Contains("ix_events_camera_timestamp", names);
        Assert.Contains("ix_events_type", names);
    }

    [Fact]
    public async Task Repository_FiltersOrdersAndPaginatesUsingUtc()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new SqliteHandEventRepository(database.Options);
        var start = new DateTimeOffset(DateTimeOffset.UtcNow.Year, 4, 1, 12, 0, 0, TimeSpan.Zero);
        await repository.SaveBatchAsync([
            Event("a", "cam-a", "zone-a", "hand_raised", start),
            Event("b", "cam-a", "zone-b", "hand_lowered", start.AddMinutes(1)),
            Event("c", "cam-b", "zone-a", "hand_raised", start.AddMinutes(2))
        ]);

        Assert.Equal("c", Assert.Single(await repository.QueryAsync(
            new EventQuery(CameraId: "cam-b"))).Id);
        Assert.Equal("a", Assert.Single(await repository.QueryAsync(
            new EventQuery(Zone: "zone-a", CameraId: "cam-a"))).Id);
        Assert.Equal(2, (await repository.QueryAsync(new EventQuery(Type: "hand_raised"))).Count);
        Assert.Equal("b", Assert.Single(await repository.QueryAsync(
            new EventQuery(From: start.AddSeconds(30), To: start.AddMinutes(1)))).Id);
        Assert.Equal("b", Assert.Single(await repository.QueryAsync(
            new EventQuery(Limit: 1, Offset: 1))).Id);
        var ordered = await repository.QueryAsync(new EventQuery());
        Assert.Equal(["c", "b", "a"], ordered.Select(item => item.Id));
        Assert.All(ordered, item => Assert.Equal(TimeSpan.Zero, item.Timestamp.Offset));
        Assert.Equal("a", (await repository.GetByIdAsync("a"))!.Id);
    }

    [Fact]
    public async Task Repository_AggregatesByCameraZoneAndUtcHour()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new SqliteHandEventRepository(database.Options);
        var start = new DateTimeOffset(2026, 4, 1, 12, 10, 0, TimeSpan.Zero);
        await repository.SaveBatchAsync([
            Event("a", "cam", "zone", "hand_raised", start),
            Event("b", "cam", "zone", "hand_lowered", start.AddMinutes(20)),
            Event("c", "cam", "zone", "hand_raised", start.AddHours(1))
        ]);

        var statistics = await repository.GetStatisticsAsync(new EventQuery());

        Assert.Equal(2, statistics.Count);
        Assert.Equal(2, statistics[0].Count);
        Assert.Equal(12, statistics[0].HourUtc.Hour);
        Assert.Equal(1, statistics[1].Count);
    }

    [Fact]
    public async Task PersistenceWorker_FlushesPendingEventsAndWritesRelativeSnapshot()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new SqliteHandEventRepository(database.Options);
        var directory = TemporaryDirectory();
        try
        {
            await using var bus = new HandEventBus();
            await using var worker = new EventPersistenceWorker(
                bus,
                repository,
                new SnapshotStore(directory),
                WorkerOptions(directory),
                new FixedClock(DateTimeOffset.UtcNow));
            for (var index = 0; index < 3; index++)
            {
                await bus.PublishAsync(Event($"event-{index}", "cam", null, "hand_raised",
                    DateTimeOffset.UtcNow.AddSeconds(index)) with { SnapshotJpeg = [1, 2, 3] });
            }

            await bus.DisposeAsync();
            await worker.DisposeAsync();

            var saved = await repository.QueryAsync(new EventQuery());
            Assert.Equal(3, saved.Count);
            Assert.All(saved, item =>
            {
                Assert.NotNull(item.SnapshotUrl);
                Assert.False(Path.IsPathRooted(item.SnapshotUrl));
                Assert.True(File.Exists(Path.Combine(directory, item.SnapshotUrl!.Replace('/', Path.DirectorySeparatorChar))));
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PersistenceFailure_IsLoggedAndDoesNotReachPublisher()
    {
        var logs = new List<string>();
        var directory = TemporaryDirectory();
        try
        {
            await using var bus = new HandEventBus();
            await using var worker = new EventPersistenceWorker(
                bus,
                new ThrowingRepository(),
                new SnapshotStore(directory),
                WorkerOptions(directory),
                log: logs.Add);

            await bus.PublishAsync(Event("event", "cam", null, "hand_raised", DateTimeOffset.UtcNow));
            await bus.DisposeAsync();
            await worker.DisposeAsync();

            Assert.Contains(logs, item => item.Contains("No se guardó un lote"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PersistenceQueue_WhenFull_DropsNewestWithoutBlockingPublisher()
    {
        var directory = TemporaryDirectory();
        var repository = new SlowRepository();
        try
        {
            await using var bus = new HandEventBus();
            await using var worker = new EventPersistenceWorker(
                bus,
                repository,
                new SnapshotStore(directory),
                WorkerOptions(directory) with { QueueCapacity = 1, BatchSize = 1 });
            for (var index = 0; index < 10; index++)
            {
                await bus.PublishAsync(Event(
                    $"event-{index}", "cam", null, "hand_raised", DateTimeOffset.UtcNow));
            }

            await repository.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Task.Delay(50);
            Assert.True(worker.DroppedEvents > 0);

            repository.Release.TrySetResult();
            await bus.DisposeAsync();
            await worker.DisposeAsync();
        }
        finally
        {
            repository.Release.TrySetResult();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Retention_RemovesExpiredEventsSnapshotsAndOrphans()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new SqliteHandEventRepository(database.Options);
        var directory = TemporaryDirectory();
        var now = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
        try
        {
            var store = new SnapshotStore(directory);
            var oldPath = await store.SaveAsync("old", now.AddDays(-5), [1]);
            var currentPath = await store.SaveAsync("current", now, [2]);
            var orphanPath = await store.SaveAsync("orphan", now, [3]);
            await repository.SaveBatchAsync([
                Event("old", "cam", null, "hand_raised", now.AddDays(-5)) with { SnapshotUrl = oldPath },
                Event("current", "cam", null, "hand_raised", now) with { SnapshotUrl = currentPath }
            ]);
            var options = WorkerOptions(directory) with { RetentionDays = 2 };

            await new RetentionService(repository, store, options, new FixedClock(now)).RunOnceAsync();

            Assert.Null(await repository.GetByIdAsync("old"));
            Assert.NotNull(await repository.GetByIdAsync("current"));
            Assert.False(File.Exists(Path.Combine(directory, oldPath.Replace('/', Path.DirectorySeparatorChar))));
            Assert.False(File.Exists(Path.Combine(directory, orphanPath.Replace('/', Path.DirectorySeparatorChar))));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Retention_EnforcesMaximumSnapshotSizeOldestFirst()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new SqliteHandEventRepository(database.Options);
        var directory = TemporaryDirectory();
        var now = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
        try
        {
            var store = new SnapshotStore(directory);
            var oldPath = await store.SaveAsync("old", now.AddMinutes(-1), new byte[10]);
            var newPath = await store.SaveAsync("new", now, new byte[10]);
            await repository.SaveBatchAsync([
                Event("old", "cam", null, "hand_raised", now.AddMinutes(-1)) with { SnapshotUrl = oldPath },
                Event("new", "cam", null, "hand_raised", now) with { SnapshotUrl = newPath }
            ]);
            var options = WorkerOptions(directory) with { MaximumSnapshotBytes = 10 };

            await new RetentionService(repository, store, options, new FixedClock(now)).RunOnceAsync();

            Assert.Null((await repository.GetByIdAsync("old"))!.SnapshotUrl);
            Assert.NotNull((await repository.GetByIdAsync("new"))!.SnapshotUrl);
            Assert.False(File.Exists(Path.Combine(directory, oldPath.Replace('/', Path.DirectorySeparatorChar))));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static HandEvent Event(
        string id,
        string camera,
        string? zone,
        string type,
        DateTimeOffset timestamp) => new(
            id, type, camera, 1, "left", zone, 0.9, timestamp);

    private static StorageWorkerOptions WorkerOptions(string directory) => new(
        QueueCapacity: 16,
        BatchSize: 4,
        SnapshotDirectory: directory,
        RetentionDays: null,
        MaximumSnapshotBytes: null,
        CleanupInterval: TimeSpan.FromHours(1));

    private static string TemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"handraise-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FixedClock(DateTimeOffset now) : IUtcClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class ThrowingRepository : IHandEventRepository
    {
        public Task SaveBatchAsync(IReadOnlyList<HandEvent> events, CancellationToken cancellationToken = default) =>
            throw new IOException("disk failed");
        public Task<IReadOnlyList<HandEvent>> QueryAsync(EventQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<HandEvent>>([]);
        public Task<HandEvent?> GetByIdAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult<HandEvent?>(null);
        public Task<IReadOnlyList<EventStatistic>> GetStatisticsAsync(EventQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<EventStatistic>>([]);
        public Task<IReadOnlyList<StoredSnapshot>> GetSnapshotsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StoredSnapshot>>([]);
        public Task DeleteEventsBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task ClearSnapshotPathAsync(string eventId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class SlowRepository : IHandEventRepository
    {
        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task SaveBatchAsync(
            IReadOnlyList<HandEvent> events,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
        }

        public Task<IReadOnlyList<HandEvent>> QueryAsync(EventQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<HandEvent>>([]);
        public Task<HandEvent?> GetByIdAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult<HandEvent?>(null);
        public Task<IReadOnlyList<EventStatistic>> GetStatisticsAsync(EventQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<EventStatistic>>([]);
        public Task<IReadOnlyList<StoredSnapshot>> GetSnapshotsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StoredSnapshot>>([]);
        public Task DeleteEventsBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task ClearSnapshotPathAsync(string eventId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private TestDatabase(SqliteConnection connection, DbContextOptions<HandRaiseDbContext> options)
        {
            Connection = connection;
            Options = options;
        }

        public SqliteConnection Connection { get; }
        public DbContextOptions<HandRaiseDbContext> Options { get; }

        public static async Task<TestDatabase> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<HandRaiseDbContext>()
                .UseSqlite(connection)
                .Options;
            await using var context = new HandRaiseDbContext(options);
            await context.Database.MigrateAsync();
            return new TestDatabase(connection, options);
        }

        public ValueTask DisposeAsync() => Connection.DisposeAsync();
    }
}
