using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PHANTOM.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddResponders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "responders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    call_sign = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    full_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    team = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    phone = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    skills = table.Column<string[]>(type: "text[]", nullable: false),
                    availability = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    last_status_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    is_demo_data = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_responders", x => x.id);
                    table.ForeignKey(
                        name: "fk_responders_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_responders_call_sign",
                table: "responders",
                column: "call_sign",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_responders_is_active_availability",
                table: "responders",
                columns: new[] { "is_active", "availability" });

            migrationBuilder.CreateIndex(
                name: "ix_responders_skills",
                table: "responders",
                column: "skills")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "ix_responders_team",
                table: "responders",
                column: "team");

            migrationBuilder.CreateIndex(
                name: "ix_responders_user_id",
                table: "responders",
                column: "user_id",
                unique: true,
                filter: "user_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "responders");
        }
    }
}
