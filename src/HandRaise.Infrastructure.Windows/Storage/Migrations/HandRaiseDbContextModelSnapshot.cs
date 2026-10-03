using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HandRaise.Infrastructure.Windows.Storage.Migrations;

[DbContext(typeof(HandRaiseDbContext))]
public sealed class HandRaiseDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder)
    {
        modelBuilder.HasAnnotation("ProductVersion", "10.0.12");
        modelBuilder.Entity("HandRaise.Infrastructure.Windows.Storage.HandEventEntity", entity =>
        {
            entity.Property<string>("Id").HasColumnType("TEXT").HasColumnName("id");
            entity.Property<string>("CameraId").IsRequired().HasColumnType("TEXT").HasColumnName("camera_id");
            entity.Property<string>("NodeId").IsRequired().HasColumnType("TEXT").HasColumnName("node_id");
            entity.Property<string>("SiteId").IsRequired().HasColumnType("TEXT").HasColumnName("site_id");
            entity.Property<double>("Confidence").HasColumnType("REAL").HasColumnName("confidence");
            entity.Property<string>("Hand").IsRequired().HasColumnType("TEXT").HasColumnName("hand");
            entity.Property<string>("SnapshotPath").HasColumnType("TEXT").HasColumnName("snapshot_path");
            entity.Property<DateTimeOffset>("Timestamp").HasColumnType("INTEGER").HasColumnName("timestamp_utc_ticks");
            entity.Property<int>("TrackId").HasColumnType("INTEGER").HasColumnName("track_id");
            entity.Property<string>("Type").IsRequired().HasColumnType("TEXT").HasColumnName("type");
            entity.Property<string>("Zone").HasColumnType("TEXT").HasColumnName("zone");
            entity.HasKey("Id");
            entity.HasIndex("Type").HasDatabaseName("ix_events_type");
            entity.HasIndex("CameraId", "Timestamp").HasDatabaseName("ix_events_camera_timestamp");
            entity.ToTable("events");
        });
    }
}
