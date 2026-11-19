using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PHANTOM.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class IncidentHumanVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // xmin is a PostgreSQL system column (it was only mapped, never created), so it cannot be
            // renamed. Human decisions now maintain an explicit version counter instead.
            migrationBuilder.AddColumn<int>(
                name: "version",
                table: "incidents",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "version",
                table: "incidents");
        }
    }
}
