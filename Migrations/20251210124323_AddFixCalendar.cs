using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DmsProjeckt.Migrations
{
    /// <inheritdoc />
    public partial class AddFixCalendar : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "Date",
                table: "CalendarEvents",
                newName: "StartDate");

            migrationBuilder.AlterColumn<string>(
                name: "UserId",
                table: "CalendarEvents",
                type: "nvarchar(450)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.AlterColumn<DateTime>(
                name: "StartTime",
                table: "CalendarEvents",
                type: "datetime2",
                nullable: true,
                oldClrType: typeof(TimeSpan),
                oldType: "time",
                oldNullable: true);

            migrationBuilder.AlterColumn<DateTime>(
                name: "EndTime",
                table: "CalendarEvents",
                type: "datetime2",
                nullable: true,
                oldClrType: typeof(TimeSpan),
                oldType: "time",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Color",
                table: "CalendarEvents",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AllDay",
                table: "CalendarEvents",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "EndDate",
                table: "CalendarEvents",
                type: "datetime2",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<string>(
                name: "EventType",
                table: "CalendarEvents",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEvents_UserId",
                table: "CalendarEvents",
                column: "UserId");

            migrationBuilder.AddForeignKey(
                name: "FK_CalendarEvents_AspNetUsers_UserId",
                table: "CalendarEvents",
                column: "UserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CalendarEvents_AspNetUsers_UserId",
                table: "CalendarEvents");

            migrationBuilder.DropIndex(
                name: "IX_CalendarEvents_UserId",
                table: "CalendarEvents");

            migrationBuilder.DropColumn(
                name: "AllDay",
                table: "CalendarEvents");

            migrationBuilder.DropColumn(
                name: "EndDate",
                table: "CalendarEvents");

            migrationBuilder.DropColumn(
                name: "EventType",
                table: "CalendarEvents");

            migrationBuilder.RenameColumn(
                name: "StartDate",
                table: "CalendarEvents",
                newName: "Date");

            migrationBuilder.AlterColumn<string>(
                name: "UserId",
                table: "CalendarEvents",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)");

            migrationBuilder.AlterColumn<TimeSpan>(
                name: "StartTime",
                table: "CalendarEvents",
                type: "time",
                nullable: true,
                oldClrType: typeof(DateTime),
                oldType: "datetime2",
                oldNullable: true);

            migrationBuilder.AlterColumn<TimeSpan>(
                name: "EndTime",
                table: "CalendarEvents",
                type: "time",
                nullable: true,
                oldClrType: typeof(DateTime),
                oldType: "datetime2",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Color",
                table: "CalendarEvents",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");
        }
    }
}
