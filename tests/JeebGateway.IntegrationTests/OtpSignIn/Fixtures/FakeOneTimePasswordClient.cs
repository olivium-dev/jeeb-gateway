// SPDX-License-Identifier: Proprietary
// JEB-471 / T-BE-001 — Ported from updated-requirements/qa-scaffolding/JEB-467/
//   auth-service/AuthService.Tests/Otp/Fixtures/FakeOneTimePasswordClient.cs
//
// Port adjustments (audit #14769 wins):
//   - Namespace: AuthService.Tests.Otp.Fixtures → JeebGateway.IntegrationTests.OtpSignIn.Fixtures
//   - Implements production IServiceOtpClient (not the scaffolding's
//     IFakeOneTimePasswordClient marker interface).
//   - Reuses production SendOtpResult / ValidateOtpResult records.
//
// Invariants preserved verbatim:
//   - 5-minute TTL (300 s)
//   - Hard 3-attempt cap, NO time window
//   - 60-second idempotency window on (phone, purpose)

using System.Collections.Concurrent;
using JeebGateway.Auth.OtpSignIn;

namespace JeebGateway.IntegrationTests.OtpSignIn.Fixtures;

public sealed class FakeOneTimePasswordClient : IServiceOtpClient
{
    private const int OtpTtlSeconds = 300;
    private const int MaxAttempts   = 3;
    private const int IdempotencyS  = 60;

    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, ActiveOtp> _store = new();

    public FakeOneTimePasswordClient(TimeProvider clock) => _clock = clock;

    public Task<SendOtpResult> SendAsync(string normalizedE164Phone, string purpose, CancellationToken ct = default)
    {
        if (!string.Equals(purpose, "login", StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"FakeOneTimePasswordClient only accepts purpose='login' (was '{purpose}').");

        var now = _clock.GetUtcNow();
        var key = $"{normalizedE164Phone}|{purpose}";

        // 60s idempotency window per downstream behavior.
        if (_store.TryGetValue(key, out var existing) &&
            (now - existing.IssuedAt).TotalSeconds < IdempotencyS)
        {
            return Task.FromResult(new SendOtpResult(existing.Code, existing.ExpiresAt, Reused: true));
        }

        // Fresh OTP — also resets the attempt counter per AC4 recovery rule.
        var code = GenerateCode();
        var fresh = new ActiveOtp(code, now, now.AddSeconds(OtpTtlSeconds), 0);
        _store[key] = fresh;
        SendCalls.Add((normalizedE164Phone, purpose, now));
        return Task.FromResult(new SendOtpResult(code, fresh.ExpiresAt, Reused: false));
    }

    public Task<ValidateOtpResult> ValidateAsync(string normalizedE164Phone, string code, CancellationToken ct = default)
    {
        var key = $"{normalizedE164Phone}|login";
        var now = _clock.GetUtcNow();

        if (!_store.TryGetValue(key, out var active))
            return Task.FromResult(ValidateOtpResult.InvalidOtp());

        if (active.Attempts >= MaxAttempts)
            return Task.FromResult(ValidateOtpResult.TooManyAttempts());

        if (now >= active.ExpiresAt)
            return Task.FromResult(ValidateOtpResult.InvalidOtp());

        if (!string.Equals(active.Code, code, StringComparison.Ordinal))
        {
            _store[key] = active with { Attempts = active.Attempts + 1 };
            return Task.FromResult(ValidateOtpResult.InvalidOtp());
        }

        _store.TryRemove(key, out _);
        return Task.FromResult(ValidateOtpResult.Ok());
    }

    public List<(string Phone, string Purpose, DateTimeOffset At)> SendCalls { get; } = new();

    public string? PeekCode(string normalizedE164Phone) =>
        _store.TryGetValue($"{normalizedE164Phone}|login", out var a) ? a.Code : null;

    public void Reset()
    {
        _store.Clear();
        SendCalls.Clear();
    }

    private static string GenerateCode() =>
        Random.Shared.Next(0, 1_000_000).ToString("D6");

    private sealed record ActiveOtp(string Code, DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt, int Attempts);
}
