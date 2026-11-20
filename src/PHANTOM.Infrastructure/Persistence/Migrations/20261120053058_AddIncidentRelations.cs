using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PHANTOM.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddIncidentRelations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "incident_relations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_incident_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_incident_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    score = table.Column<decimal>(type: "numeric(4,3)", nullable: true),
                    signals = table.Column<string>(type: "jsonb", nullable: true),
                    explanation = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    decided_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    primary_incident_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_incident_relations", x => x.id);
                    table.ForeignKey(
                        name: "fk_incident_relations_incidents_primary_incident_id",
                        column: x => x.primary_incident_id,
                        principalTable: "incidents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_incident_relations_incidents_source_incident_id",
                        column: x => x.source_incident_id,
                        principalTable: "incidents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_incident_relations_incidents_target_incident_id",
                        column: x => x.target_incident_id,
                        principalTable: "incidents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_incident_relations_primary_incident_id",
                table: "incident_relations",
                column: "primary_incident_id");

            migrationBuilder.CreateIndex(
                name: "ix_incident_relations_source_incident_id_target_incident_id",
                table: "incident_relations",
                columns: new[] { "source_incident_id", "target_incident_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_incident_relations_target_incident_id",
                table: "incident_relations",
                column: "target_incident_id");

            migrationBuilder.CreateIndex(
                name: "ix_incident_relations_type",
                table: "incident_relations",
                column: "type");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "incident_relations");
        }
    }
}
