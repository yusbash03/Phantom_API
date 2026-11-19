using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PHANTOM.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAnalysisClaimedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "claimed_at",
                table: "incident_analyses",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "claimed_at",
                table: "incident_analyses");
        }
    }
}
