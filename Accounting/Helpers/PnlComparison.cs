using Accounting.Models.DTOs.Accounting;

namespace Accounting.Helpers;

/// <summary>ความละเอียดของคอลัมน์งบกำไรขาดทุนเปรียบเทียบ</summary>
public enum PnlCompareGranularity { Month, Quarter, Year }

/// <summary>หนึ่งคอลัมน์ (ช่วงวันแบบรวมปลาย) — <paramref name="IsPartial"/> = ถูกตัดโดยช่วงที่ผู้ใช้เลือก</summary>
public readonly record struct PnlPeriod(string Label, DateTime Start, DateTime EndInclusive, bool IsPartial);

/// <summary>
/// **งบกำไรขาดทุนเปรียบเทียบ — ตัวแบ่งช่วง + ตัวประกอบตาราง (ตัวตั้งเดียว · ทีม M 2026-10-22)**
///
/// <para><b>ไม่มีสูตรงบในไฟล์นี้</b> — ตัวเลขทุกช่องมาจาก <c>AccountingService.GetProfitAndLossAsync</c> (ตัวคำนวณงบกำไรขาดทุนตัวเดียว:
/// Posted+Reversed · ไม่นับใบปิดบัญชี · ตัวกรองสาขา/มิติ/โครงการ + ส่วนที่ปันส่วน) เรียกครั้งละหนึ่งช่วง · ไฟล์นี้ทำแค่
/// (1) แบ่งช่วง [from, to] เป็นคอลัมน์ <b>ต่อกันพอดี ไม่ทับ ไม่เว้น</b> ⇒ ผลรวมคอลัมน์ = ยอดทั้งงวดเสมอ (เทสต์ล็อก) ·
/// (2) ไตรมาส/ปี ตาม <b>รอบบัญชีของบริษัท</b> (<see cref="FiscalYear"/> — ไม่ใช่ปีปฏิทินเสมอ) · (3) % เปลี่ยนแปลง</para>
///
/// <para>ป้ายช่วงเป็น พ.ศ. (รายงานภาษาไทยที่ผู้ใช้อ่าน) · ภายในเก็บ ค.ศ.</para>
/// </summary>
public static class PnlComparison
{
    /// <summary>เพดานจำนวนคอลัมน์ — 36 เดือน (3 ปี) · มากกว่านี้ให้เปลี่ยนเป็นรายไตรมาส/รายปี</summary>
    public const int MaxColumns = 36;

    public const string KindAccount = "Account";
    public const string KindTotalRevenue = "TotalRevenue";
    public const string KindTotalExpenses = "TotalExpenses";
    public const string KindNetIncome = "NetIncome";

    private static readonly string[] ThaiShortMonths =
        { "ม.ค.", "ก.พ.", "มี.ค.", "เม.ย.", "พ.ค.", "มิ.ย.", "ก.ค.", "ส.ค.", "ก.ย.", "ต.ค.", "พ.ย.", "ธ.ค." };

    /// <summary>อ่านพารามิเตอร์ <c>granularity</c> — month/monthly · quarter/quarterly · year/yearly/annual (ไม่สนตัวพิมพ์) ·
    /// ค่าอื่น = false (controller ตอบ 400 ไม่เดาเงียบ)</summary>
    public static bool TryParseGranularity(string? raw, out PnlCompareGranularity g)
    {
        switch ((raw ?? "").Trim().ToLowerInvariant())
        {
            case "month": case "monthly": g = PnlCompareGranularity.Month; return true;
            case "quarter": case "quarterly": g = PnlCompareGranularity.Quarter; return true;
            case "year": case "yearly": case "annual": g = PnlCompareGranularity.Year; return true;
            default: g = PnlCompareGranularity.Month; return false;
        }
    }

    /// <summary>ป้ายรอบบัญชี — รอบปฏิทิน = "2569" · รอบข้ามปี = "2569/70"</summary>
    private static string FiscalYearLabel(int fiscalYear, int fiscalStartMonth)
        => FiscalYear.NormalizeStartMonth(fiscalStartMonth) == 1
            ? (fiscalYear + 543).ToString()
            : $"{fiscalYear + 543}/{(fiscalYear + 544) % 100:D2}";

    /// <summary>แบ่ง [from, to] (รวมปลาย) เป็นคอลัมน์ต่อกันพอดี · คอลัมน์แรก/สุดท้ายอาจไม่เต็มช่วง (IsPartial)</summary>
    /// <exception cref="BusinessRuleException">to ก่อน from · คอลัมน์เกิน <see cref="MaxColumns"/></exception>
    public static List<PnlPeriod> BuildPeriods(DateTime from, DateTime to, PnlCompareGranularity granularity, int fiscalStartMonth)
    {
        from = from.Date;
        to = to.Date;
        if (to < from) throw new BusinessRuleException("วันที่สิ้นสุดต้องไม่ก่อนวันที่เริ่ม");
        var sm = FiscalYear.NormalizeStartMonth(fiscalStartMonth);
        var list = new List<PnlPeriod>();
        var cursor = from;
        while (cursor <= to)
        {
            var (bucketStart, bucketEndExclusive, label) = Bucket(cursor, granularity, sm);
            var bucketEnd = bucketEndExclusive.AddDays(-1);
            var end = bucketEnd < to ? bucketEnd : to;
            list.Add(new PnlPeriod(label, cursor, end, cursor > bucketStart || end < bucketEnd));
            if (list.Count > MaxColumns)
                throw new BusinessRuleException(
                    $"ช่วงที่เลือกมีมากกว่า {MaxColumns} คอลัมน์ — แคบช่วงวันที่ หรือเปลี่ยนเป็นรายไตรมาส/รายปี");
            cursor = end.AddDays(1);
        }
        return list;
    }

    private static (DateTime Start, DateTime EndExclusive, string Label) Bucket(DateTime d, PnlCompareGranularity g, int sm)
    {
        switch (g)
        {
            case PnlCompareGranularity.Year:
            {
                var fy = FiscalYear.FiscalYearOf(d, sm);
                var r = FiscalYear.RangeFor(fy, sm);
                return (r.Start, r.EndExclusive, "ปีบัญชี " + FiscalYearLabel(fy, sm));
            }
            case PnlCompareGranularity.Quarter:
            {
                var fy = FiscalYear.FiscalYearOf(d, sm);
                var fyStart = FiscalYear.RangeFor(fy, sm).Start;
                var q = (((d.Month - sm) % 12) + 12) % 12 / 3;
                var start = fyStart.AddMonths(q * 3);
                return (start, start.AddMonths(3), $"ไตรมาส {q + 1}/{FiscalYearLabel(fy, sm)}");
            }
            default:
            {
                var start = new DateTime(d.Year, d.Month, 1);
                return (start, start.AddMonths(1), $"{ThaiShortMonths[d.Month - 1]} {d.Year + 543}");
            }
        }
    }

    /// <summary>ช่วงเดียวกันปีก่อน — เลื่อน 12 เดือน · ปลายช่วงที่เป็นวันสิ้นเดือน ⇒ วันสิ้นเดือนเดียวกันของปีก่อน (ก.พ. 29/28 ไม่ตกหล่น)</summary>
    public static (DateTime Start, DateTime EndInclusive) PriorYear(DateTime start, DateTime endInclusive)
    {
        var s = start.Date.AddYears(-1);
        var e = endInclusive.Date;
        var isMonthEnd = e.AddDays(1).Day == 1;
        var pe = e.AddYears(-1);
        if (isMonthEnd) pe = new DateTime(pe.Year, pe.Month, DateTime.DaysInMonth(pe.Year, pe.Month));
        return (s, pe);
    }

    /// <summary>% เปลี่ยนแปลงเทียบฐาน (ทศนิยม 1 ตำแหน่ง AwayFromZero) · ฐาน 0 = null (ไม่มีความหมาย — หน้าแสดง "—")</summary>
    private static decimal? PercentChange(decimal current, decimal baseValue)
        => baseValue == 0m ? null : Math.Round((current - baseValue) / Math.Abs(baseValue) * 100m, 1, MidpointRounding.AwayFromZero);

    /// <summary>ประกอบตาราง — <paramref name="current"/>[i] = ผล GetProfitAndLossAsync ของ <paramref name="periods"/>[i] ·
    /// <paramref name="prior"/> (ถ้ามี) = ช่วงเดียวกันปีก่อนของคอลัมน์เดียวกัน</summary>
    public static PnlComparisonResponse Assemble(DateTime from, DateTime to, PnlCompareGranularity granularity, int fiscalStartMonth,
        IReadOnlyList<PnlPeriod> periods, IReadOnlyList<ProfitAndLossResponse> current, IReadOnlyList<ProfitAndLossResponse>? prior)
    {
        if (current.Count != periods.Count) throw new ArgumentException("จำนวนผลต่อช่วงไม่เท่าจำนวนคอลัมน์", nameof(current));
        if (prior != null && prior.Count != periods.Count) throw new ArgumentException("จำนวนผลปีก่อนไม่เท่าจำนวนคอลัมน์", nameof(prior));

        var revenue = AccountRows(current.Select(p => p.Revenue).ToList(), prior?.Select(p => p.Revenue).ToList());
        var expenses = AccountRows(current.Select(p => p.Expenses).ToList(), prior?.Select(p => p.Expenses).ToList());

        var totalRevenue = Row(KindTotalRevenue, null, "", "รวมรายได้",
            current.Select(p => p.TotalRevenue).ToList(), prior?.Select(p => p.TotalRevenue).ToList());
        var totalExpenses = Row(KindTotalExpenses, null, "", "รวมค่าใช้จ่าย",
            current.Select(p => p.TotalExpenses).ToList(), prior?.Select(p => p.TotalExpenses).ToList());
        var net = Row(KindNetIncome, null, "", "กำไร(ขาดทุน)สุทธิ",
            current.Select(p => p.NetIncome).ToList(), prior?.Select(p => p.NetIncome).ToList());

        var infos = new List<PnlComparePeriodInfo>(periods.Count);
        foreach (var p in periods)
        {
            DateTime? ps = null, pe = null;
            if (prior != null) { var (a, b) = PriorYear(p.Start, p.EndInclusive); ps = a; pe = b; }
            infos.Add(new PnlComparePeriodInfo(p.Label, p.Start, p.EndInclusive, p.IsPartial, ps, pe));
        }

        var notes = new List<string>();
        if (periods.Any(p => p.IsPartial))
            notes.Add("คอลัมน์ที่มีเครื่องหมาย * ไม่เต็มช่วง (ถูกตัดตามวันที่ที่เลือก) — % เปลี่ยนแปลงของคอลัมน์นั้นเทียบกับช่วงที่ยาวไม่เท่ากัน");
        if (prior != null)
            notes.Add("ช่วงเดียวกันปีก่อน = วันที่เดียวกันย้อนหลัง 12 เดือน (คำนวณด้วยตัวคำนวณงบกำไรขาดทุนตัวเดียวกัน)");
        if (granularity != PnlCompareGranularity.Month && FiscalYear.NormalizeStartMonth(fiscalStartMonth) != 1)
            notes.Add($"ไตรมาส/ปี นับตามรอบบัญชีของบริษัท (เริ่มเดือน {ThaiShortMonths[FiscalYear.NormalizeStartMonth(fiscalStartMonth) - 1]})");

        return new PnlComparisonResponse(from.Date, to.Date, granularity.ToString(), FiscalYear.NormalizeStartMonth(fiscalStartMonth), prior != null,
            infos, revenue, expenses, totalRevenue, totalExpenses, net, notes);
    }

    private static List<PnlCompareRow> AccountRows(List<List<PnlSection>> current, List<List<PnlSection>>? prior)
    {
        // กุญแจ = ผัง (AccountId) — รหัสซ้ำข้ามผังที่ลบ/สร้างใหม่ไม่ถูกรวมผิดแถว · ไม่มี Id (ข้อมูลเก่า) ⇒ ใช้รหัส
        static string Key(PnlSection s) => s.AccountId?.ToString() ?? "code:" + s.AccountCode;
        var meta = new Dictionary<string, PnlSection>();
        foreach (var s in current.SelectMany(x => x).Concat(prior?.SelectMany(x => x) ?? Enumerable.Empty<PnlSection>()))
            meta.TryAdd(Key(s), s);

        static List<decimal> Amounts(List<List<PnlSection>> cols, string key)
            => cols.Select(c => c.Where(s => Key(s) == key).Sum(s => s.Amount)).ToList();

        return meta.Values
            .OrderBy(s => s.AccountCode, StringComparer.Ordinal)
            .Select(s =>
            {
                var k = Key(s);
                return Row(KindAccount, s.AccountId, s.AccountCode, s.AccountName, Amounts(current, k), prior == null ? null : Amounts(prior, k));
            })
            .ToList();
    }

    private static PnlCompareRow Row(string kind, Guid? accountId, string code, string name, List<decimal> amounts, List<decimal>? prior)
    {
        var change = new List<decimal?>(amounts.Count);
        for (var i = 0; i < amounts.Count; i++)
            change.Add(i == 0 ? null : PercentChange(amounts[i], amounts[i - 1]));
        var total = amounts.Sum();
        decimal? priorTotal = prior?.Sum();
        var yoy = prior?.Select((p, i) => PercentChange(amounts[i], p)).ToList();
        return new PnlCompareRow(kind, accountId, code, name, amounts, total, change, prior, priorTotal, yoy,
            priorTotal.HasValue ? PercentChange(total, priorTotal.Value) : null);
    }

    // ═══ ไฟล์ส่งออก (CSV/Excel ผ่าน TabularExport → MiniExcel) — แถว/ยอด = ตารางเดียวกับหน้า ไม่รวมใหม่ ═══

    private sealed record CompareLine(string Section, PnlCompareRow Row);

    /// <summary>แผ่นงานของมุมมองเปรียบเทียบ — หมวด · รหัส · ชื่อ · คอลัมน์ต่อช่วง · รวม · (ถ้าเปรียบเทียบปีก่อน) ปีก่อนต่อช่วง · รวมปีก่อน ·
    /// % เทียบปีก่อน · ช่องที่ไม่มีความหมาย (ฐาน 0) = ว่าง</summary>
    public static TabularSheet ComparisonSheet(PnlComparisonResponse r, IEnumerable<string> title)
    {
        var cols = new List<TabularColumn<CompareLine>>
        {
            new("หมวด", x => x.Section),
            new("รหัสบัญชี", x => x.Row.AccountCode),
            new("ชื่อบัญชี", x => x.Row.AccountName),
        };
        for (var i = 0; i < r.Periods.Count; i++)
        {
            var idx = i;
            var label = r.Periods[i].Label + (r.Periods[i].IsPartial ? " *" : "");
            cols.Add(new(label, x => x.Row.Amounts[idx], TabularCellKind.Money));
        }
        cols.Add(new("รวม", x => x.Row.Total, TabularCellKind.Money));
        if (r.ComparePriorYear)
        {
            for (var i = 0; i < r.Periods.Count; i++)
            {
                var idx = i;
                cols.Add(new("ปีก่อน " + r.Periods[i].Label, x => x.Row.PriorAmounts?[idx], TabularCellKind.Money));
            }
            cols.Add(new("รวมปีก่อน", x => x.Row.PriorTotal, TabularCellKind.Money));
            cols.Add(new("% เทียบปีก่อน", x => x.Row.TotalChangeVsPriorYear, TabularCellKind.Number));
        }
        var rows = new List<CompareLine>();
        rows.AddRange(r.Revenue.Select(x => new CompareLine("รายได้", x)));
        rows.Add(new CompareLine("รายได้", r.TotalRevenue));
        rows.AddRange(r.Expenses.Select(x => new CompareLine("ค่าใช้จ่าย", x)));
        rows.Add(new CompareLine("ค่าใช้จ่าย", r.TotalExpenses));
        var footer = new[] { new CompareLine("", r.NetIncome) };
        return TabularExport.Sheet("งบกำไรขาดทุนเปรียบเทียบ", cols, rows, footer, title.Concat(r.Notes));
    }
}
