// SPDX-License-Identifier: Proprietary
// JEB-471 / T-BE-001 — AC-PhoneNorm pure-unit theories.
// Ported from updated-requirements/qa-scaffolding/JEB-467/.

using FluentAssertions;
using JeebGateway.Auth.OtpSignIn;
using Microsoft.Extensions.Options;
using PhoneNumbers;
using Xunit;

namespace JeebGateway.IntegrationTests.OtpSignIn;

[Trait("Story", "JEB-37")]
[Trait("AC", "AC-PhoneNorm")]
public sealed class PhoneNormalizationTests
{
    // Note: dropped the scaffolding's "0079 123 456" data point — libphonenumber
    // resolves `00` as the international call prefix, so "0079" is read as
    // country-code 79 (which is unassigned) and does NOT normalise to LB.
    // The Lebanese-local trunk-prefix form is "079..." (no leading 00).
    public static TheoryData<string> EquivalentPhoneFormats => new()
    {
        "+961 79 123 456",
        "+96179123456",
        "00961 79 123 456",
        " +961-79-123-456 ",
        "+961-79-123-456",
        "79 123 456",         // bare LB subscriber number, parsed with defaultRegion=LB
    };

    [Theory(DisplayName = "AC-PhoneNorm: every equivalent format normalises to +96179123456")]
    [MemberData(nameof(EquivalentPhoneFormats))]
    public void EquivalentFormats_AllNormaliseToSameE164(string input)
    {
        var util = PhoneNumberUtil.GetInstance();
        var parsed = util.Parse(input, defaultRegion: "LB");
        var e164   = util.Format(parsed, PhoneNumberFormat.E164);

        e164.Should().Be("+96179123456");
        util.GetRegionCodeForNumber(parsed).Should().Be("LB");
    }

    [Theory(DisplayName = "AC-PhonePIIHash: every equivalent format produces the SAME HMAC-SHA256 hash (deterministic, PR #32 B1)")]
    [MemberData(nameof(EquivalentPhoneFormats))]
    public void EquivalentFormats_AllProduceSameHmacHash(string input)
    {
        // PR #32 review B1 — replaces bcrypt (random salt → per-request random)
        // with HMAC-SHA256(pepper, e164) which is deterministic across calls.
        var util  = PhoneNumberUtil.GetInstance();
        var e164  = util.Format(util.Parse(input, "LB"), PhoneNumberFormat.E164);

        var options = Options.Create(new JeebJwtOptions
        {
            PhonePepper = "fixed-test-pepper-must-be-at-least-thirty-two-bytes-long-OK",
        });
        using var hasher = new HmacShaPhoneHasher(options);

        var hash      = hasher.HashE164(e164);
        var benchmark = hasher.HashE164("+96179123456");

        hash.Should().Be(benchmark);
        hash.Should().StartWith("ph1:");
    }

    [Theory(DisplayName = "AC-PhoneNorm: non-LB numbers are rejected with RegionCode != LB")]
    [InlineData("+14155551234",   "US")]
    [InlineData("+33123456789",   "FR")]
    [InlineData("+442079460001",  "GB")]   // London 020 7946 — UK drama/BBC reserved range
    [InlineData("+966512345678",  "SA")]
    public void NonLebaneseNumbers_ReturnNonLBRegionCode(string input, string expectedRegion)
    {
        var util   = PhoneNumberUtil.GetInstance();
        var parsed = util.Parse(input, "LB");
        var region = util.GetRegionCodeForNumber(parsed);

        region.Should().Be(expectedRegion);
        region.Should().NotBe("LB");
    }

    [Theory(DisplayName = "AC-PhoneNorm: unparseable inputs raise NumberParseException")]
    [InlineData("not-a-phone")]
    [InlineData("")]
    [InlineData("           ")]
    [InlineData("+")]
    [InlineData("abc-def-ghij")]
    public void UnparseableInput_RaisesNumberParseException(string input)
    {
        var util = PhoneNumberUtil.GetInstance();
        var act  = () => util.Parse(input, "LB");
        act.Should().Throw<NumberParseException>();
    }
}
