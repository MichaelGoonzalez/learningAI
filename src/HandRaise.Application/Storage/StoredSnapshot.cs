namespace HandRaise.Application.Storage;

public sealed record StoredSnapshot(string EventId, string RelativePath, DateTimeOffset Timestamp);
