using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace sidequest.backend.Migrations
{
    /// <summary>
    /// Group planning: preference visibility, group decisions and votes.
    ///
    /// Purely additive. The three new preference columns all default to the
    /// SAFE value — visibility "private", not a hard constraint, no confirmation
    /// timestamp — so every preference that already exists stays private and
    /// keeps behaving exactly as it did. Nothing is retroactively shared with a
    /// group because a column appeared.
    ///
    /// That default is the whole security posture of this feature in one line:
    /// sharing is a deliberate act by the person whose preference it is, and a
    /// migration is not a person.
    ///
    /// Both new tables CASCADE — decisions with the Adventure, votes with the
    /// decision. A vote must not outlive the question it answered.
    ///
    /// RLS is enabled with no policies on both, matching every other table (see
    /// EnableRowLevelSecurityOnAllTables, whose list is maintained by hand so
    /// each new table is a conscious decision). It matters especially here:
    /// these rows record who voted for what and which constraints somebody
    /// shared, and without this they would be readable through PostgREST with
    /// the anon key that ships inside the app bundle. The backend connects as
    /// the table owner and bypasses RLS.
    /// </summary>
    public partial class AddGlunoGroupPlanning : Migration
    {
        private static readonly string[] NewTables =
        [
            "GlunoGroupDecisions",
            "GlunoGroupVotes",
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
            migrationBuilder.AddColumn<DateTime>(
                name: "ConfirmedAt",
                table: "GlunoPreferences",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsHardConstraint",
                table: "GlunoPreferences",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Visibility",
                table: "GlunoPreferences",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "GlunoGroupDecisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TripId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Question = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    OptionsJson = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AcceptedOptionId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    ClosingRule = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ClosesAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ResolvedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GlunoGroupDecisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GlunoGroupDecisions_Trips_TripId",
                        column: x => x.TripId,
                        principalTable: "Trips",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GlunoGroupVotes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DecisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    OptionId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GlunoGroupVotes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GlunoGroupVotes_GlunoGroupDecisions_DecisionId",
                        column: x => x.DecisionId,
                        principalTable: "GlunoGroupDecisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GlunoGroupDecisions_TripId_Status",
                table: "GlunoGroupDecisions",
                columns: new[] { "TripId", "Status" });

            // ONE vote per member per decision, enforced by the database. This
            // index IS the guarantee — an application-level check lets a double
            // tap through and double-counts one person.
            migrationBuilder.CreateIndex(
                name: "IX_GlunoGroupVotes_DecisionId_UserId",
                table: "GlunoGroupVotes",
                columns: new[] { "DecisionId", "UserId" },
                unique: true);

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
                name: "GlunoGroupVotes");

            migrationBuilder.DropTable(
                name: "GlunoGroupDecisions");

            migrationBuilder.DropColumn(
                name: "ConfirmedAt",
                table: "GlunoPreferences");

            migrationBuilder.DropColumn(
                name: "IsHardConstraint",
                table: "GlunoPreferences");

            migrationBuilder.DropColumn(
                name: "Visibility",
                table: "GlunoPreferences");
        }
    }
}
