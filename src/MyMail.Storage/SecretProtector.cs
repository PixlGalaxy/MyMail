using System.Security.Cryptography;
using System.Text;

namespace MyMail.Storage;

/// <summary>Encrypts secrets (passwords, token caches) for the current Windows user with DPAPI.</summary>
public static class SecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MyMail.v1");

    public static byte[] Protect(byte[] data) =>
        ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);

    public static byte[] Unprotect(byte[] data) =>
        ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);

    public static byte[] ProtectString(string value) => Protect(Encoding.UTF8.GetBytes(value));

    public static string UnprotectString(byte[] data) => Encoding.UTF8.GetString(Unprotect(data));
}
