using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace sidequest.backend.Migrations
{
    /// <inheritdoc />
    public partial class AddActorAvatarAndActivityTitle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ActivityTitle",
                table: "TripEvents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ActorAvatarUrl",
                table: "NotificationLogs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ActorName",
                table: "NotificationLogs",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ActivityTitle",
                table: "TripEvents");

            migrationBuilder.DropColumn(
                name: "ActorAvatarUrl",
                table: "NotificationLogs");

            migrationBuilder.DropColumn(
                name: "ActorName",
                table: "NotificationLogs");
        }
    }
}
