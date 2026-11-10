namespace NeoPrint.Application.Common;

/// <summary>
/// A page of a customer's own list (T-413): the app asks for the newest
/// <c>pageSize</c> items and the next page as the list scrolls. Without a
/// page the list is its newest 100, as before.
/// </summary>
public static class ListPaging
{
    /// <summary>The largest page, and the size of the list asked for without one.</summary>
    public const int MaxSize = 100;

    /// <summary>The items to skip and to take for a 1-based <paramref name="page"/>.</summary>
    public static (int Skip, int Take) Of(int page, int pageSize)
    {
        var take = Math.Clamp(pageSize, 1, MaxSize);
        var skip = (Math.Max(1, page) - 1) * take;
        return (skip, take);
    }
}
