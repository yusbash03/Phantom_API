namespace PHANTOM.Application.Common;

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

public sealed record PageRequest(int Page = 1, int PageSize = 25)
{
    public const int MaxPageSize = 100;

    /// <summary>Far beyond any real list; stops (page - 1) * size overflowing into a negative OFFSET.</summary>
    public const int MaxPage = 100_000;

    public int SafePage => Math.Clamp(Page, 1, MaxPage);
    public int SafePageSize => Math.Clamp(PageSize, 1, MaxPageSize);
    public int Skip => (SafePage - 1) * SafePageSize;
}
