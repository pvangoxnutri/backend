using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace sidequest.backend.Migrations
{
    /// <inheritdoc />
    public partial class AddGlunoDraftContinuation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "DraftId",
                table: "GlunoProposals",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DraftVersion",
                table: "GlunoProposals",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AcceptedConflictsJson",
                table: "GlunoProposalDrafts",
                type: "character varying(400)",
                maxLength: 400,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ConflictMetaJson",
                table: "GlunoClarifications",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ConflictType",
                table: "GlunoClarifications",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ConflictVersion",
                table: "GlunoClarifications",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DraftId",
                table: "GlunoClarifications",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DraftVersion",
                table: "GlunoClarifications",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DraftId",
                table: "GlunoProposals");

            migrationBuilder.DropColumn(
                name: "DraftVersion",
                table: "GlunoProposals");

            migrationBuilder.DropColumn(
                name: "AcceptedConflictsJson",
                table: "GlunoProposalDrafts");

            migrationBuilder.DropColumn(
                name: "ConflictMetaJson",
                table: "GlunoClarifications");

            migrationBuilder.DropColumn(
                name: "ConflictType",
                table: "GlunoClarifications");

            migrationBuilder.DropColumn(
                name: "ConflictVersion",
                table: "GlunoClarifications");

            migrationBuilder.DropColumn(
                name: "DraftId",
                table: "GlunoClarifications");

            migrationBuilder.DropColumn(
                name: "DraftVersion",
                table: "GlunoClarifications");
        }
    }
}
