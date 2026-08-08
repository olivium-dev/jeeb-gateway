namespace JeebGateway.Services.Clients;

/// <summary>Binds Services:RoleService. ApiKey is committed BLANK, injected at deploy.</summary>
public sealed class RoleServiceOptions
{
    public const string SectionName = "Services:RoleService";

    public string? BaseUrl { get; set; }

    public string? ApiKey { get; set; }
}
