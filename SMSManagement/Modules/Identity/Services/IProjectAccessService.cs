using SMSManagement.Modules.Identity.Domain;

namespace SMSManagement.Modules.Identity.Services;

public interface IProjectAccessService
{
    Task<bool> CanAsync(Guid projectId, ProjectAccessLevel required, CancellationToken ct = default);
    Task EnsureAsync(Guid projectId, ProjectAccessLevel required, CancellationToken ct = default);

    Task<ProjectMembership> ShareAsync(
        Guid projectId, Guid targetUserId, ProjectAccessLevel level, CancellationToken ct = default);

    Task RevokeAsync(Guid projectId, Guid targetUserId, CancellationToken ct = default);

    Task TransferOwnershipAsync(Guid projectId, Guid newOwnerUserId, CancellationToken ct = default);
}
