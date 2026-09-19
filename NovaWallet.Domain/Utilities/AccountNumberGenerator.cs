using System.Security.Cryptography;

namespace NovaWallet.Domain;

public static class AccountNumberGenerator
{
    private const int Length = 10;

    public static string Generate()
    {
        Span<char> digits = stackalloc char[Length];
        for (var i = 0; i < Length; i++)
            digits[i] = (char)('0' + RandomNumberGenerator.GetInt32(10));

        return new string(digits);
    }
}
