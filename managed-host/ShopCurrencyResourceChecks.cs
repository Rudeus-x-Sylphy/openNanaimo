using System.Globalization;
using OpenNanaimo.Adapter.Services;

// Exact-client loader oracles, independent of ShopCatalog's price-column choices.
// Evidence: inventory-followup-20260926/shop (9EB1B0, 960B90, 9CD0E0,
// 5F69B0, 60E600, 8C48A0, 8C67B0, 8C5290 and 8C5520).
internal static class ShopCurrencyResourceChecks
{
    public static void Run()
    {
        CheckTable("pi._D7", "PET", 4, 35, 0, 13, 14, 868, 51, 165);
        CheckTable("PA._D9", "PETACCESSORY", 4, 24, 0, 19, 20, 18931, 65, 0);
        CheckTable("inter._D3", "INTERIOR", 4, 20, 0, 6, 7, 781, 352, 247);
        CheckPrice(15_001_011, 100, 0);
        CheckPrice(15_001_065, 7000, 0);
        CheckPrice(15_009_263, 0, 2500);
        CheckPrice(17_000_004, 1000, 0);
        CheckPrice(17_000_030, 3000, 0);
        CheckPrice(18_000_001, 0, 480); // SPECIAL_GEMSTONE field8 stays Cash.
        CheckPrice(18_000_002, 0, 200);
        CheckPrice(11_110_021, 4950, 0);
        CheckPrice(11_420_304, 0, 36);
        Console.WriteLine("SHOP_CURRENCY_RESOURCE_CHECKS_PASS PET-51-Hans-runtime-or-original-Cash PA-65-Hans furniture-352-Hans-247-Cash SP-unchanged");
    }

    private static void CheckPrice(uint code, uint hans, uint cash)
    {
        Check(ShopCatalog.TryGet(code, out var item) && item.HansPrice == hans && item.CashPrice == cash,
            $"explicit currency oracle {code}: Hans={hans} Cash={cash}");
    }

    private static void CheckTable(string file, string kind, int header, int width, int codeColumn,
        int hansColumn, int cashColumn, int count, int hansRows, int cashRows)
    {
        var fields = CardCatalog.DecryptFields("OpenNanaimo.Adapter.ClientData." + file);
        Check(fields[0] == kind && int.Parse(fields[2], CultureInfo.InvariantCulture) == count,
            file + " exact resource header");
        int gold = 0, premium = 0;
        for (int i = 0; i < count; i++)
        {
            int offset = header + i * width;
            uint Number(int column) => uint.Parse(fields[offset + column], CultureInfo.InvariantCulture);
            uint code = Number(codeColumn), hans = Number(hansColumn), cash = Number(cashColumn);
            if (hans != 0) gold++;
            if (cash != 0) premium++;
            // Current resource rows have only one priced currency. Zero-price rows remain non-sale.
            Check(!(hans != 0 && cash != 0), $"{file} conflicting currencies at {code}", quiet: true);
            Check(ShopCatalog.TryGet(code, out var item) && item.HansPrice == hans && item.CashPrice == cash
                && item.IsPurchasable == (hans != 0 || cash != 0), $"{file} price columns at {code}", quiet: true);
            if (file == "inter._D3")
                Check(item!.DurationDays == Number(5) && item.InteriorType == Number(2)
                    && item.IconPath == fields[offset + 13], $"{file} non-price columns at {code}", quiet: true);
        }
        // Runtime pi._D7 disables 114 Cash rows present in the supplied original client.
        // Do not rewrite those resources or call disabled rows purchasable. Every row above
        // must match the actual resource, while accepting both evidenced resource revisions.
        bool knownCashCount = premium == cashRows || (file == "pi._D7" && premium == 279);
        Check(gold == hansRows && knownCashCount, $"{file} complete price counts {gold}/{premium}");
    }

    private static void Check(bool success, string message, bool quiet = false)
    {
        if (!success) throw new InvalidDataException("CHECK_FAILED " + message);
        if (!quiet) Console.WriteLine("CHECK_PASS " + message);
    }
}
