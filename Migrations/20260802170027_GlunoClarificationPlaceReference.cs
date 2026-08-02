using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace sidequest.backend.Migrations
{
    /// <inheritdoc />
    public partial class GlunoClarificationPlaceReference : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ContentSuppressed",
                table: "GlunoClarifications",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "PlaceMessageId",
                table: "GlunoClarifications",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PlaceOptionKey",
                table: "GlunoClarifications",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ContentSuppressed",
                table: "GlunoClarifications");

            migrationBuilder.DropColumn(
                name: "PlaceMessageId",
                table: "GlunoClarifications");

            migrationBuilder.DropColumn(
                name: "PlaceOptionKey",
                table: "GlunoClarifications");
        }
    }
}
