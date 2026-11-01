namespace Mode.Domain;

/// <summary>Host application user ids are opaque strings; Mode never owns user records.</summary>
public static class UserIds
{
    public const int MaxLength = 64;
}
