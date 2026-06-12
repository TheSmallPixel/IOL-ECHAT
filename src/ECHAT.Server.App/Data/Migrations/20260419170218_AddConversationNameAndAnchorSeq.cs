using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ECHAT.Server.App.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddConversationNameAndAnchorSeq : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "AnchorSeq",
                table: "SeqCounters",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<Guid>(
                name: "CreatedByUserId",
                table: "Conversations",
                type: "char(36)",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                collation: "ascii_general_ci");

            migrationBuilder.AddColumn<string>(
                name: "Name",
                table: "Conversations",
                type: "longtext",
                nullable: false)
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AnchorSeq",
                table: "SeqCounters");

            migrationBuilder.DropColumn(
                name: "CreatedByUserId",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "Name",
                table: "Conversations");
        }
    }
}
