using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PHANTOM.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddIncidentGeocoding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "geocoding_claimed_at",
                table: "incidents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "geocoding_status",
                table: "incidents",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "NotRequired");

            migrationBuilder.AddColumn<string>(
                name: "location_geocoder_place_name",
                table: "incidents",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            // Existing incidents without coordinates have never been geocoded: queue them honestly.
            migrationBuilder.Sql(
                "UPDATE incidents SET geocoding_status = 'Pending' " +
                "WHERE location_latitude IS NULL AND location_provenance <> 'ManuallyVerified';");

            migrationBuilder.CreateIndex(
                name: "ix_incidents_geocoding_status_created_at",
                table: "incidents",
                columns: new[] { "geocoding_status", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_incidents_geocoding_status_created_at",
                table: "incidents");

            migrationBuilder.DropColumn(
                name: "geocoding_claimed_at",
                table: "incidents");

            migrationBuilder.DropColumn(
                name: "geocoding_status",
                table: "incidents");

            migrationBuilder.DropColumn(
                name: "location_geocoder_place_name",
                table: "incidents");
        }
    }
}
