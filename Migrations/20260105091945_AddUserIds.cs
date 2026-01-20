using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DmsProjeckt.Migrations
{
    /// <inheritdoc />
    public partial class AddUserIds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "UserIds",
                table: "Steps",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UserIds",
                table: "Steps");
        }
    }
}
