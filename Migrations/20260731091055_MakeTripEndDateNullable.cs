using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace sidequest.backend.Migrations
{
    /// <summary>
    /// Lets an adventure exist without a known end date.
    ///
    /// Only the NOT NULL constraint is dropped — no row is read, written or
    /// defaulted. Every existing adventure keeps the end date it already has
    /// and behaves exactly as before; null is reachable only for trips created
    /// or edited after this ships.
    ///
    /// Null deliberately means "unknown", not "far future". Code that needs a
    /// finite range derives one at read time (see TripDateRange) rather than
    /// storing a placeholder, so an ongoing adventure's range keeps up with
    /// today instead of freezing at whatever date got written once.
    /// </summary>
    public partial class MakeTripEndDateNullable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<DateOnly>(
                name: "EndDate",
                table: "Trips",
                type: "date",
                nullable: true,
                oldClrType: typeof(DateOnly),
                oldType: "date");
        }

        /// <summary>
        /// Reversible, but LOSSY: restoring NOT NULL stamps every open-ended
        /// adventure with the default below (year 0001), because there is no
        /// true end date to put back. Trips that always had one are unaffected.
        /// Give any open-ended adventures a real end date before reverting.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<DateOnly>(
                name: "EndDate",
                table: "Trips",
                type: "date",
                nullable: false,
                defaultValue: new DateOnly(1, 1, 1),
                oldClrType: typeof(DateOnly),
                oldType: "date",
                oldNullable: true);
        }
    }
}
