using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ECHAT.Server.App.Data.Migrations
{
    /// <inheritdoc />
    public partial class FreshStartWipeConversations : Migration
    {

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM `Files`;");
            migrationBuilder.Sql("DELETE FROM `Members`;");
            migrationBuilder.Sql("DELETE FROM `Conversations`;");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
