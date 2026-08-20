using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using WireTv.Core.Storage;

namespace WireTv.Windows.UI.Services;

/// <summary>
/// Encrypts stored passwords with Windows DPAPI under the current user account.
///
/// This protects sources.json against being read by another user on the machine or
/// copied off it - the ciphertext is useless without the user's Windows credentials.
/// It is not protection against malware already running as that user; nothing a
/// desktop app can do would be.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretProtector : ISecretProtector
{
    /// <summary>Marks values this protector produced, so plaintext from older files is recognised.</summary>
    private const string Prefix = "dpapi:";

    /// <summary>Ties the ciphertext to this app; a value lifted into another app will not decrypt.</summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("WireTv.v1.credentials");

    /// <summary>
    /// Entropy used before the app was renamed. Passwords already on disk were
    /// encrypted with it, so decryption falls back to it and the value is re-encrypted
    /// under the current entropy the next time the profile is saved. Removing this
    /// would silently invalidate every stored credential from an older install.
    /// </summary>
    private static readonly byte[] LegacyEntropy = Encoding.UTF8.GetBytes("OpenTv.v1.credentials");

    public string? Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
            return plaintext;

        try
        {
            var encrypted = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(plaintext), Entropy, DataProtectionScope.CurrentUser);

            return Prefix + Convert.ToBase64String(encrypted);
        }
        catch (CryptographicException)
        {
            // Storing the credential unencrypted beats losing the user's settings.
            return plaintext;
        }
    }

    public string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored) || !stored.StartsWith(Prefix, StringComparison.Ordinal))
            return stored;

        byte[] encrypted;

        try
        {
            encrypted = Convert.FromBase64String(stored[Prefix.Length..]);
        }
        catch (FormatException)
        {
            return null;
        }

        return TryUnprotect(encrypted, Entropy) ?? TryUnprotect(encrypted, LegacyEntropy);
    }

    /// <summary>
    /// Returns null rather than throwing, so the caller can try the legacy entropy.
    /// A null result also reaches the UI as "ask for the password again", which is
    /// what should happen when the file came from another machine or user account -
    /// far better than sending ciphertext to the provider as if it were a password.
    /// </summary>
    private static string? TryUnprotect(byte[] encrypted, byte[] entropy)
    {
        try
        {
            return Encoding.UTF8.GetString(
                ProtectedData.Unprotect(encrypted, entropy, DataProtectionScope.CurrentUser));
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
