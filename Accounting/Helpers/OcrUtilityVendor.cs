using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Accounting.Services.Implementations.Ocr;

namespace Accounting.Helpers;

/// <summary>
/// **ผู้ขายที่เป็นผู้ให้บริการสาธารณูปโภค/สื่อสารแบบคิดตามมิเตอร์ — ตัวตัดสินตัวเดียว** (pure · ทีม OCR 9 · 2026-10-16)
///
/// <para>ใช้เป็น "หลักฐานมิเตอร์" ข้อหนึ่งของ <see cref="OcrLineQuantityGuard.Decide"/> (บรรทัดที่ จำนวน × ราคา ไม่ลงตัว ⇒ 1 × ยอด + จำนวนในคำอธิบาย)
/// — ไม่ใช่ตัวจัดหมวดค่าใช้จ่าย (นั่นคือ <c>ExpenseCategoryResolver</c>/<c>SystemOcrKnowledgeSeeder</c> ซึ่งรู้จักแบรนด์สั้น ๆ "true"/"ais"/"nt"
/// สำหรับเดาผัง — ความคล้ายชื่อพอสำหรับเดาผัง แต่ไม่พอสำหรับตัดสินว่า "จำนวนบนบรรทัดไม่ใช่จำนวนสินค้า")</para>
///
/// <para>กติกา (กุญแจก่อนชื่อ — หลัก <c>DbdIdentityGuard</c>): (1) เลขผู้เสียภาษีที่ยืนยันจากกระดาษจริงในเรพ · (2) ชื่อนิติบุคคลเต็มของรัฐวิสาหกิจ/ผู้ให้บริการ
/// (ไม่ใช่แบรนด์สั้น — "True Coffee" ขายสินค้า · "AIS Shop" ขายโทรศัพท์) · (3) อักษรย่อทางการ กฟภ/กฟน/กปภ/กปน · PEA/MEA/PWA/MWA/3BB เป็นคำเต็มคำ ·
/// ห้ามเติมเลขผู้เสียภาษีจากความจำ (F2 ข้อ 3) — เพิ่มเมื่อมีกระดาษจริงยืนยัน</para>
/// </summary>
public static class OcrUtilityVendor
{
    /// <summary>เลขผู้เสียภาษีที่ยืนยันจากกระดาษจริง — การไฟฟ้าส่วนภูมิภาค (บิล XH0712609004157 · <c>OcrPaperSamples.PeaElectricityBill0869</c>)</summary>
    private static readonly Dictionary<string, OcrUtilityKind> TaxIds = new(StringComparer.Ordinal)
    {
        ["0994000165501"] = OcrUtilityKind.Electricity,
    };

    /// <summary>ชื่อนิติบุคคล (ย่อด้วย <see cref="ThaiTextNormalizer.SquashForKeywordMatch"/> ทั้งสองฝั่ง) + ชนิดบริการ</summary>
    private static readonly (string Name, OcrUtilityKind Kind)[] LegalNames =
    {
        ("การไฟฟ้าส่วนภูมิภาค", OcrUtilityKind.Electricity), ("การไฟฟ้านครหลวง", OcrUtilityKind.Electricity),
        ("provincial electricity authority", OcrUtilityKind.Electricity), ("metropolitan electricity authority", OcrUtilityKind.Electricity),
        ("การประปาส่วนภูมิภาค", OcrUtilityKind.Water), ("การประปานครหลวง", OcrUtilityKind.Water),
        ("provincial waterworks authority", OcrUtilityKind.Water), ("metropolitan waterworks authority", OcrUtilityKind.Water),
        ("แอดวานซ์ อินโฟร์ เซอร์วิส", OcrUtilityKind.Telecom), ("แอดวานซ์ ไวร์เลส เน็ทเวอร์ค", OcrUtilityKind.Telecom),
        ("advanced info service", OcrUtilityKind.Telecom), ("advanced wireless network", OcrUtilityKind.Telecom),
        ("ทรู มูฟ เอช", OcrUtilityKind.Telecom), ("ทรู อินเทอร์เน็ต", OcrUtilityKind.Telecom), ("ทรูมูฟ", OcrUtilityKind.Telecom),
        ("true move", OcrUtilityKind.Telecom), ("true internet", OcrUtilityKind.Telecom),
        ("ทริปเปิลที บรอดแบนด์", OcrUtilityKind.Telecom), ("triple t broadband", OcrUtilityKind.Telecom),
        ("โทรคมนาคมแห่งชาติ", OcrUtilityKind.Telecom), ("national telecom", OcrUtilityKind.Telecom),
    };

    private static readonly (string Squashed, OcrUtilityKind Kind)[] SquashedNames =
        LegalNames.Select(n => (ThaiTextNormalizer.SquashForKeywordMatch(n.Name), n.Kind)).ToArray();

    /// <summary>อักษรย่อทางการ/แบรนด์ที่เป็นผู้ให้บริการอย่างเดียว — ต้องเป็นคำเต็ม (ไม่ติดตัวอักษรอื่น)</summary>
    private static readonly Regex AbbrevRe = new(
        @"(?<![\p{L}\d])(?<a>กฟภ|กฟน|กปภ|กปน|pea|mea|pwa|mwa|3bb)(?![\p{L}\d])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>ผู้ขายเป็นผู้ให้บริการแบบคิดตามมิเตอร์ (ไฟฟ้า · ประปา · โทรศัพท์/อินเทอร์เน็ต) — เลขผู้เสียภาษีชนะชื่อ · ข้อมูลไม่พอ = false</summary>
    public static bool Is(string? taxId, string? vendorName) => KindOf(taxId, vendorName) != OcrUtilityKind.None;

    /// <summary>
    /// **ชนิดบริการของผู้ขายสาธารณูปโภค** (ทีม OCR 11 · ฝ่ายค้าน OCR 9 P1-1/P1-2) — <see cref="OcrLineQuantityGuard"/> ใช้เป็น<b>บริบท</b>ของหน่วยที่กำกวม
    /// (ประปา ⇒ ลบ.ม. · ไฟฟ้า ⇒ "หน่วย" · สื่อสาร ⇒ ครั้ง/GB/MB) — <b>ไม่ใช่หลักฐานมิเตอร์ด้วยตัวเอง</b> (AWN/ทรูมูฟ เอช ขายเคส/หูฟัง:
    /// เดิมชื่อผู้ขายอย่างเดียวพาบรรทัดสินค้า 3 ชิ้นไปเป็น 1 × ยอด) · เลขผู้เสียภาษีชนะชื่อ · ข้อมูลไม่พอ = <see cref="OcrUtilityKind.None"/>
    /// </summary>
    public static OcrUtilityKind KindOf(string? taxId, string? vendorName)
    {
        var digits = ThaiTaxId.Normalize(taxId);
        if (digits.Length == 13 && TaxIds.TryGetValue(digits, out var byId)) return byId;
        if (string.IsNullOrWhiteSpace(vendorName)) return OcrUtilityKind.None;
        var squashed = ThaiTextNormalizer.SquashForKeywordMatch(vendorName);
        foreach (var (n, kind) in SquashedNames)
            if (n.Length > 0 && squashed.Contains(n, StringComparison.Ordinal)) return kind;
        var m = AbbrevRe.Match(vendorName);
        if (!m.Success) return OcrUtilityKind.None;
        return m.Groups["a"].Value.ToLowerInvariant() switch
        {
            "กฟภ" or "กฟน" or "pea" or "mea" => OcrUtilityKind.Electricity,
            "กปภ" or "กปน" or "pwa" or "mwa" => OcrUtilityKind.Water,
            _ => OcrUtilityKind.Telecom,
        };
    }
}

/// <summary>ชนิดบริการของผู้ขายสาธารณูปโภค (<see cref="OcrUtilityVendor.KindOf"/>) — None = ไม่ใช่/ไม่รู้</summary>
public enum OcrUtilityKind
{
    None = 0,
    Electricity,
    Water,
    Telecom,
}
