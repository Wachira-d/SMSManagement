using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMSManagement.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ChangeIngestNotifyDefaults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Switch every existing project to "one email per round, sent
            // when DN has settled" — silence the per-batch ingest success /
            // partial alerts that fire before the SMS is dispatched. Failure
            // alerts stay on (the round summary never fires when there's
            // nothing to summarise, so this is the only path for parse
            // failures). Operators can flip these back per-project in the UI
            // if they want the verbose stream back.
            migrationBuilder.Sql(@"
                UPDATE [Projects]
                SET    [NotifyOnIngestSuccess] = 0,
                       [NotifyOnIngestPartial] = 0
                WHERE  [NotifyOnIngestSuccess] = 1
                   OR  [NotifyOnIngestPartial] = 1;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                UPDATE [Projects]
                SET    [NotifyOnIngestSuccess] = 1,
                       [NotifyOnIngestPartial] = 1;
            ");
        }
    }
}
