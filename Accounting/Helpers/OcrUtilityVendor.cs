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
    private static readonly HashSet<string> TaxIds = new(StringComparer.Ordinal) { "0994000165501" };

    /// <summary>ชื่อนิติบุคคล (ย่อด้วย <see cref="ThaiTextNormalizer.SquashForKeywordMatch"/> ทั้งสองฝั่ง)</summary>
    private static readonly string[] LegalNames =
    {
        "การไฟฟ้าส่วนภูมิภาค", "การไฟฟ้านครหลวง", "การประปาส่วนภูมิภาค", "การประปานครหลวง",
        "provincial electricity authority", "metropolitan electricity authority",
        "provincial waterworks authority", "metropolitan waterworks authority",
        "แอดวานซ์ อินโฟร์ เซอร์วิส", "แอดวานซ์ ไวร์เลส เน็ทเวอร์ค", "advanced info service", "advanced wireless network",
        "ทรู มูฟ เอช", "ทรู อินเทอร์เน็ต", "ทรูมูฟ", "true move", "true internet",
        "ทริปเปิลที บรอดแบนด์", "triple t broadband", "โทรคมนาคมแห่งชาติ", "national telecom",
    };

    private static readonly string[] SquashedNames = LegalNames.Select(ThaiTextNormalizer.SquashForKeywordMatch).ToArray();

    /// <summary>อักษรย่อทางการ/แบรนด์ที่เป็นผู้ให้บริการอย่างเดียว — ต้องเป็นคำเต็ม (ไม่ติดตัวอักษรอื่น)</summary>
    private static readonly Regex AbbrevRe = new(
        @"(?<![\p{L}\d])(?:กฟภ|กฟน|กปภ|กปน|pea|mea|pwa|mwa|3bb)(?![\p{L}\d])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>ผู้ขายเป็นผู้ให้บริการแบบคิดตามมิเตอร์ (ไฟฟ้า · ประปา · โทรศัพท์/อินเทอร์เน็ต) — เลขผู้เสียภาษีชนะชื่อ · ข้อมูลไม่พอ = false</summary>
    public static bool Is(string? taxId, string? vendorName)
    {
        var digits = new string((taxId ?? "").Where(char.IsDigit).ToArray());
        if (digits.Length == 13 && TaxIds.Contains(digits)) return true;
        if (string.IsNullOrWhiteSpace(vendorName)) return false;
        var squashed = ThaiTextNormalizer.SquashForKeywordMatch(vendorName);
        if (SquashedNames.Any(n => n.Length > 0 && squashed.Contains(n, StringComparison.Ordinal))) return true;
        return AbbrevRe.IsMatch(vendorName);
    }
}
