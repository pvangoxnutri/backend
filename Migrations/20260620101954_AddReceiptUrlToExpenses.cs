using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace sidequest.backend.Migrations
{
    /// <inheritdoc />
    public partial class AddReceiptUrlToExpenses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReceiptUrl",
                table: "Expenses",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReceiptUrl",
                table: "Expenses");
        }
    }
}
