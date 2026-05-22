using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMSManagement.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectCouponCheckEnabled : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CouponCheckEnabled",
                table: "Projects",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CouponCheckEnabled",
                table: "Projects");
        }
    }
}
