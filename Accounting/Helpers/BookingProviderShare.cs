namespace Accounting.Helpers;

/// <summary>ผู้ให้บริการหนึ่งคนในการจอง + สัดส่วนงาน %</summary>
public sealed record BookingProviderSharePart(Guid ResourceId, decimal SharePercent);

/// <summary>
/// <b>ตัวตรวจเดียว</b>ของ "แบ่ง % งานระหว่างผู้ให้บริการหลายคนในการจองเดียว" (รอบ 207 · คำขอเจ้าของข้อ 3)
/// <para>กติกา: อย่างน้อย 1 คน · ไม่ซ้ำ · แต่ละคน &gt; 0 และ ≤ 100 · ทศนิยมไม่เกิน 2 ตำแหน่ง · ผลรวม = 100 พอดี ·
/// ไม่ส่ง % มาเลย ⇒ แบ่งเท่ากัน (<see cref="Even"/> เศษสตางค์ของ % ลงคนแรก) · ผู้ให้บริการหลัก = สัดส่วนมากสุด (เท่ากัน = ลำดับที่ส่งมา)</para>
/// <para>ผู้อ่าน: <c>CmsBookingService.StartServiceAsync</c>/<c>SetAssignmentsAsync</c> · รายงาน <c>BookingProviderReport</c> (ยอดตามสัดส่วน)</para>
/// </summary>
public static class BookingProviderShare
{
    public const string RuleCode = "CMS-BOOKING-PROVIDER-SHARE";
    public const int MaxProviders = 10;

    private static decimal R2(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    /// <summary>แบ่งเท่ากัน — Σ = 100 พอดี (เศษลงคนแรก)</summary>
    internal static IReadOnlyList<BookingProviderSharePart> Even(IReadOnlyList<Guid> ids)
    {
        if (ids.Count == 0) return Array.Empty<BookingProviderSharePart>();
        var each = Math.Floor(10000m / ids.Count) / 100m;
        var first = R2(100m - each * (ids.Count - 1));
        return ids.Select((id, i) => new BookingProviderSharePart(id, i == 0 ? first : each)).ToList();
    }

    /// <summary>ตรวจ + เติมสัดส่วนที่ขาด · ทุกคนไม่ส่ง % ⇒ แบ่งเท่ากัน · ส่งบางคน ⇒ ผิด (ต้องครบทุกคนหรือไม่ส่งเลย)</summary>
    public static (IReadOnlyList<BookingProviderSharePart> Parts, string? Error) Normalize(IReadOnlyList<(Guid ResourceId, decimal? SharePercent)> input)
    {
        if (input.Count == 0) return (Array.Empty<BookingProviderSharePart>(), "เลือกผู้ให้บริการอย่างน้อย 1 คน");
        if (input.Count > MaxProviders) return (Array.Empty<BookingProviderSharePart>(), $"ผู้ให้บริการต่อการจองได้ไม่เกิน {MaxProviders} คน");
        if (input.Select(x => x.ResourceId).Distinct().Count() != input.Count || input.Any(x => x.ResourceId == Guid.Empty))
            return (Array.Empty<BookingProviderSharePart>(), "เลือกผู้ให้บริการซ้ำกัน");
        if (input.All(x => x.SharePercent is null)) return (Even(input.Select(x => x.ResourceId).ToList()), null);
        if (input.Any(x => x.SharePercent is null))
            return (Array.Empty<BookingProviderSharePart>(), "ระบุ % งานให้ครบทุกคน (หรือเว้นว่างทุกคนให้ระบบแบ่งเท่ากัน)");
        foreach (var x in input)
        {
            var v = x.SharePercent!.Value;
            if (v <= 0m || v > 100m) return (Array.Empty<BookingProviderSharePart>(), "% งานของแต่ละคนต้องมากกว่า 0 และไม่เกิน 100");
            if (R2(v) != v) return (Array.Empty<BookingProviderSharePart>(), "% งานใส่ทศนิยมได้ไม่เกิน 2 ตำแหน่ง");
        }
        var sum = input.Sum(x => x.SharePercent!.Value);
        if (sum != 100m) return (Array.Empty<BookingProviderSharePart>(), $"% งานรวมต้องเท่ากับ 100 (ตอนนี้ {sum:0.##})");
        return (input.Select(x => new BookingProviderSharePart(x.ResourceId, x.SharePercent!.Value)).ToList(), null);
    }

    /// <summary>ผู้ให้บริการหลัก = สัดส่วนมากสุด (เท่ากัน = คนแรกที่ส่งมา)</summary>
    public static Guid? Primary(IReadOnlyList<BookingProviderSharePart> parts)
        => parts.Count == 0 ? null : parts.Select((p, i) => (p, i)).OrderByDescending(x => x.p.SharePercent).ThenBy(x => x.i).First().p.ResourceId;

    /// <summary>ยอดตามสัดส่วน — เศษสตางค์ลงคนหลัก ⇒ Σ = ยอดเต็มเสมอ</summary>
    public static IReadOnlyDictionary<Guid, decimal> SplitAmount(decimal amount, IReadOnlyList<BookingProviderSharePart> parts)
    {
        var result = parts.ToDictionary(p => p.ResourceId, p => R2(amount * p.SharePercent / 100m));
        if (parts.Count > 0 && Primary(parts) is Guid main)
            result[main] = R2(result[main] + (R2(amount) - result.Values.Sum()));
        return result;
    }

    public static string Describe(IEnumerable<(string Name, decimal Share)> parts)
        => string.Join(" · ", parts.Select(p => $"{p.Name} {p.Share:0.##}%"));
}
