using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace sidequest.backend.Migrations
{
    /// <inheritdoc />
    public partial class AddSortIndexToTripActivities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SortIndex",
                table: "TripActivities",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Backfill: assign sequential SortIndex per (TripId, Date) following
            // the existing Time/CreatedAt order, so today's visual order is
            // unchanged the moment this ships — only a later drag changes it.
            migrationBuilder.Sql("""
                UPDATE "TripActivities" AS t
                SET "SortIndex" = ranked.rn
                FROM (
                    SELECT "Id", ROW_NUMBER() OVER (
                        PARTITION BY "TripId", "Date"
                        ORDER BY "Time" NULLS LAST, "CreatedAt"
                    ) - 1 AS rn
                    FROM "TripActivities"
                ) AS ranked
                WHERE t."Id" = ranked."Id";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SortIndex",
                table: "TripActivities");
        }
    }
}
