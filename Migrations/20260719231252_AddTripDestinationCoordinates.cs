using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace sidequest.backend.Migrations
{
    /// <inheritdoc />
    public partial class AddTripDestinationCoordinates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "DestinationLatitude",
                table: "Trips",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "DestinationLongitude",
                table: "Trips",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DestinationPlaceId",
                table: "Trips",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DestinationLatitude",
                table: "Trips");

            migrationBuilder.DropColumn(
                name: "DestinationLongitude",
                table: "Trips");

            migrationBuilder.DropColumn(
                name: "DestinationPlaceId",
                table: "Trips");
        }
    }
}
