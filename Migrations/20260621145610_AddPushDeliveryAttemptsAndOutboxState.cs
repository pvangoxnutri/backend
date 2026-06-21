using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace sidequest.backend.Migrations
{
    /// <inheritdoc />
    public partial class AddPushDeliveryAttemptsAndOutboxState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ErrorMessage",
                table: "NotificationLogs");

            migrationBuilder.DropColumn(
                name: "Success",
                table: "NotificationLogs");

            migrationBuilder.AddColumn<string>(
                name: "Body",
                table: "NotificationLogs",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "DataJson",
                table: "NotificationLogs",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "DeliveredAt",
                table: "NotificationLogs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "NotificationLogs",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Title",
                table: "NotificationLogs",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "PushDeliveryAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    NotificationLogId = table.Column<Guid>(type: "uuid", nullable: false),
                    PushTokenId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    LastAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    NextAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ExpoTicketId = table.Column<string>(type: "text", nullable: true),
                    LastErrorCode = table.Column<string>(type: "text", nullable: true),
                    DeliveredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PushDeliveryAttempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PushDeliveryAttempts_NotificationLogs_NotificationLogId",
                        column: x => x.NotificationLogId,
                        principalTable: "NotificationLogs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PushDeliveryAttempts_PushTokens_PushTokenId",
                        column: x => x.PushTokenId,
                        principalTable: "PushTokens",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PushDeliveryAttempts_NotificationLogId",
                table: "PushDeliveryAttempts",
                column: "NotificationLogId");

            migrationBuilder.CreateIndex(
                name: "IX_PushDeliveryAttempts_PushTokenId",
                table: "PushDeliveryAttempts",
                column: "PushTokenId");

            migrationBuilder.CreateIndex(
                name: "IX_PushDeliveryAttempts_Status",
                table: "PushDeliveryAttempts",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_PushDeliveryAttempts_Status_NextAttemptAt",
                table: "PushDeliveryAttempts",
                columns: new[] { "Status", "NextAttemptAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PushDeliveryAttempts");

            migrationBuilder.DropColumn(
                name: "Body",
                table: "NotificationLogs");

            migrationBuilder.DropColumn(
                name: "DataJson",
                table: "NotificationLogs");

            migrationBuilder.DropColumn(
                name: "DeliveredAt",
                table: "NotificationLogs");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "NotificationLogs");

            migrationBuilder.DropColumn(
                name: "Title",
                table: "NotificationLogs");

            migrationBuilder.AddColumn<string>(
                name: "ErrorMessage",
                table: "NotificationLogs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Success",
                table: "NotificationLogs",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }
    }
}
