using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using SMSManagement.Modules.Identity.Services;

namespace SMSManagement.Infrastructure.Middleware;

/// <summary>
/// Translates <see cref="FeatureDisabledException"/> into a 409 Conflict so
/// controllers / services can <c>guard.EnsureAsync(...)</c> without
/// hand-rolling status-code handling everywhere.
/// </summary>
public sealed class FeatureDisabledFilter : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        if (context.Exception is not FeatureDisabledException ex) return;
        context.Result = new ConflictObjectResult(new
        {
            error = "feature_disabled",
            feature = ex.Feature.ToString(),
            projectId = ex.ProjectId,
            message = ex.Message
        });
        context.ExceptionHandled = true;
    }
}
