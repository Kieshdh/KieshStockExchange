namespace KieshStockExchange.Helpers;

// Pure pager-window math shared by the admin (server-side) and portfolio (client-side)
// pagers. Given the 1-based current page and total page count, returns the sorted set of
// page numbers to render: always first + last, plus a ±2 window around the current page.
public static class PagerMath
{
    public static List<int> ComputeVisiblePages(int currentPageDisplay, int totalPages)
    {
        var pages = new HashSet<int>();
        int current = currentPageDisplay;
        int total = totalPages;

        pages.Add(1);
        if (total > 1) pages.Add(total);
        for (int i = current - 2; i <= current + 2; i++)
            if (i > 1 && i < total) pages.Add(i);

        return pages.OrderBy(x => x).ToList();
    }
}
