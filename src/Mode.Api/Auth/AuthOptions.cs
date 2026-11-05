using System.ComponentModel.DataAnnotations;

namespace Mode.Api.Auth;

/// <summary>How Mode validates tokens issued by the host application. Bound from the "Auth" section.</summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    [Required] public string Issuer { get; set; } = "";
    [Required] public string Audience { get; set; } = "";

    /// <summary>OpenID Connect discovery document URL; signing keys are read from its jwks_uri.</summary>
    public string? MetadataAddress { get; set; }

    /// <summary>Alternative to <see cref="MetadataAddress"/>: the host's RSA or EC public key in PEM form.</summary>
    public string? PublicKeyPem { get; set; }

    public bool RequireHttpsMetadata { get; set; } = true;

    [Range(0, 300)]
    public int ClockSkewSeconds { get; set; } = 30;

    /// <summary>Tokens issued for longer than this are refused, limiting replay of a stolen token. 0 turns the check off.</summary>
    [Range(0, 24 * 60)]
    public int MaxTokenLifetimeMinutes { get; set; } = 60;

    public HostClaimNames Claims { get; set; } = new();

    /// <summary>Host role name to Mode role (<see cref="ModeRoles"/>). Unmapped host roles are ignored.</summary>
    public Dictionary<string, string> RoleMap { get; set; } = [];

    internal bool HasExactlyOneKeySource() =>
        string.IsNullOrWhiteSpace(MetadataAddress) != string.IsNullOrWhiteSpace(PublicKeyPem);

    internal bool RoleMapIsValid() =>
        RoleMap.Values.All(v => v is ModeRoles.Admin or ModeRoles.Guest);
}

public sealed class HostClaimNames
{
    public string Subject { get; set; } = "sub";
    public string Name { get; set; } = "name";
    public string Email { get; set; } = "email";
    public string Roles { get; set; } = "roles";
}

public static class ModeRoles
{
    public const string Admin = "Admin";
    public const string Guest = "Guest";
}

/// <summary>Claims Mode adds after validating a host token.</summary>
public static class ModeClaims
{
    public const string UserId = "mode:uid";
    public const string DisplayName = "mode:name";
    public const string Email = "mode:email";
    public const string Role = "mode:role";
}
