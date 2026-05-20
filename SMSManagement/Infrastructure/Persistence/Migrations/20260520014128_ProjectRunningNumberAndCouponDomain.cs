using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMSManagement.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProjectRunningNumberAndCouponDomain : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CouponRedeemDomain",
                table: "Projects",
                type: "nvarchar(253)",
                maxLength: 253,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RunningNumber",
                table: "Projects",
                type: "int",
                nullable: false,
                defaultValue: 0);

            // Backfill existing projects with sequential RunningNumbers before
            // the unique index is created — otherwise every existing row at
            // the default 0 collides. ROW_NUMBER() + correlated UPDATE works
            // on both SQL Server and SQLite 3.25+.
            migrationBuilder.Sql(@"
                WITH ranked AS (
                    SELECT Id, ROW_NUMBER() OVER (ORDER BY CreatedAt) AS rn FROM Projects
                )
                UPDATE Projects
                SET RunningNumber = (SELECT rn FROM ranked WHERE ranked.Id = Projects.Id);");

            migrationBuilder.CreateIndex(
                name: "IX_Projects_CouponRedeemDomain",
                table: "Projects",
                column: "CouponRedeemDomain");

            migrationBuilder.CreateIndex(
                name: "IX_Projects_RunningNumber",
                table: "Projects",
                column: "RunningNumber",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Projects_CouponRedeemDomain",
                table: "Projects");

            migrationBuilder.DropIndex(
                name: "IX_Projects_RunningNumber",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "CouponRedeemDomain",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "RunningNumber",
                table: "Projects");
        }
    }
}
