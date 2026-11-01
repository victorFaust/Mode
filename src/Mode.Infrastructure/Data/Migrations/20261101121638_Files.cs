using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using NpgsqlTypes;

#nullable disable

namespace Mode.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Files : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Sha256",
                table: "FileVersions",
                type: "character(64)",
                fixedLength: true,
                maxLength: 64,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character(64)",
                oldFixedLength: true,
                oldMaxLength: 64);

            migrationBuilder.AddColumn<string>(
                name: "ExtractedText",
                table: "FileVersions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreviewKey",
                table: "FileVersions",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreviewStatus",
                table: "FileVersions",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ScanDetail",
                table: "FileVersions",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ScannedAt",
                table: "FileVersions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ThumbnailKey",
                table: "FileVersions",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<NpgsqlTsVector>(
                name: "SearchVector",
                table: "FileVersions",
                type: "tsvector",
                nullable: true,
                computedColumnSql: "to_tsvector('english', coalesce(\"ExtractedText\", ''))",
                stored: true);

            migrationBuilder.CreateTable(
                name: "Jobs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    Type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    MaxAttempts = table.Column<int>(type: "integer", nullable: false),
                    RunAfter = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LockedUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Jobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MessageAttachments",
                columns: table => new
                {
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    FileAssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    VersionNo = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessageAttachments", x => new { x.MessageId, x.FileAssetId });
                    table.ForeignKey(
                        name: "FK_MessageAttachments_FileAssets_FileAssetId",
                        column: x => x.FileAssetId,
                        principalTable: "FileAssets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MessageAttachments_Messages_MessageId",
                        column: x => x.MessageId,
                        principalTable: "Messages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UploadSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ChannelId = table.Column<Guid>(type: "uuid", nullable: true),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: true),
                    FileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    NormalizedName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    DeclaredSize = table.Column<long>(type: "bigint", nullable: false),
                    ContentType = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    StorageKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UploadSessions", x => x.Id);
                    table.CheckConstraint("CK_UploadSessions_SingleRoom", "num_nonnulls(\"ChannelId\", \"ConversationId\") = 1");
                    table.ForeignKey(
                        name: "FK_UploadSessions_Channels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "Channels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UploadSessions_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FileVersions_SearchVector",
                table: "FileVersions",
                column: "SearchVector")
                .Annotation("Npgsql:IndexMethod", "GIN");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_Status_RunAfter",
                table: "Jobs",
                columns: new[] { "Status", "RunAfter" });

            migrationBuilder.CreateIndex(
                name: "IX_MessageAttachments_FileAssetId",
                table: "MessageAttachments",
                column: "FileAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_UploadSessions_ChannelId",
                table: "UploadSessions",
                column: "ChannelId");

            migrationBuilder.CreateIndex(
                name: "IX_UploadSessions_ConversationId",
                table: "UploadSessions",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_UploadSessions_ExpiresAt",
                table: "UploadSessions",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_UploadSessions_StorageKey",
                table: "UploadSessions",
                column: "StorageKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Jobs");

            migrationBuilder.DropTable(
                name: "MessageAttachments");

            migrationBuilder.DropTable(
                name: "UploadSessions");

            migrationBuilder.DropIndex(
                name: "IX_FileVersions_SearchVector",
                table: "FileVersions");

            migrationBuilder.DropColumn(
                name: "SearchVector",
                table: "FileVersions");

            migrationBuilder.DropColumn(
                name: "ExtractedText",
                table: "FileVersions");

            migrationBuilder.DropColumn(
                name: "PreviewKey",
                table: "FileVersions");

            migrationBuilder.DropColumn(
                name: "PreviewStatus",
                table: "FileVersions");

            migrationBuilder.DropColumn(
                name: "ScanDetail",
                table: "FileVersions");

            migrationBuilder.DropColumn(
                name: "ScannedAt",
                table: "FileVersions");

            migrationBuilder.DropColumn(
                name: "ThumbnailKey",
                table: "FileVersions");

            migrationBuilder.AlterColumn<string>(
                name: "Sha256",
                table: "FileVersions",
                type: "character(64)",
                fixedLength: true,
                maxLength: 64,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character(64)",
                oldFixedLength: true,
                oldMaxLength: 64,
                oldNullable: true);
        }
    }
}
