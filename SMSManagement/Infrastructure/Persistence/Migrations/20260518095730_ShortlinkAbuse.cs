using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMSManagement.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ShortlinkAbuse : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<short>(
                name: "ShortlinkSlugLength",
                table: "Projects",
                type: "smallint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BlockedIps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IpHash = table.Column<byte[]>(type: "varbinary(900)", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    FailureCount = table.Column<int>(type: "int", nullable: false),
                    FirstFailureAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastFailureAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    BlockedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    BlockedUntil = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UnblockedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UnblockedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UnblockReason = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BlockedIps", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IpAccessFailures",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    IpHash = table.Column<byte[]>(type: "varbinary(900)", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Slug = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IpAccessFailures", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BlockedIps_BlockedUntil",
                table: "BlockedIps",
                column: "BlockedUntil");

            migrationBuilder.CreateIndex(
                name: "IX_BlockedIps_IpHash_BlockedUntil",
                table: "BlockedIps",
                columns: new[] { "IpHash", "BlockedUntil" });

            migrationBuilder.CreateIndex(
                name: "IX_IpAccessFailures_IpHash_OccurredAt",
                table: "IpAccessFailures",
                columns: new[] { "IpHash", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_IpAccessFailures_OccurredAt",
                table: "IpAccessFailures",
                column: "OccurredAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BlockedIps");

            migrationBuilder.DropTable(
                name: "IpAccessFailures");

            migrationBuilder.DropColumn(
                name: "ShortlinkSlugLength",
                table: "Projects");
        }
    }
}
