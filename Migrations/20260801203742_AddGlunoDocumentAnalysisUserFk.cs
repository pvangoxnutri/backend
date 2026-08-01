using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace sidequest.backend.Migrations
{
    /// <inheritdoc />
    public partial class AddGlunoDocumentAnalysisUserFk : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Rows that already lost their user. They exist precisely because
            // the constraint below was missing, and they are the reason it is
            // being added — extracted booking references outliving the account
            // they belong to. Removing them is the point of the migration, not
            // a side effect of it.
            migrationBuilder.Sql("""
                DELETE FROM "GlunoDocumentAnalyses" a
                WHERE NOT EXISTS (SELECT 1 FROM "Users" u WHERE u."Id" = a."UserId");
                """);

            migrationBuilder.CreateIndex(
                name: "IX_GlunoDocumentAnalyses_UserId",
                table: "GlunoDocumentAnalyses",
                column: "UserId");

            migrationBuilder.AddForeignKey(
                name: "FK_GlunoDocumentAnalyses_Users_UserId",
                table: "GlunoDocumentAnalyses",
                column: "UserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_GlunoDocumentAnalyses_Users_UserId",
                table: "GlunoDocumentAnalyses");

            migrationBuilder.DropIndex(
                name: "IX_GlunoDocumentAnalyses_UserId",
                table: "GlunoDocumentAnalyses");
        }
    }
}
