using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMSManagement.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ColumnMappings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceColumn = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    CanonicalField = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TransformChainJson = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ColumnMappings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IngestionBatches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SourceRef = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    FileHash = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    TotalRows = table.Column<int>(type: "int", nullable: false),
                    AcceptedRows = table.Column<int>(type: "int", nullable: false),
                    RejectedRows = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IngestedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    IngestedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IngestionBatches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IngestionSourceSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceType = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    EncryptedConfig = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    ArchiveDirectory = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RejectedDirectory = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Action = table.Column<int>(type: "int", nullable: false),
                    DuplicatePolicy = table.Column<int>(type: "int", nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    PollingSchedule = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IngestionSourceSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LoginAudits",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Username = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Success = table.Column<bool>(type: "bit", nullable: false),
                    AuthSource = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    FailureReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IpAddress = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    UserAgent = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoginAudits", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProjectMemberships",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AccessLevel = table.Column<int>(type: "int", nullable: false),
                    GrantedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    GrantedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectMemberships", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Projects",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DefaultProvider = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Projects", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RefreshTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Username = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    TokenHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RevokedReason = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CreatedFromIp = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RefreshTokens", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ShortlinkClicks",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ShortlinkId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IpHash = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    UserAgent = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DeviceClass = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Country = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ClickedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShortlinkClicks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Shortlinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkflowInstanceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Slug = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    EncryptedTargetUrl = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    MaxClicks = table.Column<int>(type: "int", nullable: true),
                    ClickCount = table.Column<int>(type: "int", nullable: false),
                    Disabled = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Shortlinks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SmsMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkflowInstanceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DedupKey = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ProviderMessageId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    MaskedTo = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    EncryptedTo = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    EncryptedBody = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    Attempts = table.Column<short>(type: "smallint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ScheduledFor = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SentAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DeliveredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ErrorCode = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SmsMessages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserCaches",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Username = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    PasswordHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Salt = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Email = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Department = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    EmployeeId = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    GroupsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    IsLocked = table.Column<bool>(type: "bit", nullable: false),
                    FailedAttempts = table.Column<int>(type: "int", nullable: false),
                    LastFailedLogin = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastLogin = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastAdSync = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CacheExpires = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserCaches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExternalSubject = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Email = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastLoginAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowDefinitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    Active = table.Column<bool>(type: "bit", nullable: false),
                    DefinitionJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowDefinitions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowInstances",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DefinitionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IngestionBatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    State = table.Column<int>(type: "int", nullable: false),
                    CurrentStep = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    StepRepeatCount = table.Column<int>(type: "int", nullable: false),
                    MaskedPhone = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    EncryptedPayload = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    NextCheckAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowInstances", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowTransitions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    InstanceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FromState = table.Column<int>(type: "int", nullable: false),
                    ToState = table.Column<int>(type: "int", nullable: false),
                    Trigger = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DataJson = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowTransitions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ColumnMappings_ProjectId_SourceColumn",
                table: "ColumnMappings",
                columns: new[] { "ProjectId", "SourceColumn" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IngestionBatches_FileHash",
                table: "IngestionBatches",
                column: "FileHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IngestionBatches_ProjectId_IngestedAt",
                table: "IngestionBatches",
                columns: new[] { "ProjectId", "IngestedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_IngestionSourceSettings_ProjectId_SourceType",
                table: "IngestionSourceSettings",
                columns: new[] { "ProjectId", "SourceType" });

            migrationBuilder.CreateIndex(
                name: "IX_LoginAudits_CreatedAt",
                table: "LoginAudits",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_LoginAudits_Success_CreatedAt",
                table: "LoginAudits",
                columns: new[] { "Success", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_LoginAudits_Username_CreatedAt",
                table: "LoginAudits",
                columns: new[] { "Username", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectMemberships_ProjectId_UserId",
                table: "ProjectMemberships",
                columns: new[] { "ProjectId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProjectMemberships_UserId",
                table: "ProjectMemberships",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Projects_Code",
                table: "Projects",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RefreshTokens_TokenHash",
                table: "RefreshTokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RefreshTokens_Username_ExpiresAt",
                table: "RefreshTokens",
                columns: new[] { "Username", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ShortlinkClicks_ShortlinkId_ClickedAt",
                table: "ShortlinkClicks",
                columns: new[] { "ShortlinkId", "ClickedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Shortlinks_Slug",
                table: "Shortlinks",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Shortlinks_WorkflowInstanceId",
                table: "Shortlinks",
                column: "WorkflowInstanceId");

            migrationBuilder.CreateIndex(
                name: "IX_SmsMessages_DedupKey",
                table: "SmsMessages",
                column: "DedupKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SmsMessages_Status_ScheduledFor",
                table: "SmsMessages",
                columns: new[] { "Status", "ScheduledFor" });

            migrationBuilder.CreateIndex(
                name: "IX_SmsMessages_WorkflowInstanceId",
                table: "SmsMessages",
                column: "WorkflowInstanceId");

            migrationBuilder.CreateIndex(
                name: "IX_UserCaches_CacheExpires",
                table: "UserCaches",
                column: "CacheExpires");

            migrationBuilder.CreateIndex(
                name: "IX_UserCaches_Username",
                table: "UserCaches",
                column: "Username",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_Email",
                table: "Users",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_ExternalSubject",
                table: "Users",
                column: "ExternalSubject",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowDefinitions_ProjectId_Name_Version",
                table: "WorkflowDefinitions",
                columns: new[] { "ProjectId", "Name", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowInstances_ExpiresAt",
                table: "WorkflowInstances",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowInstances_State_NextCheckAt",
                table: "WorkflowInstances",
                columns: new[] { "State", "NextCheckAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowTransitions_InstanceId_At",
                table: "WorkflowTransitions",
                columns: new[] { "InstanceId", "At" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ColumnMappings");

            migrationBuilder.DropTable(
                name: "IngestionBatches");

            migrationBuilder.DropTable(
                name: "IngestionSourceSettings");

            migrationBuilder.DropTable(
                name: "LoginAudits");

            migrationBuilder.DropTable(
                name: "ProjectMemberships");

            migrationBuilder.DropTable(
                name: "Projects");

            migrationBuilder.DropTable(
                name: "RefreshTokens");

            migrationBuilder.DropTable(
                name: "ShortlinkClicks");

            migrationBuilder.DropTable(
                name: "Shortlinks");

            migrationBuilder.DropTable(
                name: "SmsMessages");

            migrationBuilder.DropTable(
                name: "UserCaches");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropTable(
                name: "WorkflowDefinitions");

            migrationBuilder.DropTable(
                name: "WorkflowInstances");

            migrationBuilder.DropTable(
                name: "WorkflowTransitions");
        }
    }
}
