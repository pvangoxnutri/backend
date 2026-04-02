using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using sidequest.backend.Data;

#nullable disable

namespace sidequest.backend.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20260402113000_AddTripInvitesAndCodes")]
    public class AddTripInvitesAndCodes : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "InviteCode",
                table: "Trips",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql("""
                UPDATE "Trips"
                SET "InviteCode" = UPPER(SUBSTRING(REPLACE(CAST("Id" AS text), '-', '') FROM 1 FOR 6))
                WHERE COALESCE("InviteCode", '') = '';
                """);

            migrationBuilder.CreateTable(
                name: "TripInvites",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TripId = table.Column<Guid>(type: "uuid", nullable: false),
                    InvitedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TripInvites", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TripInvites_Trips_TripId",
                        column: x => x.TripId,
                        principalTable: "Trips",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TripInvites_Users_InvitedByUserId",
                        column: x => x.InvitedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TripInvites_InvitedByUserId",
                table: "TripInvites",
                column: "InvitedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_TripInvites_TripId_Email",
                table: "TripInvites",
                columns: new[] { "TripId", "Email" },
                unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TripInvites");

            migrationBuilder.DropColumn(
                name: "InviteCode",
                table: "Trips");
        }
    }
}
