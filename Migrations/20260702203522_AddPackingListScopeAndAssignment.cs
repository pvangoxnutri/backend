using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace sidequest.backend.Migrations
{
    /// <inheritdoc />
    public partial class AddPackingListScopeAndAssignment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AssignedToUserId",
                table: "PackingListItems",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Scope",
                table: "PackingListCategories",
                type: "text",
                nullable: false,
                defaultValue: "shared");

            migrationBuilder.AddColumn<Guid>(
                name: "UserId",
                table: "PackingListCategories",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AssignedToUserId",
                table: "PackingListItems");

            migrationBuilder.DropColumn(
                name: "Scope",
                table: "PackingListCategories");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "PackingListCategories");
        }
    }
}
