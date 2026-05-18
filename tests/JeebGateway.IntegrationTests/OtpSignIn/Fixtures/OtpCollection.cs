// SPDX-License-Identifier: Proprietary
// JEB-471 / T-BE-001 — xUnit collection definition for the OTP test suite.
//
// All OtpSignIn tests share a single WebApplicationFactory<Program> so we
// pay the host-build cost once. Per-test isolation is via
// OtpServiceWebAppFactory.ResetState() in InitializeAsync.

using Xunit;

namespace JeebGateway.IntegrationTests.OtpSignIn.Fixtures;

[CollectionDefinition("Otp")]
public sealed class OtpCollection : ICollectionFixture<OtpServiceWebAppFactory>
{
}
