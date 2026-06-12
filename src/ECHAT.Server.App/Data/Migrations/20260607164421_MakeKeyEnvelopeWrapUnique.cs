using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ECHAT.Server.App.Data.Migrations
{
    /// <inheritdoc />
    public partial class MakeKeyEnvelopeWrapUnique : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_KeyEnvelopes_ConversationId_EpochId_DeviceId",
                table: "KeyEnvelopes");

            migrationBuilder.CreateIndex(
                name: "IX_KeyEnvelopes_ConversationId_EpochId_DeviceId",
                table: "KeyEnvelopes",
                columns: new[] { "ConversationId", "EpochId", "DeviceId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_KeyEnvelopes_ConversationId_EpochId_DeviceId",
                table: "KeyEnvelopes");

            migrationBuilder.CreateIndex(
                name: "IX_KeyEnvelopes_ConversationId_EpochId_DeviceId",
                table: "KeyEnvelopes",
                columns: new[] { "ConversationId", "EpochId", "DeviceId" });
        }
    }
}
