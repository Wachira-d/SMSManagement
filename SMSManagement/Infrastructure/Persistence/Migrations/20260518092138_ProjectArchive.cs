using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMSManagement.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProjectArchive : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ArchivedAt",
                table: "Projects",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ArchivedByUserId",
                table: "Projects",
                type: "uniqueidentifier",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ArchivedAt",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "ArchivedByUserId",
                table: "Projects");
        }
    }
}
