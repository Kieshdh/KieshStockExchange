using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace KieshStockExchange.Helpers;

public class ParsingHelper
{
    public static bool TryToDecimal(object? o, out decimal value)
    {
        value = 0m;
        switch (o)
        {
            case null: return false;
            case decimal d: value = d; return true;
            case double db: value = (decimal)db; return true;
            case float f: value = (decimal)f; return true;
            case int i: value = i; return true;
            case long l: value = l; return true;
            case string s:
                // try current culture first, then invariant
                return decimal.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
                    || decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
            default:
                try { value = Convert.ToDecimal(o, CultureInfo.CurrentCulture); return true; }
                catch { return false; }
        }
    }

    public static bool TryToInt(object? o, out int value)
    {
        value = 0;
        switch (o)
        {
            case null: return false;
            case int i: value = i; return true;
            case long l: value = (int)l; return true;
            case string s:
                return int.TryParse(s, NumberStyles.Integer, CultureInfo.CurrentCulture, out value)
                             || int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
            default:
                try { value = Convert.ToInt32(o, CultureInfo.CurrentCulture); return true; }
                catch { return false; }
        }
    }
}
