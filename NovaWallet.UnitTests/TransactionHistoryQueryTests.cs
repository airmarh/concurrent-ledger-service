using NovaWallet.Domain;

namespace NovaWallet.UnitTests;

public class TransactionHistoryQueryTests
{
    [Fact]
    public void Create_WithNoParameters_DefaultsToFirstPageOfDefaultSize()
    {
        var query = TransactionHistoryQuery.Create(null, null);

        Assert.Equal(1, query.Page);
        Assert.Equal(TransactionHistoryQuery.DefaultPageSize, query.PageSize);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Create_WithNonPositivePage_ClampsToFirstPage(int page)
    {
        var query = TransactionHistoryQuery.Create(page, null);

        Assert.Equal(1, query.Page);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Create_WithNonPositivePageSize_FallsBackToDefaultPageSize(int pageSize)
    {
        var query = TransactionHistoryQuery.Create(1, pageSize);

        Assert.Equal(TransactionHistoryQuery.DefaultPageSize, query.PageSize);
    }

    [Fact]
    public void Create_WithPageSizeAboveMax_ClampsToMaxPageSize()
    {
        var query = TransactionHistoryQuery.Create(1, TransactionHistoryQuery.MaxPageSize + 500);

        Assert.Equal(TransactionHistoryQuery.MaxPageSize, query.PageSize);
    }

    [Theory]
    [InlineData(1, 20, 0)]
    [InlineData(2, 20, 20)]
    [InlineData(3, 10, 20)]
    public void Skip_ComputesZeroBasedOffsetFromPageAndPageSize(int page, int pageSize, int expectedSkip)
    {
        var query = TransactionHistoryQuery.Create(page, pageSize);

        Assert.Equal(expectedSkip, query.Skip);
    }
}
