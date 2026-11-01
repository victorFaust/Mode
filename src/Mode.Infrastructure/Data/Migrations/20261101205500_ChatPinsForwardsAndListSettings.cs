using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

namespace Mode.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class ChatPinsForwardsAndListSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ForwardedBodyMarkdown",
                table: "Messages",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ForwardedFromCreatedAt",
                table: "Messages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ForwardedFromMessageId",
                table: "Messages",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ForwardedFromUserId",
                table: "Messages",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsHidden",
                table: "ConversationMembers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsPinned",
                table: "ConversationMembers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // PostgreSQL 16 can't change a generated column's expression, so drop and re-add it with its index.
            ReplaceSearchVector(migrationBuilder, "to_tsvector('english', \"BodyMarkdown\" || ' ' || coalesce(\"ForwardedBodyMarkdown\", ''))");

            migrationBuilder.CreateTable(
                name: "PinnedMessages",
                columns: table => new
                {
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    PinnedByUserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PinnedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PinnedMessages", x => x.MessageId);
                    table.ForeignKey(
                        name: "FK_PinnedMessages_Messages_MessageId",
                        column: x => x.MessageId,
                        principalTable: "Messages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Messages_ForwardedFromMessageId",
                table: "Messages",
                column: "ForwardedFromMessageId");

            migrationBuilder.AddForeignKey(
                name: "FK_Messages_Messages_ForwardedFromMessageId",
                table: "Messages",
                column: "ForwardedFromMessageId",
                principalTable: "Messages",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Messages_Messages_ForwardedFromMessageId",
                table: "Messages");

            migrationBuilder.DropTable(
                name: "PinnedMessages");

            migrationBuilder.DropIndex(
                name: "IX_Messages_ForwardedFromMessageId",
                table: "Messages");

            ReplaceSearchVector(migrationBuilder, "to_tsvector('english', \"BodyMarkdown\")");

            migrationBuilder.DropColumn(
                name: "ForwardedBodyMarkdown",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "ForwardedFromCreatedAt",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "ForwardedFromMessageId",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "ForwardedFromUserId",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "IsHidden",
                table: "ConversationMembers");

            migrationBuilder.DropColumn(
                name: "IsPinned",
                table: "ConversationMembers");
        }

        private static void ReplaceSearchVector(MigrationBuilder migrationBuilder, string expression)
        {
            migrationBuilder.DropIndex(name: "IX_Messages_SearchVector", table: "Messages");
            migrationBuilder.DropColumn(name: "SearchVector", table: "Messages");
            migrationBuilder.AddColumn<NpgsqlTsVector>(
                name: "SearchVector",
                table: "Messages",
                type: "tsvector",
                nullable: true,
                computedColumnSql: expression,
                stored: true);
            migrationBuilder.CreateIndex(name: "IX_Messages_SearchVector", table: "Messages", column: "SearchVector")
                .Annotation("Npgsql:IndexMethod", "GIN");
        }
    }
}
