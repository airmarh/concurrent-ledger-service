namespace NovaWallet.Domain;

public readonly record struct TransactionHistoryQuery(int Page, int PageSize)
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;

    public static TransactionHistoryQuery Create(int? page, int? pageSize)
    {
        var resolvedPage = page is null or < 1 ? 1 : page.Value;
        var resolvedPageSize = pageSize switch
        {
            null or < 1 => DefaultPageSize,
            > MaxPageSize => MaxPageSize,
            _ => pageSize.Value
        };

        return new TransactionHistoryQuery(resolvedPage, resolvedPageSize);
    }

    public int Skip => (Page - 1) * PageSize;
}
