using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace sidequest.backend.Migrations
{
    /// <inheritdoc />
    public partial class AddThemesAndUserTheme : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Themes",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    PrimaryColor = table.Column<string>(type: "text", nullable: false),
                    SecondaryColor = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Themes", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "Themes",
                columns: new[] { "Id", "Name", "PrimaryColor", "SecondaryColor" },
                values: new object[,]
                {
                    { "classic",    "Classic",    "#ff4f74", "#0d90a8" },
                    { "ocean",      "Ocean",      "#2563eb", "#0891b2" },
                    { "forest",     "Forest",     "#16a34a", "#ea580c" },
                    { "lemonglow",  "Lemon Glow", "#F4E649", "#28303D" },
                });

            migrationBuilder.AddColumn<string>(
                name: "ThemeId",
                table: "Users",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ThemeId",
                table: "Users");

            migrationBuilder.DropTable(
                name: "Themes");
        }
    }
}
