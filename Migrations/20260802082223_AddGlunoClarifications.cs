using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace sidequest.backend.Migrations
{
    /// <inheritdoc />
    public partial class AddGlunoClarifications : Migration
    {
        /// <inheritdoc />

        /// <summary>
        /// Closes these tables to Supabase's auto-generated REST API.
        ///
        /// RLS on, and NO policies: with row level security enabled and no
        /// policy granting access, Postgres denies every row to the anon and
        /// authenticated roles PostgREST uses. The backend connects as the
        /// table owner, which bypasses RLS unless FORCE is set — deliberately
        /// not set here. Same pattern as every other Gluno table.
        /// </summary>
        private static readonly string[] NewTables =
        [
            "GlunoClarifications",
            "GlunoClarificationOptions",
        ];

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

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GlunoClarifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TripId = table.Column<Guid>(type: "uuid", nullable: true),
                    OriginalUserMessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: true),
                    Type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Question = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    OriginalIntent = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    AllowFreeText = table.Column<bool>(type: "boolean", nullable: false),
                    MultiSelect = table.Column<bool>(type: "boolean", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SelectedOptionId = table.Column<Guid>(type: "uuid", nullable: true),
                    ContinuationMessageId = table.Column<Guid>(type: "uuid", nullable: true),
                    ContextVersion = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ResolvedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GlunoClarifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GlunoClarifications_GlunoConversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "GlunoConversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GlunoClarifications_Trips_TripId",
                        column: x => x.TripId,
                        principalTable: "Trips",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_GlunoClarifications_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GlunoClarificationOptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClarificationId = table.Column<Guid>(type: "uuid", nullable: false),
                    OptionKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Label = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Description = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: true),
                    Icon = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: true),
                    EntityType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    EntityId = table.Column<Guid>(type: "uuid", nullable: true),
                    Value = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Disabled = table.Column<bool>(type: "boolean", nullable: false),
                    DisabledReason = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    SortIndex = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GlunoClarificationOptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GlunoClarificationOptions_GlunoClarifications_Clarification~",
                        column: x => x.ClarificationId,
                        principalTable: "GlunoClarifications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GlunoClarificationOptions_ClarificationId_OptionKey",
                table: "GlunoClarificationOptions",
                columns: new[] { "ClarificationId", "OptionKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GlunoClarifications_ConversationId_Status",
                table: "GlunoClarifications",
                columns: new[] { "ConversationId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_GlunoClarifications_MessageId",
                table: "GlunoClarifications",
                column: "MessageId");

            migrationBuilder.CreateIndex(
                name: "IX_GlunoClarifications_TripId",
                table: "GlunoClarifications",
                column: "TripId");

            migrationBuilder.CreateIndex(
                name: "IX_GlunoClarifications_UserId",
                table: "GlunoClarifications",
                column: "UserId");

            foreach (var table in NewTables)
            {
                migrationBuilder.Sql(ToggleRlsSql(table, "ENABLE"));
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GlunoClarificationOptions");

            migrationBuilder.DropTable(
                name: "GlunoClarifications");
        }
    }
}
