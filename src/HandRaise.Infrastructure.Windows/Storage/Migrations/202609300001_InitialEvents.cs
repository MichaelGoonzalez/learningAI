using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;

#nullable disable

namespace HandRaise.Infrastructure.Windows.Storage.Migrations;

[DbContext(typeof(HandRaiseDbContext))]
[Migration("202609300001_InitialEvents")]
public sealed class InitialEvents : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "events",
            columns: table => new
            {
                id = table.Column<string>(type: "TEXT", nullable: false),
                type = table.Column<string>(type: "TEXT", nullable: false),
                camera_id = table.Column<string>(type: "TEXT", nullable: false),
                track_id = table.Column<int>(type: "INTEGER", nullable: false),
                hand = table.Column<string>(type: "TEXT", nullable: false),
                zone = table.Column<string>(type: "TEXT", nullable: true),
                confidence = table.Column<double>(type: "REAL", nullable: false),
                timestamp_utc_ticks = table.Column<long>(type: "INTEGER", nullable: false),
                snapshot_path = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_events", item => item.id));
        migrationBuilder.CreateIndex(
            name: "ix_events_camera_timestamp",
            table: "events",
            columns: ["camera_id", "timestamp_utc_ticks"]);
        migrationBuilder.CreateIndex(
            name: "ix_events_type",
            table: "events",
            column: "type");
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "events");
}
