using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMSManagement.Infrastructure.Persistence.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20260520030000_WorkflowLockingAndProjectConstraints")]
    public partial class WorkflowLockingAndProjectConstraints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Per-instance processing lease — stops a tick and a signal (or two
            // overlapping ticks / two app servers) from processing the same
            // workflow instance at once and double-sending.
            migrationBuilder.AddColumn<System.DateTimeOffset>(
                name: "ProcessingLockedUntil",
                table: "WorkflowInstances",
                type: "datetimeoffset",
                nullable: true);

            // RunningNumber was created with DEFAULT 0 purely to satisfy the
            // backfill of pre-existing rows. Leaving the default in place lets
            // any future INSERT that omits the column land on 0 and collide
            // with the unique index. The application always assigns it
            // explicitly, so drop the default now that the backfill is done.
            migrationBuilder.AlterColumn<int>(
                name: "RunningNumber",
                table: "Projects",
                type: "int",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int",
                oldDefaultValue: 0);

            // Promote the redeem-domain index to a filtered UNIQUE index: two
            // projects sharing a redeem domain would make the Host-only
            // project resolution on the redeem path ambiguous.
            migrationBuilder.DropIndex(
                name: "IX_Projects_CouponRedeemDomain",
                table: "Projects");

            migrationBuilder.CreateIndex(
                name: "IX_Projects_CouponRedeemDomain",
                table: "Projects",
                column: "CouponRedeemDomain",
                unique: true,
                filter: "[CouponRedeemDomain] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Projects_CouponRedeemDomain",
                table: "Projects");

            migrationBuilder.CreateIndex(
                name: "IX_Projects_CouponRedeemDomain",
                table: "Projects",
                column: "CouponRedeemDomain");

            migrationBuilder.AlterColumn<int>(
                name: "RunningNumber",
                table: "Projects",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.DropColumn(
                name: "ProcessingLockedUntil",
                table: "WorkflowInstances");
        }
    }
}
