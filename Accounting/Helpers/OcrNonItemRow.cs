using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Accounting.Helpers;

/// <summary>
/// **แถวในตารางรายการที่ไม่ใช่สินค้า/บริการ — แถวสรุป (รวม · VAT · ยอดสุทธิ · ส่วนลด) และแถวชำระ/เงินทอน (เงินสด · เงินทอน · บัตร · โอน)**
/// ตัวตัดสินตัวเดียวของทุก engine ที่คืน "ตารางรายการ" มาให้ (Azure layout-table fallback · python ocr-service · Azure prebuilt Items)
///
/// ═══ ที่มา (2026-10-09 · ตรวจความครอบคลุมเส้นกระดาษ) ═══
/// <para>สลิป POS (7-Eleven · Lotus · BigC · Makro) พิมพ์แถว "รวม 3 รายการ 774.00 · เงินสด 1,000.00 · เงินทอน 226.00 · VAT 7% 48.10"
/// ต่อท้ายตารางสินค้าในคอลัมน์เดียวกัน · ตัวอ่านตาราง layout ของ Azure (<c>TryExtractItemsFromTables</c>) ตัดเฉพาะแถวที่คำอธิบาย
/// "ไม่มีตัวอักษรเลย" ⇒ แถว "เงินสด 1,000.00" ผ่านเป็นบรรทัดสินค้า ⇒ Σ บรรทัด = 774 + 1,000 + 226 + 48.10 ≠ หัวใบ ⇒ ตัวกระทบยอด
/// ตก Ambiguous [Σ-GAP] ทุกใบ (ดีที่ไม่ลงบัญชีผิดเงียบ แต่ผู้ใช้ต้องลบแถวเองทุกใบ) · เส้น python/local AI คืน items แบบเดียวกัน</para>
///
/// ═══ กติกา (ฝั่ง "ตัดทิ้ง" ต้องแม่น — ตัดสินค้าจริงทิ้ง = ยอดขาดเงียบ) ═══
/// <list type="bullet">
/// <item>แยกคำอธิบายเป็นคำ (ไทยติดกันเป็นก้อน · อังกฤษแยกช่องว่าง · ตัวเลข/สัญลักษณ์ไม่นับ) · <b>คำแรก</b>ต้องเป็นป้ายสรุป/ชำระ<b>ทั้งคำ</b>
///   — "รวมมิตรทะเล" เป็นก้อนเดียว ≠ "รวม" · "Cashew" ≠ "cash" · "ทอนสเตน" ≠ "ทอน" · "ภาษีป้าย" ≠ "ภาษี"</item>
/// <item>คำที่เหลือ (ถ้ามี) ต้องเป็นคำประกอบที่แถวสรุปใช้ (รายการ · ชิ้น · บาท · VAT · บัตร · VISA · incl …) — "Visa application fee" ·
///   "Cash Drawer Tray" · "Total Care Package" มีคำอื่น ⇒ เป็นสินค้า คงไว้</item>
/// <item>แถว "มัดจำ" <b>ไม่</b>ตัด — ใบมัดจำมีบรรทัดมัดจำเป็นรายการจริง (<see cref="OcrDepositMarker"/>) · แถวค่าขนส่ง/ค่าบริการไม่ตัด (เป็นบรรทัดจริงที่เสีย VAT)</item>
/// <item>ไม่รู้ = คงไว้ (ปล่อยให้ตัวกระทบยอด/[Σ-GAP] บอกคน) — ตัวนี้ตัดเฉพาะที่แน่ใจ</item>
/// </list>
/// </summary>
public static class OcrNonItemRow
{
    /// <summary>ป้ายสรุป/ชำระ (ตัวพิมพ์เล็ก · คำอังกฤษหลายคำคั่นด้วยช่องว่างเดียว) — เรียงยาว→สั้นตอนเทียบ</summary>
    private static readonly string[] Primary =
    {
        // แถวชำระ / เงินทอน
        "เงินสด", "เงินทอน", "ทอน", "รับเงิน", "เงินรับ", "รับมา", "ชำระโดย", "ชำระด้วย", "ชำระเงิน", "ยอดชำระ",
        "บัตรเครดิต", "บัตรเดบิต", "บัตร", "เงินโอน", "โอนเงิน", "พร้อมเพย์", "คิวอาร์",
        "cash", "change", "tendered", "tender", "paid by", "paid", "payment", "received", "credit card", "debit card", "card",
        "promptpay", "qr code", "qr", "visa", "mastercard", "truemoney", "wallet",
        // แถวสรุป
        "รวมเงินทั้งสิ้น", "รวมทั้งสิ้น", "รวมทั้งหมด", "รวมสุทธิ", "รวมเงิน", "รวม", "ยอดรวม", "ยอดสุทธิ", "สุทธิ", "ยอดเงิน",
        "ภาษีมูลค่าเพิ่ม", "ภาษี", "ส่วนลดท้ายบิล", "ส่วนลดรวม", "ส่วนลด",
        "grand total", "sub total", "subtotal", "net total", "total", "vat", "tax", "discount", "amount due", "balance due",
        "net amount", "net amt", "amount",
    };

    /// <summary>คำประกอบที่แถวสรุป/ชำระใช้ต่อท้ายป้ายได้ (คำอื่นนอกนี้ = สินค้า)</summary>
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "รายการ", "ชิ้น", "บาท", "สตางค์", "ถ้วน", "รวม", "สุทธิ", "ภาษี", "ภาษีมูลค่าเพิ่ม", "เงินสด", "บัตร", "บัตรเครดิต", "บัตรเดบิต",
        "โอน", "เงินโอน", "พร้อมเพย์", "ทอน", "เงินทอน", "รับ", "จ่าย", "ชำระ", "ยอด", "ก่อน", "หลัง", "หัก", "แล้ว", "ท้ายบิล",
        "thb", "baht", "vat", "incl", "included", "inc", "excl", "excluded", "net", "amt", "amount", "total", "sub", "items", "item",
        "qty", "pcs", "visa", "mastercard", "master", "card", "credit", "debit", "cc", "qr", "promptpay", "cash", "change", "due",
        "paid", "tendered", "tender", "by", "in", "of", "and", "no", "ref", "approval", "code", "x",
    };

    private static readonly Regex WordToken = new(@"[ก-๛]+|[A-Za-z]+", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>แถวนี้เป็นแถวสรุป/แถวชำระ (ไม่ใช่สินค้า) หรือไม่ — <c>false</c> = คงไว้ (รวมกรณีคำอธิบายว่าง: ไม่รู้ = ไม่ตัด)</summary>
    public static bool IsSummaryOrTenderRow(string? description)
        => Judge(description).IsNonItem;

    /// <summary>คำตัดสินพร้อมเหตุผล (ให้ trace/คำเตือนอ้างได้ว่าตัดเพราะอะไร)</summary>
    public static (bool IsNonItem, string? Reason) Judge(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return (false, null);
        var tokens = WordToken.Matches(description).Select(m => m.Value.ToLowerInvariant()).ToList();
        if (tokens.Count == 0) return (false, null);
        var normalized = string.Join(" ", tokens);
        foreach (var label in Primary.OrderByDescending(x => x.Length))
        {
            if (normalized != label && !normalized.StartsWith(label + " ", StringComparison.Ordinal)) continue;
            var rest = normalized.Length == label.Length
                ? Array.Empty<string>()
                : normalized[(label.Length + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var stranger = rest.FirstOrDefault(w => !Allowed.Contains(w));
            if (stranger != null)
                return (false, $"ขึ้นต้นด้วย “{label}” แต่มีคำว่า “{stranger}” ที่แถวสรุปไม่ใช้ — ถือเป็นสินค้า");
            return (true, $"แถว “{description.Trim()}” ขึ้นต้นด้วยป้ายสรุป/ชำระ “{label}” — ไม่ใช่รายการสินค้า");
        }
        return (false, null);
    }

    /// <summary>ข้อความแจ้งใน trace/คำเตือนเมื่อตัดแถวออก (รูปเดียวทุก engine)</summary>
    public static string DroppedNote(IReadOnlyList<string> droppedDescriptions)
        => $"[Items] ตัดแถวสรุป/แถวชำระออกจากรายการ {droppedDescriptions.Count} แถว (ไม่ใช่สินค้า): "
           + string.Join(" · ", droppedDescriptions.Select(d => $"“{d.Trim()}”"));
}
