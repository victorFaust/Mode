using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mode.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class MessageUpdatedSequence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "UpdatedSequence",
                table: "Messages",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.Sql("UPDATE \"Messages\" SET \"UpdatedSequence\" = \"Sequence\";");

            migrationBuilder.CreateIndex(
                name: "IX_Messages_ChannelId_UpdatedSequence",
                table: "Messages",
                columns: new[] { "ChannelId", "UpdatedSequence" });

            migrationBuilder.CreateIndex(
                name: "IX_Messages_ConversationId_UpdatedSequence",
                table: "Messages",
                columns: new[] { "ConversationId", "UpdatedSequence" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Messages_ChannelId_UpdatedSequence",
                table: "Messages");

            migrationBuilder.DropIndex(
                name: "IX_Messages_ConversationId_UpdatedSequence",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "UpdatedSequence",
                table: "Messages");
        }
    }
}
