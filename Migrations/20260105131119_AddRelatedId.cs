using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DmsProjeckt.Migrations
{
    /// <inheritdoc />
    public partial class AddRelatedId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RelatedAufgabeId",
                table: "CalendarEvents",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RelatedAufgabeId",
                table: "CalendarEvents");
        }
    }
}
