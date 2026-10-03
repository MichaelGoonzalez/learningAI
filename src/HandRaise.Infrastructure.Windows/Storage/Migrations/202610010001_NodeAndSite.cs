using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HandRaise.Infrastructure.Windows.Storage.Migrations;

[DbContext(typeof(HandRaiseDbContext))]
[Migration("202610010001_NodeAndSite")]
public sealed class NodeAndSite : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "node_id",
            table: "events",
            type: "TEXT",
            nullable: false,
            defaultValue: "unknown");
        migrationBuilder.AddColumn<string>(
            name: "site_id",
            table: "events",
            type: "TEXT",
            nullable: false,
            defaultValue: "unknown");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "node_id", table: "events");
        migrationBuilder.DropColumn(name: "site_id", table: "events");
    }
}
