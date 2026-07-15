using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace sidequest.backend.Migrations
{
    /// <inheritdoc />
    public partial class AddActivityEndDate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "EndDate",
                table: "TripActivities",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EndTime",
                table: "TripActivities",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EndDate",
                table: "TripActivities");

            migrationBuilder.DropColumn(
                name: "EndTime",
                table: "TripActivities");
        }
    }
}
