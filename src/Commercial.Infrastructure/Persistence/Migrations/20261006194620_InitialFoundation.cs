using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Commercial.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "commercial");

            migrationBuilder.CreateTable(
                name: "diagnostic_ping_receipts",
                schema: "commercial",
                columns: table => new
                {
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    correlation_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    note = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    recorded_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_diagnostic_ping_receipts", x => x.message_id);
                });

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                schema: "commercial",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    correlation_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    causation_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    processed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    locked_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    locked_until_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_messages", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_claimable",
                schema: "commercial",
                table: "outbox_messages",
                columns: new[] { "next_attempt_at_utc", "occurred_at_utc" },
                filter: "processed_at_utc IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_correlation_id",
                schema: "commercial",
                table: "outbox_messages",
                column: "correlation_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "diagnostic_ping_receipts",
                schema: "commercial");

            migrationBuilder.DropTable(
                name: "outbox_messages",
                schema: "commercial");
        }
    }
}
