using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace sidequest.backend.Migrations
{
    /// <summary>
    /// Gluno's working memory for one conversation.
    ///
    /// Purely additive — one new table, nothing existing is touched. A
    /// conversation with no state row behaves exactly as it did before: the
    /// store returns a fresh empty state, references simply do not resolve from
    /// memory, and every other layer is unchanged. That matters more than usual
    /// here, because this table optimises continuity rather than being required
    /// for correctness.
    ///
    /// One row per conversation, CASCADEing with it. A deleted conversation
    /// must not leave behind a record of which restaurants somebody turned down.
    ///
    /// RLS is enabled with no policies, matching every other table (see
    /// EnableRowLevelSecurityOnAllTables, whose list is maintained by hand so
    /// each new table is a conscious decision). Without it these rows would be
    /// readable through PostgREST with the anon key that ships inside the app
    /// bundle — and the payload holds Activity ids, place ids, dates and the
    /// user's own stated preferences. The backend connects as the table owner
    /// and bypasses RLS, as everywhere.
    /// </summary>
    public partial class AddGlunoConversationStates : Migration
    {
        private const string GlunoConversationStatesTable = "GlunoConversationStates";

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
                name: "GlunoConversationStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    StateJson = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GlunoConversationStates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GlunoConversationStates_GlunoConversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "GlunoConversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Unique: exactly one working memory per conversation. The upsert in
            // GlunoWorkingStateStore relies on the database enforcing this
            // rather than on convention.
            migrationBuilder.CreateIndex(
                name: "IX_GlunoConversationStates_ConversationId",
                table: "GlunoConversationStates",
                column: "ConversationId",
                unique: true);

            // Deny PostgREST (anon + authenticated) every row — same no-policy
            // lockdown as every other table.
            migrationBuilder.Sql(ToggleRlsSql(GlunoConversationStatesTable, "ENABLE"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GlunoConversationStates");
        }
    }
}
