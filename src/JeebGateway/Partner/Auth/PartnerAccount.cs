using System;

namespace JeebGateway.Partner.Auth;

/// <summary>
/// A verified partner identity resolved from a correct login credential. Carries only non-secret
/// fields: the wallet holder id (== user-management userId) and the display basics the portal shows
/// after sign-in. Never carries the secret or its hash.
/// </summary>
/// <param name="HolderId">The partner's user-management userId, which IS the wallet holder id.</param>
/// <param name="Login">The login handle that was verified (echoed back as a profile basic).</param>
/// <param name="DisplayName">Human display name for the signed-in partner.</param>
public sealed record PartnerAccount(Guid HolderId, string Login, string DisplayName);
