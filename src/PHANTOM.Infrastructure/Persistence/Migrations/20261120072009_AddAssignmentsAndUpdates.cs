using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PHANTOM.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAssignmentsAndUpdates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "responder_assignments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    incident_id = table.Column<Guid>(type: "uuid", nullable: false),
                    responder_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    assigned_by_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assigned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    acknowledged_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    en_route_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    on_scene_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ended_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    override_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    completion_notes = table.Column<string>(type: "character varying(5000)", maxLength: 5000, nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_responder_assignments", x => x.id);
                    table.ForeignKey(
                        name: "fk_responder_assignments_incidents_incident_id",
                        column: x => x.incident_id,
                        principalTable: "incidents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_responder_assignments_responders_responder_id",
                        column: x => x.responder_id,
                        principalTable: "responders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_responder_assignments_users_assigned_by_id",
                        column: x => x.assigned_by_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "incident_updates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    incident_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assignment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    author_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    body = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_incident_updates", x => x.id);
                    table.ForeignKey(
                        name: "fk_incident_updates_incidents_incident_id",
                        column: x => x.incident_id,
                        principalTable: "incidents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_incident_updates_responder_assignments_assignment_id",
                        column: x => x.assignment_id,
                        principalTable: "responder_assignments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_incident_updates_users_author_id",
                        column: x => x.author_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_incident_updates_assignment_id",
                table: "incident_updates",
                column: "assignment_id");

            migrationBuilder.CreateIndex(
                name: "ix_incident_updates_author_id",
                table: "incident_updates",
                column: "author_id");

            migrationBuilder.CreateIndex(
                name: "ix_incident_updates_incident_id_created_at",
                table: "incident_updates",
                columns: new[] { "incident_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_responder_assignments_assigned_by_id",
                table: "responder_assignments",
                column: "assigned_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_responder_assignments_incident_id_assigned_at",
                table: "responder_assignments",
                columns: new[] { "incident_id", "assigned_at" });

            migrationBuilder.CreateIndex(
                name: "ix_responder_assignments_responder_id_status",
                table: "responder_assignments",
                columns: new[] { "responder_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_responder_assignments_active_pair",
                table: "responder_assignments",
                columns: new[] { "incident_id", "responder_id" },
                unique: true,
                filter: "status NOT IN ('Completed', 'Declined', 'Cancelled')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "incident_updates");

            migrationBuilder.DropTable(
                name: "responder_assignments");
        }
    }
}
