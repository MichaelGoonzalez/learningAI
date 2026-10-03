using Microsoft.EntityFrameworkCore;

namespace HandRaise.Infrastructure.Windows.Storage;

public static class StorageDatabase
{
    public static DbContextOptions<HandRaiseDbContext> CreateOptions(string databasePath)
    {
        var path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(databasePath));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return new DbContextOptionsBuilder<HandRaiseDbContext>()
            .UseSqlite($"Data Source={path}")
            .Options;
    }

    public static async Task MigrateAsync(
        DbContextOptions<HandRaiseDbContext> options,
        CancellationToken cancellationToken = default)
    {
        await using var context = new HandRaiseDbContext(options);
        await context.Database.MigrateAsync(cancellationToken);
    }
}
