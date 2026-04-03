using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace sidequest.backend.Migrations
{
    /// <inheritdoc />
    public partial class AddRichSideQuestActivities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ImageUrl",
                table: "TripActivities",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OwnerId",
                table: "TripActivities",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RevealAt",
                table: "TripActivities",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Teaser",
                table: "TripActivities",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TeaserOffsetMinutes",
                table: "TripActivities",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Visibility",
                table: "TripActivities",
                type: "text",
                nullable: false,
                defaultValue: "public");

            migrationBuilder.Sql("""
                UPDATE "TripActivities"
                SET "Visibility" = CASE
                    WHEN "IsHidden" THEN 'hidden'
                    ELSE 'public'
                END;
                """);

            migrationBuilder.Sql("""
                UPDATE "TripActivities" AS activity
                SET "OwnerId" = COALESCE(
                    activity."AssignedToUserId",
                    trip."OwnerId"
                )
                FROM "Trips" AS trip
                WHERE trip."Id" = activity."TripId";
                """);

            migrationBuilder.AlterColumn<Guid>(
                name: "OwnerId",
                table: "TripActivities",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TripActivities_OwnerId",
                table: "TripActivities",
                column: "OwnerId");

            migrationBuilder.AddForeignKey(
                name: "FK_TripActivities_Users_OwnerId",
                table: "TripActivities",
                column: "OwnerId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_TripActivities_Users_OwnerId",
                table: "TripActivities");

            migrationBuilder.DropIndex(
                name: "IX_TripActivities_OwnerId",
                table: "TripActivities");

            migrationBuilder.DropColumn(
                name: "ImageUrl",
                table: "TripActivities");

            migrationBuilder.DropColumn(
                name: "OwnerId",
                table: "TripActivities");

            migrationBuilder.DropColumn(
                name: "RevealAt",
                table: "TripActivities");

            migrationBuilder.DropColumn(
                name: "Teaser",
                table: "TripActivities");

            migrationBuilder.DropColumn(
                name: "TeaserOffsetMinutes",
                table: "TripActivities");

            migrationBuilder.DropColumn(
                name: "Visibility",
                table: "TripActivities");
        }
    }
}
