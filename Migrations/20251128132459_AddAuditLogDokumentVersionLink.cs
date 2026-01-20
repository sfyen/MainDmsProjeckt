using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DmsProjeckt.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditLogDokumentVersionLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AuditLogDokumente_DokumentVersionen_DokumentId",
                table: "AuditLogDokumente");

            migrationBuilder.AddColumn<Guid>(
                name: "DokumentVersionId",
                table: "AuditLogDokumente",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogDokumente_DokumentVersionId",
                table: "AuditLogDokumente",
                column: "DokumentVersionId");

            migrationBuilder.AddForeignKey(
                name: "FK_AuditLogDokumente_DokumentVersionen_DokumentVersionId",
                table: "AuditLogDokumente",
                column: "DokumentVersionId",
                principalTable: "DokumentVersionen",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AuditLogDokumente_DokumentVersionen_DokumentVersionId",
                table: "AuditLogDokumente");

            migrationBuilder.DropIndex(
                name: "IX_AuditLogDokumente_DokumentVersionId",
                table: "AuditLogDokumente");

            migrationBuilder.DropColumn(
                name: "DokumentVersionId",
                table: "AuditLogDokumente");

            migrationBuilder.AddForeignKey(
                name: "FK_AuditLogDokumente_DokumentVersionen_DokumentId",
                table: "AuditLogDokumente",
                column: "DokumentId",
                principalTable: "DokumentVersionen",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
