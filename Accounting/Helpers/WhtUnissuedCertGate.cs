using System;
using System.Collections.Generic;
using System.Linq;

namespace Accounting.Helpers;

/// <summary>สรุป "แถวที่ถูกตัดออกจากยอดนำส่ง เพราะยังไม่มีหนังสือรับรอง 50 ทวิ
/// ที่ออกจริง" — ใช้ได้ทั้งเป็นด่าน (บล็อกการยื่น) และเป็นคำเตือนบนไฟล์ยื่น</summary>
/// <param name="Count">จำนวนแถว</param>
/// <param name="TaxAmount">ยอดภาษีรวมของแถวเหล่านั้น</param>
/// <param name="Samples">ตัวอย่างข้อความของแถว (สูงสุด 3 แถว) ให้ผู้ใช้รู้ว่า "ใบไหน"</param>
public sealed record UnissuedWhtRows(int Count, decimal TaxAmount, IReadOnlyList<string> Samples)
{
    public bool Any => Count > 0;

    /// <summary>ประโยคสั้นต่อท้ายข้อความสรุปไฟล์ยื่น (แบบเดียวกับ
    /// <c>ExportPnd3Async</c> ที่มี reviewNote อยู่แล้ว) — ว่างเมื่อไม่มีแถว</summary>
    public string ReviewNote => Count == 0 ? "" :
        $" · ⚠️ ตัดออกจากไฟล์ {Count} รายการ (ภาษี {TaxAmount:N2} บาท) เพราะยังไม่มีหนังสือรับรอง 50 ทวิ ที่ออกแล้ว"
        + (Samples.Count == 0 ? "" : $" — {string.Join(", ", Samples)}")
        + (Count > Samples.Count ? ", …" : "")
        + " — ออกใบให้ครบที่หน้า \"หนังสือรับรองหัก ณ ที่จ่าย\" แล้วกด \"สร้างใหม่\"";
}

/// <summary>
/// ด่าน**ตัวเดียว**ของ "ยอดหัก ณ ที่จ่ายที่ยังไม่มีใบ 50 ทวิ ออกจริง"
///
/// <para>═══ ทำไมต้องมี (DECISION_AUDIT_2026-09-18 · D2-B1a) ═══
/// <c>StatutoryRemittanceService.RemitAsync</c> มีด่าน <c>WHT-CERT-UNISSUED</c>
/// ที่บล็อก**การจ่ายเงินนำส่ง**อยู่แล้ว แต่ <c>FileTaxReportAsync</c> (การ
/// "ยื่นแบบ") ไม่มี ⇒ ด่านไม่สมมาตร: ยื่นแบบที่ประกาศยอดไม่ครบได้ แล้วค่อยไป
/// ชนด่านตอนจ่าย · และ <c>BuildPndAsync</c> ตัดแถวเหล่านี้ออกจากไฟล์ยื่น
/// **เงียบ ๆ** (ต่างจาก <c>ExportPnd3Async</c> ที่มี reviewNote)</para>
///
/// <para>═══ กติกา ═══ แถวเตือนถูกสร้างโดย <c>TaxService.GenerateWhtReport</c>
/// ด้วย <c>IsExcluded = true</c> + คำนำหน้าใน <see cref="DraftCertMarker"/> /
/// <see cref="UnissuedCertMarker"/> — **คำนำหน้าเป็นของไฟล์นี้** ผู้สร้างแถว
/// ต้องอ้างค่าคงที่ที่นี่ ห้ามพิมพ์ข้อความซ้ำ (ไม่งั้นด่านจะมองไม่เห็นแถวของ
/// ตัวเองทันทีที่มีใครแก้ถ้อยคำ)</para>
/// </summary>
public static class WhtUnissuedCertGate
{
    /// <summary>คำนำหน้าแถว "เอกสารหัก WHT แต่ไม่มีหนังสือรับรองเลย"</summary>
    public const string UnissuedCertMarker = "⚠️ ยังไม่ออกหนังสือรับรอง";

    /// <summary>คำนำหน้าแถว "มีหนังสือรับรองแต่ยังเป็นร่าง"</summary>
    public const string DraftCertMarker = "⚠️ หนังสือรับรองยังเป็นร่าง";

    /// <summary>รหัสด่าน — ชุดเดียวกับที่ <c>RemitAsync</c> ใช้ เพื่อให้ผู้ใช้
    /// เห็นเหตุผลเดียวกันไม่ว่าจะชนด่านที่ "ยื่นแบบ" หรือที่ "นำส่งเงิน"</summary>
    public const string RuleCode = "WHT-CERT-UNISSUED";

    /// <summary>ชื่อแบบยื่นของรายงานหัก ณ ที่จ่าย — ใช้ในข้อความของด่านนี้
    /// เท่านั้น (ไม่ใช่ตารางกลางของชนิดรายงาน)</summary>
    public static string FormLabel(Models.Enums.TaxType taxType) => taxType switch
    {
        Models.Enums.TaxType.WithholdingTax1 => "ภ.ง.ด.1",
        Models.Enums.TaxType.WithholdingTax3 => "ภ.ง.ด.3",
        Models.Enums.TaxType.WithholdingTax53 => "ภ.ง.ด.53",
        Models.Enums.TaxType.WithholdingTax54 => "ภ.ง.ด.54",
        _ => taxType.ToString(),
    };

    /// <summary>รหัสแบบยื่น (PND.1/3/53/54) → ชนิดรายงาน — **ตัวเดียว**ของทั้ง
    /// ตัวสร้างไฟล์ (<c>TaxService.BuildPndAsync</c>) และตัวที่หาคำเตือนมาแปะ
    /// (<c>TaxController.GenerateEFiling</c>) · คืน null เมื่อไม่ใช่แบบ ภ.ง.ด.</summary>
    public static Models.Enums.TaxType? TaxTypeForPndForm(string? formType)
        => (formType ?? "").ToUpperInvariant() switch
        {
            "PND.1" => Models.Enums.TaxType.WithholdingTax1,
            "PND.3" => Models.Enums.TaxType.WithholdingTax3,
            "PND.53" => Models.Enums.TaxType.WithholdingTax53,
            "PND.54" => Models.Enums.TaxType.WithholdingTax54,
            _ => null,
        };

    /// <summary>แถวนี้เป็นแถวเตือน "ใบ 50 ทวิ ยังไม่ออก" หรือไม่</summary>
    private static bool IsUnissuedCertLine(string? description)
        => description != null
           && (description.StartsWith(UnissuedCertMarker, StringComparison.Ordinal)
               || description.StartsWith(DraftCertMarker, StringComparison.Ordinal));

    /// <summary>รวมแถวเตือนของรายงานหนึ่งฉบับ — pure ทั้งหมด (เทสต์ได้โดยไม่ต้องมี DB)</summary>
    public static UnissuedWhtRows Evaluate(
        IEnumerable<(string? Description, bool IsExcluded, decimal TaxAmount)> lines)
    {
        var hits = (lines ?? Enumerable.Empty<(string?, bool, decimal)>())
            .Where(l => l.IsExcluded && IsUnissuedCertLine(l.Description))
            .ToList();

        if (hits.Count == 0) return new UnissuedWhtRows(0, 0m, Array.Empty<string>());

        var samples = hits.Take(3)
            .Select(h => Shorten(h.Description!))
            .ToList();

        return new UnissuedWhtRows(hits.Count, hits.Sum(h => h.TaxAmount), samples);
    }

    /// <summary>ข้อความบล็อกตอนยื่นแบบ — บอก "ใบไหน" และ "ทำอะไรต่อ" เสมอ
    /// (ด่านที่ไม่มีทางไปต่อ = ด่านที่ผู้ใช้ต้องหลบ)</summary>
    public static string BlockMessage(string formLabel, int month, int year, UnissuedWhtRows rows)
        => $"ยื่น {formLabel} งวด {month:D2}/{year} ไม่ได้ — มี {rows.Count} รายการ "
           + $"(ภาษี {rows.TaxAmount:N2} บาท) ที่ยังไม่มีหนังสือรับรอง 50 ทวิ ที่ออกแล้ว: "
           + string.Join(" · ", rows.Samples)
           + (rows.Count > rows.Samples.Count ? " · …" : "")
           + " — ออกใบให้ครบที่หน้า \"หนังสือรับรองหัก ณ ที่จ่าย\" (แท็บรอออกใบ) แล้วกด \"สร้างใหม่\" "
           + "หรือติ๊กรายการที่ไม่ใช่การจ่ายจริงออกจากรายงานก่อน";

    private static string Shorten(string description)
    {
        // ตัดคำนำหน้า marker + วงเล็บคำแนะนำท้ายบรรทัดออก เหลือส่วนที่ระบุ "ใบไหน"
        var s = description;
        if (s.StartsWith(UnissuedCertMarker, StringComparison.Ordinal))
            s = s[UnissuedCertMarker.Length..];
        else if (s.StartsWith(DraftCertMarker, StringComparison.Ordinal))
            s = s[DraftCertMarker.Length..];
        s = s.TrimStart(' ', '—', '-');
        var paren = s.IndexOf(" (", StringComparison.Ordinal);
        if (paren > 0) s = s[..paren];
        s = s.Trim();
        return s.Length == 0 ? description.Trim() : s;
    }
}
