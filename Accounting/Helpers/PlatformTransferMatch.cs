namespace Accounting.Helpers;

/// <summary>รายการรับชำระที่ยังไม่มีเจ้าของรอบโอน (ผู้สมัครจับคู่) — <c>Net</c> = ยอดที่รายการนี้ทำให้ยอดคงเหลือที่ผู้ให้บริการเพิ่ม
/// (ยอดรับ − ยอดคืน − ค่าธรรมเนียมที่ถูกหัก · สูตร <c>GatewaySettlementMath.Contribution(...).Net</c> ตัวเดียวกับร่างรอบโอน)</summary>
public sealed record TransferMatchCandidate(Guid IntentId, string? ProviderRef, DateTime ConfirmedAtUtc, decimal Net);

/// <summary>ผลจับคู่ — <c>IntentIds</c> ว่าง = ไม่สร้างร่าง · <c>Problem</c> = สิ่งที่ผู้ตรวจต้องเห็น (มีได้แม้จับคู่ได้)</summary>
public sealed record TransferMatchResult(string Method, IReadOnlyList<Guid> IntentIds, string? Problem);

/// <summary>
/// รอบ 230 (PLATFORM_OMISE_AUDIT §5.3 · §7.1) — <b>ตัวจับคู่รอบโอนของผู้ให้บริการ ↔ รายการรับชำระ (pure)</b>
/// <list type="number">
/// <item><b>ผู้ให้บริการระบุ charge ของรอบโอน</b> ⇒ ใช้รายการที่เลข charge ตรง (<see cref="MethodChargeList"/>) · บางเลขไม่พบในรายการที่ยังไม่มีเจ้าของ ⇒
///   ยังร่างจากที่พบ แต่ติด <c>Problem</c> (ยอดรอบโอนจะไม่ลงตัว — ด่านยอดไม่ตรงเดิมของหน้ารอบโอนบล็อกการลงบัญชี)</item>
/// <item><b>ไม่ระบุ</b> (ยังไม่ยืนยันรูปข้อมูลกับ sandbox) ⇒ จับด้วย<b>ช่วงเวลา + ยอด</b> (<see cref="MethodAmountWindow"/>): รายการที่ยืนยันก่อนรอบโอนถูกสร้าง
///   เรียงเก่า→ใหม่ หา "ชุดเก่าสุด k รายการ" ที่ผลรวมยอดสุทธิ = ยอดที่หักจากยอดคงเหลือพอดี (±0.01) — ไม่พอดี ⇒ <b>ไม่ร่าง</b> (ห้ามเดาชุดอื่น · ผู้ทำบัญชีประกอบเองที่หน้ารอบโอน)</item>
/// </list>
/// ร่างเท่านั้น — การลงบัญชีเป็นของคน (สถานะ "ผู้ให้บริการโอนแล้ว" มาจากผู้ให้บริการจริง แต่ JE ต้องผ่านคนตรวจ)
/// </summary>
public static class PlatformTransferMatch
{
    public const string MethodChargeList = "ChargeList", MethodAmountWindow = "AmountWindow", MethodNone = "None";
    public const decimal Tolerance = 0.01m;

    public static string MethodLabel(string method) => method switch
    {
        MethodChargeList => "ผู้ให้บริการระบุรายการในรอบโอน",
        MethodAmountWindow => "จับคู่ด้วยช่วงเวลา + ยอด (ตรวจก่อนลงบัญชี)",
        _ => "ยังจับคู่ไม่ได้",
    };

    public static TransferMatchResult Match(IReadOnlyList<string>? chargeRefs, decimal walletDebit, DateTime? transferCreatedUtc,
        IReadOnlyList<TransferMatchCandidate> candidates)
    {
        if (chargeRefs is { Count: > 0 })
        {
            var byRef = candidates.Where(c => !string.IsNullOrEmpty(c.ProviderRef))
                .GroupBy(c => c.ProviderRef!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            var hit = chargeRefs.Distinct(StringComparer.Ordinal).Where(byRef.ContainsKey).Select(r => byRef[r]).ToList();
            var missing = chargeRefs.Distinct(StringComparer.Ordinal).Count() - hit.Count;
            if (hit.Count == 0)
                return new(MethodNone, Array.Empty<Guid>(),
                    $"ผู้ให้บริการระบุ {chargeRefs.Count} รายการในรอบโอน แต่ไม่พบรายการรับชำระที่ยังไม่อยู่ในรอบโอนใดตรงกันเลย — ตรวจที่หน้ารอบโอน");
            var sum = hit.Sum(c => c.Net);
            string? problem = null;
            if (missing > 0)
                problem = $"ไม่พบ {missing} รายการที่ผู้ให้บริการระบุ (อาจอยู่ในรอบโอนอื่นแล้ว/ไม่ใช่ค่าบริการ) — ยอดร่างอาจไม่ลงตัว ตรวจก่อนลงบัญชี";
            else if (Math.Abs(sum - walletDebit) > Tolerance)
                problem = $"ยอดสุทธิของรายการที่ระบุ ({sum:N2}) ไม่เท่ายอดที่หักจากยอดคงเหลือ ({walletDebit:N2}) — ตรวจค่าธรรมเนียม/เงินคืนก่อนลงบัญชี";
            return new(MethodChargeList, hit.OrderBy(c => c.ConfirmedAtUtc).Select(c => c.IntentId).ToList(), problem);
        }

        if (walletDebit <= 0m) return new(MethodNone, Array.Empty<Guid>(), "รอบโอนยอดเป็นศูนย์ — ไม่มีอะไรให้ร่าง");
        var ordered = candidates
            .Where(c => transferCreatedUtc == null || c.ConfirmedAtUtc < transferCreatedUtc.Value)
            .OrderBy(c => c.ConfirmedAtUtc).ThenBy(c => c.IntentId).ToList();
        var running = 0m;
        for (var k = 0; k < ordered.Count; k++)
        {
            running += ordered[k].Net;
            if (Math.Abs(running - walletDebit) <= Tolerance)
                return new(MethodAmountWindow, ordered.Take(k + 1).Select(c => c.IntentId).ToList(),
                    "ผู้ให้บริการไม่ระบุรายการในรอบโอน — ระบบจับด้วยช่วงเวลา + ยอด (รายการเก่าสุดที่รวมได้พอดี) · ตรวจรายการก่อนลงบัญชี");
        }
        return new(MethodNone, Array.Empty<Guid>(),
            $"ผู้ให้บริการไม่ระบุรายการในรอบโอน และไม่มีชุดรายการเก่าสุดที่รวมได้ {walletDebit:N2} พอดี — ประกอบรอบโอนเองที่หน้ารอบโอน (ระบบไม่เดา)");
    }
}
