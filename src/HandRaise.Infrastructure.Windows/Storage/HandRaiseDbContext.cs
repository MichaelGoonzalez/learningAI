using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace HandRaise.Infrastructure.Windows.Storage;

public sealed class HandRaiseDbContext(DbContextOptions<HandRaiseDbContext> options) : DbContext(options)
{
    public DbSet<HandEventEntity> Events => Set<HandEventEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var utcTicks = new ValueConverter<DateTimeOffset, long>(
            value => value.UtcTicks,
            value => new DateTimeOffset(value, TimeSpan.Zero));
        var events = modelBuilder.Entity<HandEventEntity>();
        events.ToTable("events");
        events.HasKey(item => item.Id);
        events.Property(item => item.Id).HasColumnName("id");
        events.Property(item => item.Type).HasColumnName("type").IsRequired();
        events.Property(item => item.CameraId).HasColumnName("camera_id").IsRequired();
        events.Property(item => item.NodeId).HasColumnName("node_id").IsRequired();
        events.Property(item => item.SiteId).HasColumnName("site_id").IsRequired();
        events.Property(item => item.TrackId).HasColumnName("track_id");
        events.Property(item => item.Hand).HasColumnName("hand").IsRequired();
        events.Property(item => item.Zone).HasColumnName("zone");
        events.Property(item => item.Confidence).HasColumnName("confidence");
        events.Property(item => item.Timestamp)
            .HasColumnName("timestamp_utc_ticks")
            .HasConversion(utcTicks);
        events.Property(item => item.SnapshotPath).HasColumnName("snapshot_path");
        events.HasIndex(item => new { item.CameraId, item.Timestamp })
            .HasDatabaseName("ix_events_camera_timestamp");
        events.HasIndex(item => item.Type).HasDatabaseName("ix_events_type");
    }
}
