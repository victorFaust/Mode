using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mode.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Notifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsMuted",
                table: "ReadMarkers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EmailedAt",
                table: "ActivityItems",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PushSubscriptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Endpoint = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    P256dh = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Auth = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PushSubscriptions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ActivityItems_UserId_Unread",
                table: "ActivityItems",
                column: "UserId",
                filter: "\"ReadAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PushSubscriptions_Endpoint",
                table: "PushSubscriptions",
                column: "Endpoint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PushSubscriptions_UserId",
                table: "PushSubscriptions",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PushSubscriptions");

            migrationBuilder.DropIndex(
                name: "IX_ActivityItems_UserId_Unread",
                table: "ActivityItems");

            migrationBuilder.DropColumn(
                name: "IsMuted",
                table: "ReadMarkers");

            migrationBuilder.DropColumn(
                name: "EmailedAt",
                table: "ActivityItems");
        }
    }
}
