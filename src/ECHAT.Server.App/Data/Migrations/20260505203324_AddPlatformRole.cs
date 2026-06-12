using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ECHAT.Server.App.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPlatformRole : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PlatformRole",
                table: "Users",
                type: "longtext",
                nullable: false,
                defaultValue: "User")
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PlatformRole",
                table: "Users");
        }
    }
}
