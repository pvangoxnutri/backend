using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace sidequest.backend.Migrations
{
    /// <inheritdoc />
    public partial class AddHiddenSideQuestFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // User.Role
            migrationBuilder.AddColumn<string>(
                name: "Role",
                table: "Users",
                type: "text",
                nullable: false,
                defaultValue: "user");

            // Trip.Visibility
            migrationBuilder.AddColumn<string>(
                name: "Visibility",
                table: "Trips",
                type: "text",
                nullable: false,
                defaultValue: "public");

            // Trip.RevealAt
            migrationBuilder.AddColumn<DateTime>(
                name: "RevealAt",
                table: "Trips",
                type: "timestamp with time zone",
                nullable: true);

            // Trip.Teaser
            migrationBuilder.AddColumn<string>(
                name: "Teaser",
                table: "Trips",
                type: "text",
                nullable: true);

            // TripMember.IsOwner
            migrationBuilder.AddColumn<bool>(
                name: "IsOwner",
                table: "TripMembers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Backfill: existing trip creators (TripMember where UserId = Trip.OwnerId) become owners
            migrationBuilder.Sql("""
                UPDATE "TripMembers" tm
                SET "IsOwner" = true
                FROM "Trips" t
                WHERE tm."TripId" = t."Id" AND tm."UserId" = t."OwnerId";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "Role", table: "Users");
            migrationBuilder.DropColumn(name: "Visibility", table: "Trips");
            migrationBuilder.DropColumn(name: "RevealAt", table: "Trips");
            migrationBuilder.DropColumn(name: "Teaser", table: "Trips");
            migrationBuilder.DropColumn(name: "IsOwner", table: "TripMembers");
        }
    }
}
