using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace sidequest.backend.Migrations
{
    /// <inheritdoc />
    public partial class AddTripSlideshowEnabled : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Default TRUE: existing trips without the value must behave as
            // slideshow-enabled (backward compatibility rule).
            migrationBuilder.AddColumn<bool>(
                name: "SlideshowEnabled",
                table: "Trips",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SlideshowEnabled",
                table: "Trips");
        }
    }
}
