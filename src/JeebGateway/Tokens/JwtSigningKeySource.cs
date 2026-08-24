namespace JeebGateway.Tokens;

/// <summary>
/// Resolves an HMAC signing key from a mounted secret file without placing key
/// material in the container environment or Swarm service specification.
/// </summary>
internal static class JwtSigningKeySource
{
    private const long MaximumKeyFileBytes = 4096;

    internal static string Resolve(string? inlineValue, string? filePath, string settingName)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return inlineValue ?? string.Empty;
        }

        if (!Path.IsPathFullyQualified(filePath))
        {
            throw new InvalidOperationException(
                $"{settingName} must be an absolute mounted-secret path.");
        }

        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists || info.Length is < 1 or > MaximumKeyFileBytes)
            {
                throw new InvalidOperationException(
                    $"{settingName} must reference a readable "
                    + $"1..{MaximumKeyFileBytes}-byte secret file.");
            }

            var value = File.ReadAllText(filePath).Trim();
            if (value.Length == 0)
            {
                throw new InvalidOperationException($"{settingName} contains no key material.");
            }
            return value;
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"{settingName} could not be read.", ex);
        }
    }
}
