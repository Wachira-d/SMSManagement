using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMSManagement.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CanonicalFieldRulesAndBatchRejections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RejectionsJson",
                table: "IngestionBatches",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CanonicalFieldRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CanonicalField = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Required = table.Column<bool>(type: "bit", nullable: false),
                    MinLength = table.Column<int>(type: "int", nullable: true),
                    MaxLength = table.Column<int>(type: "int", nullable: true),
                    StartsWithAny = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    EndsWithAny = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Pattern = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    AllowedValues = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CanonicalFieldRules", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CanonicalFieldRules_ProjectId_CanonicalField",
                table: "CanonicalFieldRules",
                columns: new[] { "ProjectId", "CanonicalField" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CanonicalFieldRules");

            migrationBuilder.DropColumn(
                name: "RejectionsJson",
                table: "IngestionBatches");
        }
    }
}
