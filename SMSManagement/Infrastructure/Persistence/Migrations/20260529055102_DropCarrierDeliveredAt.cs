using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMSManagement.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DropCarrierDeliveredAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CarrierDeliveredAt",
                table: "SmsMessages");

            migrationBuilder.DropColumn(
                name: "CarrierDeliveredAt",
                table: "DnLogs");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CarrierDeliveredAt",
                table: "SmsMessages",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CarrierDeliveredAt",
                table: "DnLogs",
                type: "datetimeoffset",
                nullable: true);
        }
    }
}
