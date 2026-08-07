using System.Reflection;
using JeebGateway.Auth.OtpSignIn;
using JeebGateway.Controllers;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ApplicationParts;

namespace JeebGateway.Infrastructure;

/// <summary>
/// Production route allow-list for the essential back-office plus the existing
/// mobile authentication surfaces. Nonessential legacy controllers remain
/// available only in Development/Testing and cannot accidentally activate a
/// process-local store in production.
/// </summary>
internal sealed class EssentialGatewayControllerFeatureProvider
    : IApplicationFeatureProvider<ControllerFeature>
{
    private static readonly HashSet<Type> Allowed = new()
    {
        typeof(AdminAuthController),
        typeof(AdminOidcAuthController),
        typeof(AdminSessionController),
        typeof(AdminCasesController),
        typeof(AdminDeliveriesController),
        typeof(AdminSettlementsController),
        typeof(CaseEventCallbacksController),
        typeof(SettlementEventCallbacksController),
        typeof(AuthController),
        typeof(TokensController),
        typeof(OtpController),
        typeof(AuthOtpController),
        typeof(AuthRefreshV1Controller),
        typeof(AuthEmailFacadeController),
        typeof(UsersMeController),
        typeof(HealthController),
    };

    public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
    {
        for (var index = feature.Controllers.Count - 1; index >= 0; index--)
        {
            TypeInfo controller = feature.Controllers[index];
            if (!Allowed.Contains(controller.AsType())) feature.Controllers.RemoveAt(index);
        }
    }
}
