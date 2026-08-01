using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace sidequest.backend.Migrations
{
    /// <summary>
    /// Idempotency claims for Gluno chat sends.
    ///
    /// Purely additive — one new table, nothing existing is touched. A client
    /// that sends no idempotency key writes no row and behaves exactly as
    /// before, which is what makes this safe to deploy ahead of the app change.
    ///
    /// The unique index is the whole point: a double tap races two inserts and
    /// the DATABASE decides which wins. A read-then-write check in application
    /// code lets both through, and the visible result is two answers, two
    /// charges, and two applicable proposals for the same day plan.
    ///
    /// Rows cascade with the conversation — an idempotency ledger must not
    /// outlive the chat it belongs to.
    ///
    /// RLS is enabled with no policies, matching every other table (see
    /// EnableRowLevelSecurityOnAllTables, whose list is maintained by hand so
    /// each new table is a conscious decision). Without it these rows would be
    /// readable through PostgREST with the anon key that ships inside the app
    /// bundle. The backend connects as the table owner and bypasses RLS.
    /// </summary>
    public partial class AddGlunoTurnRequests : Migration
    {
        private const string GlunoTurnRequestsTable = "GlunoTurnRequests";

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
                name: "GlunoTurnRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AssistantMessageId = table.Column<Guid>(type: "uuid", nullable: true),
                    FailureCode = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GlunoTurnRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GlunoTurnRequests_GlunoConversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "GlunoConversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GlunoTurnRequests_ConversationId",
                table: "GlunoTurnRequests",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_GlunoTurnRequests_UserId_ConversationId_IdempotencyKey",
                table: "GlunoTurnRequests",
                columns: new[] { "UserId", "ConversationId", "IdempotencyKey" },
                unique: true);

            // Deny PostgREST (anon + authenticated) every row — same no-policy
            // lockdown as every other table.
            migrationBuilder.Sql(ToggleRlsSql(GlunoTurnRequestsTable, "ENABLE"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GlunoTurnRequests");
        }
    }
}
