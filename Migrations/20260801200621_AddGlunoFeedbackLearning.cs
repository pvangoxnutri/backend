using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace sidequest.backend.Migrations
{
    /// <summary>
    /// Gluno's learning signals: feedback events, preference candidates and
    /// rejections.
    ///
    /// Purely additive — three new tables, nothing existing is touched. A user
    /// with no feedback history gets exactly the Gluno they had yesterday,
    /// which matters because these rows are an optimisation of relevance and
    /// never a requirement for planning.
    ///
    /// RETENTION, documented here because this is where the shape is decided:
    ///
    ///  • Every table CASCADES with the user. Account deletion removes all of
    ///    it — this is somebody's record of what they liked and turned down.
    ///  • Trip-scoped rows CASCADE with the Adventure, so deleting a trip takes
    ///    its feedback with it.
    ///  • Rejections carry ExpiresAt and stop applying on their own. An
    ///    open-ended "no" quietly shrinks what Gluno can ever offer, and nobody
    ///    would connect that to a tap they made months earlier.
    ///  • Candidates go stale after 45 days of silence (enforced in the query,
    ///    not by deletion) — a pattern nobody has repeated is not a pattern.
    ///  • Note is capped at 280 characters and is the only free text stored. It
    ///    is DATA: displayed back to its author, counted in aggregate, never
    ///    read as an instruction and never placed in a prompt.
    ///
    /// None of this is training data. Nothing here is sent anywhere or leaves
    /// SideQuest.
    ///
    /// RLS is enabled with no policies on all three, matching every other table
    /// (see EnableRowLevelSecurityOnAllTables, whose list is maintained by hand
    /// so each new table is a conscious decision). Without it these rows would
    /// be readable through PostgREST with the anon key that ships inside the
    /// app bundle — and they describe, per person, what somebody rejected and
    /// what Gluno has inferred about them. The backend connects as the table
    /// owner and bypasses RLS.
    /// </summary>
    public partial class AddGlunoFeedbackLearning : Migration
    {
        private static readonly string[] NewTables =
        [
            "GlunoFeedbackEvents",
            "GlunoPreferenceCandidates",
            "GlunoRejections",
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

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GlunoFeedbackEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    TripId = table.Column<Guid>(type: "uuid", nullable: true),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: true),
                    ProposalId = table.Column<Guid>(type: "uuid", nullable: true),
                    RecommendationRef = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    EventType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Scope = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Reason = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    Note = table.Column<string>(type: "character varying(280)", maxLength: 280, nullable: true),
                    ContextVersion = table.Column<int>(type: "integer", nullable: false),
                    Source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ProcessedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SupersededAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GlunoFeedbackEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GlunoFeedbackEvents_Trips_TripId",
                        column: x => x.TripId,
                        principalTable: "Trips",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GlunoFeedbackEvents_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GlunoPreferenceCandidates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TripId = table.Column<Guid>(type: "uuid", nullable: true),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: true),
                    Key = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ProposedValue = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: false),
                    Scope = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    EvidenceCount = table.Column<int>(type: "integer", nullable: false),
                    FirstObservedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastObservedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Confidence = table.Column<double>(type: "double precision", nullable: false),
                    SourceEventTypes = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AskedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ResolvedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GlunoPreferenceCandidates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GlunoPreferenceCandidates_Trips_TripId",
                        column: x => x.TripId,
                        principalTable: "Trips",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GlunoPreferenceCandidates_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GlunoRejections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TripId = table.Column<Guid>(type: "uuid", nullable: true),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: true),
                    Kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Reference = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Reason = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    ForDate = table.Column<DateOnly>(type: "date", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GlunoRejections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GlunoRejections_Trips_TripId",
                        column: x => x.TripId,
                        principalTable: "Trips",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GlunoRejections_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GlunoFeedbackEvents_MessageId_SupersededAt",
                table: "GlunoFeedbackEvents",
                columns: new[] { "MessageId", "SupersededAt" });

            migrationBuilder.CreateIndex(
                name: "IX_GlunoFeedbackEvents_TripId",
                table: "GlunoFeedbackEvents",
                column: "TripId");

            migrationBuilder.CreateIndex(
                name: "IX_GlunoFeedbackEvents_UserId_EventType",
                table: "GlunoFeedbackEvents",
                columns: new[] { "UserId", "EventType" });

            migrationBuilder.CreateIndex(
                name: "IX_GlunoPreferenceCandidates_TripId",
                table: "GlunoPreferenceCandidates",
                column: "TripId");

            migrationBuilder.CreateIndex(
                name: "IX_GlunoPreferenceCandidates_UserId_Key_TripId_Status",
                table: "GlunoPreferenceCandidates",
                columns: new[] { "UserId", "Key", "TripId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_GlunoRejections_TripId",
                table: "GlunoRejections",
                column: "TripId");

            migrationBuilder.CreateIndex(
                name: "IX_GlunoRejections_UserId_TripId_ExpiresAt",
                table: "GlunoRejections",
                columns: new[] { "UserId", "TripId", "ExpiresAt" });

            // Deny PostgREST (anon + authenticated) every row — same no-policy
            // lockdown as every other table.
            foreach (var table in NewTables)
            {
                migrationBuilder.Sql(ToggleRlsSql(table, "ENABLE"));
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GlunoFeedbackEvents");

            migrationBuilder.DropTable(
                name: "GlunoPreferenceCandidates");

            migrationBuilder.DropTable(
                name: "GlunoRejections");
        }
    }
}
