using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Griffin.Database.Core;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Griffin.Database.Migrations;

[DbContext(typeof(GriffinDbContext))]
[Migration("20261014221500_AddAccountStateEntity")]
public partial class AddAccountStateEntity : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "AccountStates",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                AccountUid = table.Column<long>(type: "bigint", nullable: false),
                Key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                Json = table.Column<string>(type: "jsonb", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AccountStates", x => x.Id);
                table.ForeignKey(
                    name: "FK_AccountStates_Accounts_AccountUid",
                    column: x => x.AccountUid,
                    principalTable: "Accounts",
                    principalColumn: "Uid",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_AccountStates_AccountUid_Key",
            table: "AccountStates",
            columns: new[] { "AccountUid", "Key" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "AccountStates");
    }
}
