using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DmsProjeckt.Migrations
{
    /// <inheritdoc />
    public partial class AddFixedPap : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CalendarEventParticipants",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CalendarEventId = table.Column<int>(type: "int", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    RespondedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalendarEventParticipants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CalendarEventParticipants_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_CalendarEventParticipants_CalendarEvents_CalendarEventId",
                        column: x => x.CalendarEventId,
                        principalTable: "CalendarEvents",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEventParticipants_CalendarEventId",
                table: "CalendarEventParticipants",
                column: "CalendarEventId");

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEventParticipants_UserId",
                table: "CalendarEventParticipants",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CalendarEventParticipants");
        }
    }
}
