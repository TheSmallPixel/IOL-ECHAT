using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ECHAT.Server.App.Data.Migrations
{
    /// <inheritdoc />
    public partial class SafetyBundleAndChainBoundaries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CustodianUserId",
                table: "MigrationJobs",
                type: "char(36)",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                collation: "ascii_general_ci");

            migrationBuilder.AddColumn<long>(
                name: "MaxReplacedSeq",
                table: "MigrationJobs",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "ChainBoundaries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    ConversationId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    AfterSeq = table.Column<long>(type: "bigint", nullable: false),
                    AtEpoch = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChainBoundaries", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_ChainBoundaries_ConversationId_AfterSeq",
                table: "ChainBoundaries",
                columns: new[] { "ConversationId", "AfterSeq" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChainBoundaries");

            migrationBuilder.DropColumn(
                name: "CustodianUserId",
                table: "MigrationJobs");

            migrationBuilder.DropColumn(
                name: "MaxReplacedSeq",
                table: "MigrationJobs");
        }
    }
}
