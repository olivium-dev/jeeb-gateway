namespace JeebGateway.Push;

/// <summary>
/// One push-capable device registered against a user. Production wiring stores
/// these per-user in Postgres (db/migrations follow-up) with a unique index on
/// the (Token, Platform) pair; the in-memory implementation suffices for the
/// MVP and integration tests.
/// </summary>
public sealed record DeviceToken(string UserId, DevicePlatform Platform, string Token);
