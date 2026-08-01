using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace sidequest.backend.Migrations
{
    /// <summary>
    /// Lets one activity's photo be kept out of the trip's slideshow while
    /// staying fully visible on the activity itself.
    ///
    /// Backward compatible by construction: the column is NOT NULL with a
    /// default of false, and false means "include". Every existing activity
    /// therefore keeps appearing in the slideshow exactly as it does today —
    /// no data pass, nothing to backfill.
    /// </summary>
    public partial class AddActivityExcludeFromSlideshow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ExcludeFromSlideshow",
                table: "TripActivities",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExcludeFromSlideshow",
                table: "TripActivities");
        }
    }
}
