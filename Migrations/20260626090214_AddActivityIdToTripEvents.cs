using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace sidequest.backend.Migrations
{
    /// <inheritdoc />
    public partial class AddActivityIdToTripEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ActivityId",
                table: "TripEvents",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ActivityId",
                table: "TripEvents");
        }
    }
}
