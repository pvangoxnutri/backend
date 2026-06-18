using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace sidequest.backend.Migrations
{
    /// <inheritdoc />
    public partial class AddMembersCanEditToTrips : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Default true so existing trips keep collaborative editing
            // (members can edit). Owners can lock it off afterwards.
            migrationBuilder.AddColumn<bool>(
                name: "MembersCanEdit",
                table: "Trips",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MembersCanEdit",
                table: "Trips");
        }
    }
}
