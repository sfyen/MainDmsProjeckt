using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DmsProjeckt.Migrations
{
    /// <inheritdoc />
    public partial class AddFixCals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "CreatedById",
                table: "CalendarEvents",
                type: "nvarchar(450)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEvents_CreatedById",
                table: "CalendarEvents",
                column: "CreatedById");

            migrationBuilder.AddForeignKey(
                name: "FK_CalendarEvents_AspNetUsers_CreatedById",
                table: "CalendarEvents",
                column: "CreatedById",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CalendarEvents_AspNetUsers_CreatedById",
                table: "CalendarEvents");

            migrationBuilder.DropIndex(
                name: "IX_CalendarEvents_CreatedById",
                table: "CalendarEvents");

            migrationBuilder.AlterColumn<string>(
                name: "CreatedById",
                table: "CalendarEvents",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)");
        }
    }
}
