using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DmsProjeckt.Migrations
{
    /// <inheritdoc />
    public partial class FixBuilder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CalendarEventParticipants_CalendarEvents_CalendarEventId",
                table: "CalendarEventParticipants");

            migrationBuilder.AddForeignKey(
                name: "FK_CalendarEventParticipants_CalendarEvents_CalendarEventId",
                table: "CalendarEventParticipants",
                column: "CalendarEventId",
                principalTable: "CalendarEvents",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CalendarEventParticipants_CalendarEvents_CalendarEventId",
                table: "CalendarEventParticipants");

            migrationBuilder.AddForeignKey(
                name: "FK_CalendarEventParticipants_CalendarEvents_CalendarEventId",
                table: "CalendarEventParticipants",
                column: "CalendarEventId",
                principalTable: "CalendarEvents",
                principalColumn: "Id");
        }
    }
}
