using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace KieshStockExchange.Helpers;

// Best-effort lifecycle load shared by page code-behinds. OnAppearing runs on the
// async-void path, so a load failure must be swallowed + logged, never thrown.
public static class PageLifecycle
{
    public static async Task SafeLoad(string tag, Func<Task> load)
    {
        try { await load(); }
        catch (Exception ex) { Debug.WriteLine($"{tag}: {ex}"); }
    }
}
