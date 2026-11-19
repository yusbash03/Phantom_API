using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PHANTOM.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddIncidentIntakeTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Sequence for human-readable incident numbers: PH-YYYY-NNNNNN
            migrationBuilder.Sql("CREATE SEQUENCE IF NOT EXISTS incident_number_seq START 1 INCREMENT 1 CACHE 10;");

            migrationBuilder.CreateTable(
                name: "incidents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(5000)", maxLength: 5000, nullable: false),
                    category = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ai_recommended_category = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    approved_severity = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    ai_recommended_severity = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    location_text = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    location_latitude = table.Column<decimal>(type: "numeric(9,6)", nullable: true),
                    location_longitude = table.Column<decimal>(type: "numeric(9,6)", nullable: true),
                    location_provenance = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    location_precision = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    location_verified_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    location_verified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    location_needs_verification = table.Column<bool>(type: "boolean", nullable: false),
                    assigned_coordinator_id = table.Column<Guid>(type: "uuid", nullable: true),
                    verified_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    verified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resolution_summary = table.Column<string>(type: "character varying(5000)", maxLength: 5000, nullable: true),
                    resolved_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    closure_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    is_demo_data = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_incidents", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "incident_reports",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    reference = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    incident_id = table.Column<Guid>(type: "uuid", nullable: false),
                    original_incident_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(5000)", maxLength: 5000, nullable: false),
                    category = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    location_text = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    reported_latitude = table.Column<decimal>(type: "numeric(9,6)", nullable: true),
                    reported_longitude = table.Column<decimal>(type: "numeric(9,6)", nullable: true),
                    reporter_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    reporter_phone = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    reporter_email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    submitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    idempotency_key = table.Column<string>(type: "text", nullable: true),
                    client_ip_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    is_demo_data = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_incident_reports", x => x.id);
                    table.ForeignKey(
                        name: "fk_incident_reports_incidents_incident_id",
                        column: x => x.incident_id,
                        principalTable: "incidents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "attachments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    report_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    storage_key = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    content_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    original_file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    transcript_text = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: true),
                    transcript_provider = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    transcribed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_attachments", x => x.id);
                    table.ForeignKey(
                        name: "fk_attachments_incident_reports_report_id",
                        column: x => x.report_id,
                        principalTable: "incident_reports",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "incident_analyses",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    incident_id = table.Column<Guid>(type: "uuid", nullable: false),
                    report_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attempt = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    provider = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    schema_version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    latency_ms = table.Column<int>(type: "integer", nullable: true),
                    result = table.Column<string>(type: "jsonb", nullable: true),
                    recommended_severity = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    recommended_category = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    failure_code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    failure_message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    used_image_input = table.Column<bool>(type: "boolean", nullable: false),
                    used_audio_transcript = table.Column<bool>(type: "boolean", nullable: false),
                    requested_by_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_incident_analyses", x => x.id);
                    table.ForeignKey(
                        name: "fk_incident_analyses_incident_reports_report_id",
                        column: x => x.report_id,
                        principalTable: "incident_reports",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_incident_analyses_incidents_incident_id",
                        column: x => x.incident_id,
                        principalTable: "incidents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_attachments_report_id",
                table: "attachments",
                column: "report_id");

            migrationBuilder.CreateIndex(
                name: "ix_attachments_storage_key",
                table: "attachments",
                column: "storage_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_incident_analyses_incident_id",
                table: "incident_analyses",
                column: "incident_id");

            migrationBuilder.CreateIndex(
                name: "ix_incident_analyses_report_id_attempt",
                table: "incident_analyses",
                columns: new[] { "report_id", "attempt" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_incident_analyses_status_requested_at",
                table: "incident_analyses",
                columns: new[] { "status", "requested_at" });

            migrationBuilder.CreateIndex(
                name: "ix_incident_reports_idempotency_key",
                table: "incident_reports",
                column: "idempotency_key",
                unique: true,
                filter: "idempotency_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_incident_reports_incident_id",
                table: "incident_reports",
                column: "incident_id");

            migrationBuilder.CreateIndex(
                name: "ix_incident_reports_reference",
                table: "incident_reports",
                column: "reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_incidents_approved_severity",
                table: "incidents",
                column: "approved_severity");

            migrationBuilder.CreateIndex(
                name: "ix_incidents_category",
                table: "incidents",
                column: "category");

            migrationBuilder.CreateIndex(
                name: "ix_incidents_created_at",
                table: "incidents",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_incidents_number",
                table: "incidents",
                column: "number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_incidents_status_created_at",
                table: "incidents",
                columns: new[] { "status", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "attachments");

            migrationBuilder.DropTable(
                name: "incident_analyses");

            migrationBuilder.DropTable(
                name: "incident_reports");

            migrationBuilder.DropTable(
                name: "incidents");

            migrationBuilder.Sql("DROP SEQUENCE IF EXISTS incident_number_seq;");
        }
    }
}
