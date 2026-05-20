using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMSManagement.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ForeignKeysAndScopeFilters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_IngestionBatches_FileHash",
                table: "IngestionBatches");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowInstances_DefinitionId",
                table: "WorkflowInstances",
                column: "DefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowInstances_IngestionBatchId",
                table: "WorkflowInstances",
                column: "IngestionBatchId");

            migrationBuilder.CreateIndex(
                name: "IX_SmsMessages_ProjectId",
                table: "SmsMessages",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_Shortlinks_ProjectId",
                table: "Shortlinks",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_IngestionBatches_ProjectId_FileHash",
                table: "IngestionBatches",
                columns: new[] { "ProjectId", "FileHash" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_CanonicalFieldRules_Projects_ProjectId",
                table: "CanonicalFieldRules",
                column: "ProjectId",
                principalTable: "Projects",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ColumnMappings_Projects_ProjectId",
                table: "ColumnMappings",
                column: "ProjectId",
                principalTable: "Projects",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_IngestionBatches_Projects_ProjectId",
                table: "IngestionBatches",
                column: "ProjectId",
                principalTable: "Projects",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_IngestionSourceSettings_Projects_ProjectId",
                table: "IngestionSourceSettings",
                column: "ProjectId",
                principalTable: "Projects",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PasswordResetTokens_UserCaches_UserCacheId",
                table: "PasswordResetTokens",
                column: "UserCacheId",
                principalTable: "UserCaches",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ProjectMemberships_Projects_ProjectId",
                table: "ProjectMemberships",
                column: "ProjectId",
                principalTable: "Projects",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ProjectMemberships_Users_UserId",
                table: "ProjectMemberships",
                column: "UserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ProjectSmsProviderConfigs_Projects_ProjectId",
                table: "ProjectSmsProviderConfigs",
                column: "ProjectId",
                principalTable: "Projects",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ShortlinkClicks_Shortlinks_ShortlinkId",
                table: "ShortlinkClicks",
                column: "ShortlinkId",
                principalTable: "Shortlinks",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Shortlinks_Projects_ProjectId",
                table: "Shortlinks",
                column: "ProjectId",
                principalTable: "Projects",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Shortlinks_WorkflowInstances_WorkflowInstanceId",
                table: "Shortlinks",
                column: "WorkflowInstanceId",
                principalTable: "WorkflowInstances",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SmsMessages_Projects_ProjectId",
                table: "SmsMessages",
                column: "ProjectId",
                principalTable: "Projects",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SmsMessages_WorkflowInstances_WorkflowInstanceId",
                table: "SmsMessages",
                column: "WorkflowInstanceId",
                principalTable: "WorkflowInstances",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_WorkflowDefinitions_Projects_ProjectId",
                table: "WorkflowDefinitions",
                column: "ProjectId",
                principalTable: "Projects",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_WorkflowInstances_IngestionBatches_IngestionBatchId",
                table: "WorkflowInstances",
                column: "IngestionBatchId",
                principalTable: "IngestionBatches",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_WorkflowInstances_WorkflowDefinitions_DefinitionId",
                table: "WorkflowInstances",
                column: "DefinitionId",
                principalTable: "WorkflowDefinitions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_WorkflowTransitions_WorkflowInstances_InstanceId",
                table: "WorkflowTransitions",
                column: "InstanceId",
                principalTable: "WorkflowInstances",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CanonicalFieldRules_Projects_ProjectId",
                table: "CanonicalFieldRules");

            migrationBuilder.DropForeignKey(
                name: "FK_ColumnMappings_Projects_ProjectId",
                table: "ColumnMappings");

            migrationBuilder.DropForeignKey(
                name: "FK_IngestionBatches_Projects_ProjectId",
                table: "IngestionBatches");

            migrationBuilder.DropForeignKey(
                name: "FK_IngestionSourceSettings_Projects_ProjectId",
                table: "IngestionSourceSettings");

            migrationBuilder.DropForeignKey(
                name: "FK_PasswordResetTokens_UserCaches_UserCacheId",
                table: "PasswordResetTokens");

            migrationBuilder.DropForeignKey(
                name: "FK_ProjectMemberships_Projects_ProjectId",
                table: "ProjectMemberships");

            migrationBuilder.DropForeignKey(
                name: "FK_ProjectMemberships_Users_UserId",
                table: "ProjectMemberships");

            migrationBuilder.DropForeignKey(
                name: "FK_ProjectSmsProviderConfigs_Projects_ProjectId",
                table: "ProjectSmsProviderConfigs");

            migrationBuilder.DropForeignKey(
                name: "FK_ShortlinkClicks_Shortlinks_ShortlinkId",
                table: "ShortlinkClicks");

            migrationBuilder.DropForeignKey(
                name: "FK_Shortlinks_Projects_ProjectId",
                table: "Shortlinks");

            migrationBuilder.DropForeignKey(
                name: "FK_Shortlinks_WorkflowInstances_WorkflowInstanceId",
                table: "Shortlinks");

            migrationBuilder.DropForeignKey(
                name: "FK_SmsMessages_Projects_ProjectId",
                table: "SmsMessages");

            migrationBuilder.DropForeignKey(
                name: "FK_SmsMessages_WorkflowInstances_WorkflowInstanceId",
                table: "SmsMessages");

            migrationBuilder.DropForeignKey(
                name: "FK_WorkflowDefinitions_Projects_ProjectId",
                table: "WorkflowDefinitions");

            migrationBuilder.DropForeignKey(
                name: "FK_WorkflowInstances_IngestionBatches_IngestionBatchId",
                table: "WorkflowInstances");

            migrationBuilder.DropForeignKey(
                name: "FK_WorkflowInstances_WorkflowDefinitions_DefinitionId",
                table: "WorkflowInstances");

            migrationBuilder.DropForeignKey(
                name: "FK_WorkflowTransitions_WorkflowInstances_InstanceId",
                table: "WorkflowTransitions");

            migrationBuilder.DropIndex(
                name: "IX_WorkflowInstances_DefinitionId",
                table: "WorkflowInstances");

            migrationBuilder.DropIndex(
                name: "IX_WorkflowInstances_IngestionBatchId",
                table: "WorkflowInstances");

            migrationBuilder.DropIndex(
                name: "IX_SmsMessages_ProjectId",
                table: "SmsMessages");

            migrationBuilder.DropIndex(
                name: "IX_Shortlinks_ProjectId",
                table: "Shortlinks");

            migrationBuilder.DropIndex(
                name: "IX_IngestionBatches_ProjectId_FileHash",
                table: "IngestionBatches");

            migrationBuilder.CreateIndex(
                name: "IX_IngestionBatches_FileHash",
                table: "IngestionBatches",
                column: "FileHash",
                unique: true);
        }
    }
}
