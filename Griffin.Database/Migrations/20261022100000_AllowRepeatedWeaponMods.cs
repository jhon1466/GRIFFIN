using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Griffin.Database.Core;

#nullable disable

namespace Griffin.Database.Migrations;

/// <summary>
/// The game gives every dropped weapon mod as its own instance with its own affixes, so an account can hold several mods of the same type: the
/// unique index (AccountUid, WeaponModId) of the first version becomes a plain index (A-148).
/// </summary>
[DbContext(typeof(GriffinDbContext))]
[Migration("20261022100000_AllowRepeatedWeaponMods")]
public sealed class AllowRepeatedWeaponMods : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: "IX_WeaponMods_AccountUid_WeaponModId", table: "WeaponMods");
        migrationBuilder.CreateIndex(name: "IX_WeaponMods_AccountUid_WeaponModId", table: "WeaponMods", columns: new[] { "AccountUid", "WeaponModId" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: "IX_WeaponMods_AccountUid_WeaponModId", table: "WeaponMods");
        migrationBuilder.CreateIndex(name: "IX_WeaponMods_AccountUid_WeaponModId", table: "WeaponMods", columns: new[] { "AccountUid", "WeaponModId" }, unique: true);
    }
}
