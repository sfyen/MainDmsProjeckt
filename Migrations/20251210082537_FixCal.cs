using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DmsProjeckt.Migrations
{
    /// <inheritdoc />
    public partial class FixCal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Hour",
                table: "CalendarEvents");

            migrationBuilder.AddColumn<TimeSpan>(
                name: "EndTime",
                table: "CalendarEvents",
                type: "time",
                nullable: true);

            migrationBuilder.AddColumn<TimeSpan>(
                name: "StartTime",
                table: "CalendarEvents",
                type: "time",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EndTime",
                table: "CalendarEvents");

            migrationBuilder.DropColumn(
                name: "StartTime",
                table: "CalendarEvents");

            migrationBuilder.AddColumn<int>(
                name: "Hour",
                table: "CalendarEvents",
                type: "int",
                nullable: true);
        }
    }
}
