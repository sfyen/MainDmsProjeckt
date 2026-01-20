using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DmsProjeckt.Migrations
{
    /// <inheritdoc />
    public partial class AddCalendarEventIdToAufgaben : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CalendarEventId",
                table: "Aufgaben",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Aufgaben_CalendarEventId",
                table: "Aufgaben",
                column: "CalendarEventId");

            migrationBuilder.AddForeignKey(
                name: "FK_Aufgaben_CalendarEvents_CalendarEventId",
                table: "Aufgaben",
                column: "CalendarEventId",
                principalTable: "CalendarEvents",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Aufgaben_CalendarEvents_CalendarEventId",
                table: "Aufgaben");

            migrationBuilder.DropIndex(
                name: "IX_Aufgaben_CalendarEventId",
                table: "Aufgaben");

            migrationBuilder.DropColumn(
                name: "CalendarEventId",
                table: "Aufgaben");
        }
    }
}
