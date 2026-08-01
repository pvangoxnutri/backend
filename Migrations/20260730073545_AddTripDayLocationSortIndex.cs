using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace sidequest.backend.Migrations
{
    /// <inheritdoc />
    public partial class AddTripDayLocationSortIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TripDayLocations_TripId_StartDate",
                table: "TripDayLocations");

            migrationBuilder.AddColumn<int>(
                name: "SortIndex",
                table: "TripDayLocations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_TripDayLocations_TripId_StartDate_SortIndex",
                table: "TripDayLocations",
                columns: new[] { "TripId", "StartDate", "SortIndex" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TripDayLocations_TripId_StartDate_SortIndex",
                table: "TripDayLocations");

            migrationBuilder.DropColumn(
                name: "SortIndex",
                table: "TripDayLocations");

            migrationBuilder.CreateIndex(
                name: "IX_TripDayLocations_TripId_StartDate",
                table: "TripDayLocations",
                columns: new[] { "TripId", "StartDate" },
                unique: true);
        }
    }
}
