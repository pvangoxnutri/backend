using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace sidequest.backend.Migrations
{
    /// <inheritdoc />
    public partial class UpdatePendingChanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AssignedToUserId",
                table: "TripActivities",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsHidden",
                table: "TripActivities",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_TripActivities_AssignedToUserId",
                table: "TripActivities",
                column: "AssignedToUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_TripActivities_Users_AssignedToUserId",
                table: "TripActivities",
                column: "AssignedToUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_TripActivities_Users_AssignedToUserId",
                table: "TripActivities");

            migrationBuilder.DropIndex(
                name: "IX_TripActivities_AssignedToUserId",
                table: "TripActivities");

            migrationBuilder.DropColumn(
                name: "AssignedToUserId",
                table: "TripActivities");

            migrationBuilder.DropColumn(
                name: "IsHidden",
                table: "TripActivities");
        }
    }
}
