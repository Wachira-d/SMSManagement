using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMSManagement.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddShortlinkRecipientPhoneHash : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "RecipientPhoneHash",
                table: "Shortlinks",
                type: "varbinary(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Shortlinks_ProjectId_RecipientPhoneHash",
                table: "Shortlinks",
                columns: new[] { "ProjectId", "RecipientPhoneHash" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Shortlinks_ProjectId_RecipientPhoneHash",
                table: "Shortlinks");

            migrationBuilder.DropColumn(
                name: "RecipientPhoneHash",
                table: "Shortlinks");
        }
    }
}
