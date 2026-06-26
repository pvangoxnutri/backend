using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace sidequest.backend.Migrations
{
    /// <inheritdoc />
    public partial class AddIsHiddenToTripEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsHidden",
                table: "TripEvents",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsHidden",
                table: "TripEvents");
        }
    }
}
