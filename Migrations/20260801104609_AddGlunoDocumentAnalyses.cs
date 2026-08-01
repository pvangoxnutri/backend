using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace sidequest.backend.Migrations
{
    /// <summary>
    /// Gluno document readings.
    ///
    /// Purely additive — one new table, nothing existing is touched. Document
    /// upload, viewing and deletion behave exactly as before, which matters
    /// here because analysis is OFF by default and most deployments will never
    /// write a row.
    ///
    /// Rows CASCADE with the document. An extraction is derived from a file,
    /// and keeping it after the file is deleted leaves a structured record of
    /// somebody's booking that they believe they removed.
    ///
    /// RLS is enabled with no policies, matching every other table (see
    /// EnableRowLevelSecurityOnAllTables, whose list is maintained by hand so
    /// each new table is a conscious decision). This one matters as much as
    /// any: the payload holds flight times, hotel stays, addresses and booking
    /// references read out of private documents, and without this it would be
    /// readable through PostgREST with the anon key that ships inside the app
    /// bundle. The backend connects as the table owner and bypasses RLS.
    /// </summary>
    public partial class AddGlunoDocumentAnalyses : Migration
    {
        private const string GlunoDocumentAnalysesTable = "GlunoDocumentAnalyses";

        private static string ToggleRlsSql(string table, string action) => $"""
            DO $$
            BEGIN
                IF EXISTS (
                    SELECT 1 FROM pg_tables
                    WHERE schemaname = 'public' AND tablename = '{table}'
                ) THEN
                    EXECUTE 'ALTER TABLE public."{table}" {action} ROW LEVEL SECURITY';
                END IF;
            END
            $$;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GlunoDocumentAnalyses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    TripId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExtractionVersion = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    StructuredResultJson = table.Column<string>(type: "text", nullable: true),
                    SourceFileHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FailureCode = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    ProviderModel = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    RawTextExcerpt = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UserReviewedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SupersededAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GlunoDocumentAnalyses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GlunoDocumentAnalyses_TripDocuments_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "TripDocuments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GlunoDocumentAnalyses_DocumentId_SourceFileHash",
                table: "GlunoDocumentAnalyses",
                columns: new[] { "DocumentId", "SourceFileHash" });

            migrationBuilder.CreateIndex(
                name: "IX_GlunoDocumentAnalyses_TripId_Status",
                table: "GlunoDocumentAnalyses",
                columns: new[] { "TripId", "Status" });

            // Deny PostgREST (anon + authenticated) every row — same no-policy
            // lockdown as every other table.
            migrationBuilder.Sql(ToggleRlsSql(GlunoDocumentAnalysesTable, "ENABLE"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GlunoDocumentAnalyses");
        }
    }
}
