using NovaWallet.Domain;

namespace NovaWallet.UnitTests;

public class IdempotencyHasherTests
{
    [Fact]
    public void ComputeHash_SamePayload_ProducesSameHash()
    {
        const string payload = """{"FromWalletId":"a","ToWalletId":"b","AmountKobo":1000}""";

        var hash1 = IdempotencyHasher.ComputeHash(payload);
        var hash2 = IdempotencyHasher.ComputeHash(payload);

        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void ComputeHash_DifferentPayload_ProducesDifferentHash()
    {
        var hash1 = IdempotencyHasher.ComputeHash("""{"AmountKobo":1000}""");
        var hash2 = IdempotencyHasher.ComputeHash("""{"AmountKobo":2000}""");

        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void ComputeHash_IsLowercaseHex()
    {
        var hash = IdempotencyHasher.ComputeHash("payload");

        Assert.Matches("^[0-9a-f]{64}$", hash);
    }
}
