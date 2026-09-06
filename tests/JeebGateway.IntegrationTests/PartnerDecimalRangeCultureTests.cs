using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Reflection;
using JeebGateway.Partner;
using Xunit;

namespace JeebGateway.IntegrationTests;

public class PartnerDecimalRangeCultureTests
{
    [Theory]
    [InlineData("en-US")]
    [InlineData("fr-FR")]
    [InlineData("ar-LB")]
    public void Decimal_limits_are_validated_independently_of_host_culture(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var limits = typeof(PartnerWalletOptions).Assembly.GetTypes()
                .Where(type => type.Namespace == typeof(PartnerWalletOptions).Namespace)
                .SelectMany(type => type.GetProperties())
                .SelectMany(property => property.GetCustomAttributes<RangeAttribute>())
                .Where(range => range.OperandType == typeof(decimal))
                .ToArray();
            Assert.Equal(6, limits.Length);
            foreach (var range in limits)
            {
                Assert.True(range.ParseLimitsInInvariantCulture);
                Assert.True(range.IsValid(0.01m));
                Assert.True(range.IsValid(decimal.MaxValue));
                Assert.False(range.IsValid(0m));
                Assert.False(range.IsValid(-1m));
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
