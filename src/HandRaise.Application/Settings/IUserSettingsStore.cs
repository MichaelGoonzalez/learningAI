namespace HandRaise.Application.Settings;

public interface IUserSettingsStore
{
    string FilePath { get; }

    Task<UserSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(UserSettings settings, CancellationToken cancellationToken = default);
}
