namespace OpenTv.Core.Storage;

/// <summary>
/// Encrypts values that must not sit in plaintext on disk - currently Xtream
/// subscription passwords.
///
/// The abstraction lives in Core so the storage layer stays platform-neutral;
/// each platform head supplies its own implementation (DPAPI on Windows, the
/// Keystore on Android, the Keychain on iOS).
/// </summary>
public interface ISecretProtector
{
    /// <summary>Encrypts a value for storage. Null and empty pass through unchanged.</summary>
    string? Protect(string? plaintext);

    /// <summary>
    /// Reverses <see cref="Protect"/>. Must return the input unchanged when it was
    /// not produced by this protector, so that settings written before encryption
    /// was introduced - or copied from another machine - still load.
    /// </summary>
    string? Unprotect(string? stored);
}

/// <summary>
/// Fallback that stores values as-is. Used on platforms with no protector wired up
/// yet, and in tests. Named explicitly so that "no encryption" is always a visible
/// decision rather than a silent default.
/// </summary>
public sealed class PlaintextSecretProtector : ISecretProtector
{
    public static readonly PlaintextSecretProtector Instance = new();

    public string? Protect(string? plaintext) => plaintext;

    public string? Unprotect(string? stored) => stored;
}
