using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mode.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Governance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BotName",
                table: "Messages",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ComplianceExports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedByUserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FiltersJson = table.Column<string>(type: "jsonb", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    StorageKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    MessageCount = table.Column<int>(type: "integer", nullable: false),
                    FileCount = table.Column<int>(type: "integer", nullable: false),
                    ManifestSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ManifestSignature = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ComplianceExports", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DlpSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    BlockedExtensions = table.Column<List<string>>(type: "text[]", nullable: false),
                    UpdatedByUserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Patterns = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DlpSettings", x => x.Id);
                    table.CheckConstraint("CK_DlpSettings_Single", "\"Id\" = 1");
                });

            migrationBuilder.CreateTable(
                name: "LegalHolds",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    TargetId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    CreatedByUserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReleasedByUserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ReleasedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegalHolds", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RetentionPolicies",
                columns: table => new
                {
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    KeepDays = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RetentionPolicies", x => x.WorkspaceId);
                    table.CheckConstraint("CK_RetentionPolicies_KeepDays", "\"KeepDays\" IS NULL OR \"KeepDays\" >= 1");
                    table.ForeignKey(
                        name: "FK_RetentionPolicies_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Webhooks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ChannelId = table.Column<Guid>(type: "uuid", nullable: true),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    SecretHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedByUserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RotatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Webhooks", x => x.Id);
                    table.CheckConstraint("CK_Webhooks_SingleRoom", "num_nonnulls(\"ChannelId\", \"ConversationId\") = 1");
                    table.ForeignKey(
                        name: "FK_Webhooks_Channels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "Channels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Webhooks_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ComplianceExports_CreatedAt",
                table: "ComplianceExports",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_LegalHolds_Active",
                table: "LegalHolds",
                columns: new[] { "TargetType", "TargetId" },
                filter: "\"ReleasedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Webhooks_ChannelId",
                table: "Webhooks",
                column: "ChannelId");

            migrationBuilder.CreateIndex(
                name: "IX_Webhooks_ConversationId",
                table: "Webhooks",
                column: "ConversationId");

            // Audit entries are append-only for everyone, including the application's own database user.
            migrationBuilder.Sql("""
                CREATE FUNCTION audit_logs_append_only() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'AuditLogs is append-only' USING ERRCODE = 'insufficient_privilege';
                END $$;
                CREATE TRIGGER audit_logs_no_change BEFORE UPDATE OR DELETE ON "AuditLogs"
                    FOR EACH ROW EXECUTE FUNCTION audit_logs_append_only();
                CREATE TRIGGER audit_logs_no_truncate BEFORE TRUNCATE ON "AuditLogs"
                    FOR EACH STATEMENT EXECUTE FUNCTION audit_logs_append_only();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER audit_logs_no_truncate ON "AuditLogs";
                DROP TRIGGER audit_logs_no_change ON "AuditLogs";
                DROP FUNCTION audit_logs_append_only();
                """);

            migrationBuilder.DropTable(
                name: "ComplianceExports");

            migrationBuilder.DropTable(
                name: "DlpSettings");

            migrationBuilder.DropTable(
                name: "LegalHolds");

            migrationBuilder.DropTable(
                name: "RetentionPolicies");

            migrationBuilder.DropTable(
                name: "Webhooks");

            migrationBuilder.DropColumn(
                name: "BotName",
                table: "Messages");
        }
    }
}
