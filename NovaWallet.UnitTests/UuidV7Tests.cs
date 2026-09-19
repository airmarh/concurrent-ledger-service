using NovaWallet.Domain;

namespace NovaWallet.UnitTests;

public class UuidV7Tests
{
    [Fact]
    public void NewId_SetsVersion7AndRfc4122Variant()
    {
        var id = UuidV7.NewId();
        var bytes = id.ToByteArray(bigEndian: true);

        Assert.Equal(0x7, bytes[6] >> 4);
        Assert.Equal(0x2, bytes[8] >> 6);
    }

    [Fact]
    public void NewId_ProducesUniqueValues()
    {
        var ids = Enumerable.Range(0, 1_000).Select(_ => UuidV7.NewId()).ToList();

        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void NewId_SortsAscendingWithGenerationOrder()
    {
        var first = UuidV7.NewId();
        Thread.Sleep(5);
        var second = UuidV7.NewId();

        // Raw byte comparison, not Guid equality — this is the ordering Postgres
        // actually uses for a uuid btree, and the property that matters for index locality.
        Assert.True(CompareBigEndian(first, second) < 0);
    }

    private static int CompareBigEndian(Guid a, Guid b)
    {
        var aBytes = a.ToByteArray(bigEndian: true);
        var bBytes = b.ToByteArray(bigEndian: true);
        for (var i = 0; i < aBytes.Length; i++)
        {
            var diff = aBytes[i].CompareTo(bBytes[i]);
            if (diff != 0) return diff;
        }
        return 0;
    }
}
