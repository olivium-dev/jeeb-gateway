using System;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using JeebGateway.Requests.OtpHandover;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// CS-20260813-01 residual — the raw <see cref="IEscalationMirror"/> registration still
/// exists in DI, so nothing but discipline stops a future consumer from injecting the
/// unguarded seam and re-opening the defect. This makes the omission fail CI instead.
///
/// <para>Registering <see cref="FailOpenEscalationMirror"/> AS <c>IEscalationMirror</c>
/// is not an option: the existing tests boot via <c>WebApplicationFactory</c> and call
/// <c>RemoveAll&lt;IEscalationMirror&gt;()</c>, which runs after <c>Program.cs</c> and
/// would strip the guard along with the implementation.</para>
/// </summary>
public class EscalationMirrorGuardBindingTests
{
    [Fact]
    public void NoProductionType_ConstructorInjects_RawEscalationMirror()
    {
        var offenders = typeof(IEscalationMirror).Assembly
            .GetTypes()
            // FailOpenEscalationMirror IS the guard — wrapping the raw seam is its job.
            .Where(t => t != typeof(FailOpenEscalationMirror))
            .SelectMany(t => t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .SelectMany(c => c.GetParameters())
                .Where(p => p.ParameterType == typeof(IEscalationMirror))
                .Select(p => $"{t.FullName}.ctor({p.Name})"))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        offenders.Should().BeEmpty(
            "consumers must inject FailOpenEscalationMirror, not IEscalationMirror: MirrorAsync is not " +
            "async, so a synchronous throw from the raw seam escapes `_ = MirrorAsync(...)` and turned " +
            "the OTP-lockout 423 into a 500 (CS-20260813-01, fixed in b74a8f1). Offenders: {0}",
            string.Join(", ", offenders));
    }
}
