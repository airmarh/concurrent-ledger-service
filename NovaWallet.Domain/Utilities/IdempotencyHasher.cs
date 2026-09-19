using System.Security.Cryptography;
using System.Text;

namespace NovaWallet.Domain;

public static class IdempotencyHasher
{
    public static string ComputeHash(string canonicalPayload)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalPayload));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
