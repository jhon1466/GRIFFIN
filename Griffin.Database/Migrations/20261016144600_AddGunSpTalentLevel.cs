using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Griffin.Database.Core;

#nullable disable

namespace Griffin.Database.Migrations;

[DbContext(typeof(GriffinDbContext))]
[Migration("20261016144600_AddGunSpTalentLevel")]
public sealed class AddGunSpTalentLevel : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(
            name: "SpTalentLevel",
            table: "Guns",
            type: "bigint",
            nullable: false,
            defaultValue: 0L);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropColumn(name: "SpTalentLevel", table: "Guns");
}
