using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMSManagement.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProjectFeatureFlags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Slug",
                table: "Shortlinks",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)");

            migrationBuilder.AddColumn<bool>(
                name: "EmailAlertsEnabled",
                table: "Projects",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "IngestionEnabled",
                table: "Projects",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "ShortlinkAlphabet",
                table: "Projects",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ShortlinkEnabled",
                table: "Projects",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "SmsEnabled",
                table: "Projects",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "WorkflowEnabled",
                table: "Projects",
                type: "bit",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EmailAlertsEnabled",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "IngestionEnabled",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "ShortlinkAlphabet",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "ShortlinkEnabled",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "SmsEnabled",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "WorkflowEnabled",
                table: "Projects");

            migrationBuilder.AlterColumn<string>(
                name: "Slug",
                table: "Shortlinks",
                type: "nvarchar(450)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(64)",
                oldMaxLength: 64);
        }
    }
}
