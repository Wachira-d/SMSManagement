using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMSManagement.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOutboundEmailTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EmailWebhookLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Event = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    RelayMessageId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    OutboundEmailId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Outcome = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    RemoteIp = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    RawPayload = table.Column<string>(type: "nvarchar(max)", maxLength: 4096, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailWebhookLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OutboundEmails",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Category = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Recipients = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    AttachmentCount = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    StatusSource = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    StatusUpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RelayMessageId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ProviderMessageId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    LastError = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    SentAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastPolledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboundEmails", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EmailWebhookLogs_CreatedAt",
                table: "EmailWebhookLogs",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_EmailWebhookLogs_RelayMessageId",
                table: "EmailWebhookLogs",
                column: "RelayMessageId");

            migrationBuilder.CreateIndex(
                name: "IX_OutboundEmails_CreatedAt",
                table: "OutboundEmails",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_OutboundEmails_IdempotencyKey",
                table: "OutboundEmails",
                column: "IdempotencyKey",
                unique: true,
                filter: "[IdempotencyKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_OutboundEmails_Provider_Status",
                table: "OutboundEmails",
                columns: new[] { "Provider", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_OutboundEmails_RelayMessageId",
                table: "OutboundEmails",
                column: "RelayMessageId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EmailWebhookLogs");

            migrationBuilder.DropTable(
                name: "OutboundEmails");
        }
    }
}
