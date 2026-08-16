namespace JeebGateway.Users.Moderation;

/// <summary>
/// Splits ban-service's configured moderation message into a machine code and
/// human-safe text.
///
/// <para><b>Phase V D16.</b> ban-service is a generic, tenant-agnostic service: it stores the
/// operator's message VERBATIM and has no locale and no string catalogue. Its shipped
/// <c>config/banning-rule.json</c> therefore holds i18n TEMPLATES, not prose —
/// <c>"message": "Label{{Ban.Label.YOU_ARE_BANNED_FOR_3_DAYS}}"</c>. The
/// <c>Label{{…}}</c> wrapper is precisely a "consumer, substitute this" marker.</para>
///
/// <para>The gateway was handing that template straight to clients as ProblemDetails
/// <c>detail</c> and <c>reason</c>, so run 3's suspended-login 403 shipped
/// <c>Label{{Ban.Label.YOU_ARE_BANNED_FOR_3_DAYS}}</c> verbatim to a screen a suspended user
/// reads. Resolution does NOT belong here: only the client knows the viewer's locale, and the
/// app ships en + ar. So the BFF's job is to (a) never present an unresolved template as prose
/// and (b) hand the client the CODE it can look up. <c>ModerationReason.CodeOf</c> is (b),
/// <c>Humanize</c> is (a).</para>
/// </summary>
public static class ModerationReason
{
    private const string TemplateOpen = "Label{{";
    private const string TemplateClose = "}}";
    private const string AnyPlaceholder = "{{";

    /// <summary>Text used when the configured message is blank or is an unresolved template.</summary>
    public const string Fallback = "Contact support.";

    /// <summary>
    /// The i18n key inside a whole-string <c>Label{{…}}</c> template, else null.
    /// Null for real prose, so a code-carrying client can tell "operator wrote this" from
    /// "policy stage named this".
    /// </summary>
    public static string? CodeOf(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();
        if (!trimmed.StartsWith(TemplateOpen, StringComparison.Ordinal)) return null;
        if (!trimmed.EndsWith(TemplateClose, StringComparison.Ordinal)) return null;
        var code = trimmed[TemplateOpen.Length..^TemplateClose.Length].Trim();
        return code.Length == 0 ? null : code;
    }

    /// <summary>
    /// Operator-facing variant. Same rule, but an unresolved template degrades to its bare
    /// KEY rather than to <see cref="Fallback"/> — an admin triaging a suspension is better
    /// served by <c>Ban.Label.YOU_ARE_BANNED_FOR_3_DAYS</c> than by "Contact support.",
    /// and either beats a broken template.
    /// </summary>
    public static string? ForOperator(string? raw)
    {
        var code = CodeOf(raw);
        if (code is not null) return code;
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();
        return trimmed.Contains(AnyPlaceholder, StringComparison.Ordinal) ? null : trimmed;
    }

    /// <summary>
    /// Text safe to render to a human. Prose passes through UNCHANGED (an operator's typed
    /// reason must still reach the client); anything still carrying <c>{{</c> is an
    /// unsubstituted template and is replaced by <paramref name="fallback"/>.
    /// </summary>
    public static string Humanize(string? raw, string fallback = Fallback)
    {
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        var trimmed = raw.Trim();
        return trimmed.Contains(AnyPlaceholder, StringComparison.Ordinal) ? fallback : trimmed;
    }
}
