using System.Text.RegularExpressions;

namespace JeebGateway.Cms;

/// <summary>
/// Outcome of evaluating the <c>X-Step-Up-Totp</c> header for a PUBLISH.
/// Maps directly to the §4 contract state machine:
/// <list type="bullet">
///   <item><see cref="Required"/> → 401 <c>urn:jeeb:error:step_up_required</c>
///     (header missing or not exactly six digits).</item>
///   <item><see cref="Invalid"/> → 403 <c>urn:jeeb:error:step_up_invalid</c>
///     (six digits but not the valid code).</item>
///   <item><see cref="Valid"/> → proceed to publish (200).</item>
/// </list>
/// </summary>
public enum CmsStepUpResult
{
    Required,
    Invalid,
    Valid,
}

/// <summary>
/// Validates the CMS publish step-up TOTP per §4 of the contract. The valid
/// dev code is the documented mock constant <c>424242</c> — NOT a secret; it
/// is fixed in the mock and hardcoded by E2E suites. The order of checks is
/// load-bearing: shape (six digits) is evaluated before value, so a malformed
/// header yields 401 (re-prompt for step-up) while a well-formed-but-wrong
/// code yields 403 (step-up attempted and rejected).
/// </summary>
public static class CmsStepUpValidator
{
    /// <summary>Documented mock dev TOTP. Replaced by a real TOTP verifier upstream.</summary>
    public const string DevStepUpCode = "424242";

    public const string StepUpHeaderName = "X-Step-Up-Totp";

    private static readonly Regex SixDigits =
        new(@"^\d{6}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static CmsStepUpResult Evaluate(string? totpHeader)
    {
        if (string.IsNullOrWhiteSpace(totpHeader) || !SixDigits.IsMatch(totpHeader))
        {
            return CmsStepUpResult.Required;
        }

        return string.Equals(totpHeader, DevStepUpCode, StringComparison.Ordinal)
            ? CmsStepUpResult.Valid
            : CmsStepUpResult.Invalid;
    }
}
