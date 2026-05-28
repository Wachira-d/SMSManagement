using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMSManagement.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSmsLastStatusQueryAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastStatusQueryAt",
                table: "SmsMessages",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SmsMessages_Status_SentAt",
                table: "SmsMessages",
                columns: new[] { "Status", "SentAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SmsMessages_Status_SentAt",
                table: "SmsMessages");

            migrationBuilder.DropColumn(
                name: "LastStatusQueryAt",
                table: "SmsMessages");
        }
    }
}
