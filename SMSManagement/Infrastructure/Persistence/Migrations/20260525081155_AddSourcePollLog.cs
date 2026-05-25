using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMSManagement.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSourcePollLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SourcePollLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PolledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    BatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Message = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourcePollLogs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SourcePollLogs_ProjectId_PolledAt",
                table: "SourcePollLogs",
                columns: new[] { "ProjectId", "PolledAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SourcePollLogs_SourceId",
                table: "SourcePollLogs",
                column: "SourceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SourcePollLogs");
        }
    }
}
