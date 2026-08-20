using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using OpenTv.Core.Storage;

namespace OpenTv.Windows.UI.Services;

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
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("OpenTv.v1.credentials");

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

        try
        {
            var encrypted = Convert.FromBase64String(stored[Prefix.Length..]);

            var plaintext = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plaintext);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // Happens when the file was copied from another machine or user account.
            // Returning null makes the UI ask for the password again rather than
            // sending ciphertext to the provider as if it were the password.
            return null;
        }
    }
}
