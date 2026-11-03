using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mode.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AuditSeal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Hash",
                table: "AuditLogs",
                type: "character(64)",
                fixedLength: true,
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_Unsealed",
                table: "AuditLogs",
                column: "Id",
                filter: "\"Hash\" IS NULL");

            // The only permitted change is sealing: setting Hash once, with every other column unchanged.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION audit_logs_append_only() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'UPDATE' AND OLD."Hash" IS NULL AND NEW."Hash" IS NOT NULL
                       AND (NEW."Id", NEW."At", NEW."ActorUserId", NEW."Action", NEW."TargetType", NEW."TargetId",
                            NEW."WorkspaceId", NEW."DataJson", NEW."IpAddress")
                           IS NOT DISTINCT FROM
                           (OLD."Id", OLD."At", OLD."ActorUserId", OLD."Action", OLD."TargetType", OLD."TargetId",
                            OLD."WorkspaceId", OLD."DataJson", OLD."IpAddress") THEN
                        RETURN NEW;
                    END IF;
                    RAISE EXCEPTION 'AuditLogs is append-only' USING ERRCODE = 'insufficient_privilege';
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION audit_logs_append_only() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'AuditLogs is append-only' USING ERRCODE = 'insufficient_privilege';
                END $$;
                """);

            migrationBuilder.DropIndex(
                name: "IX_AuditLogs_Unsealed",
                table: "AuditLogs");

            migrationBuilder.DropColumn(
                name: "Hash",
                table: "AuditLogs");
        }
    }
}
