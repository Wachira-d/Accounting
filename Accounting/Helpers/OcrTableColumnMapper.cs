namespace Accounting.Helpers;

/// <summary>บทบาทของคอลัมน์ในตารางรายการสินค้า</summary>
public enum OcrTableColumn
{
    /// <summary>ไม่รู้จัก — ต้องไม่ผูกกับช่องไหน</summary>
    Unknown = 0,
    Description,
    Quantity,
    UnitPrice,
    Amount,
    Unit,
    VatRate,
}

/// <summary>ตำแหน่งคอลัมน์ที่จับคู่ได้ (−1 = ไม่พบ)</summary>
public readonly record struct OcrTableColumns(
    int Description, int Quantity, int UnitPrice, int Amount, int Unit, int VatRate);

/// <summary>
/// **จับคู่หัวคอลัมน์ของตารางรายการ → บทบาท** (pure, ไม่มี I/O)
///
/// ═══ ทำไมต้องมี (ผลตรวจ 2026-09-06 · T2-11) ═══
/// ตัวจับเดิมไล่ <c>Contains</c> ตามลำดับ desc → qty → price → amount และกติกา
/// ของ qty คือ <c>h.Contains("จำนวน")</c> — แต่หัวคอลัมน์ยอดเงินบนใบไทยเขียนว่า
/// **"จำนวนเงิน"** ซึ่ง<b>มีคำว่า "จำนวน" อยู่ข้างใน</b> ⇒ คอลัมน์<b>ยอดเงิน</b>
/// ถูกจองเป็นคอลัมน์<b>จำนวน</b> ⇒ ใบที่ไม่มีคอลัมน์ "จำนวน" จริง จะได้
/// <c>Quantity = 1,240.00</c> และยอดเงินไปตกที่คอลัมน์สุดท้าย (fallback)
/// ⇒ ตัวเลขบนเอกสารผิดทั้งบรรทัด โดย "ดูเหมือนอ่านได้" ทุกช่อง
///
/// <para>กติกา: จับ **คำที่เจาะจงกว่าก่อนเสมอ** (จำนวนเงิน ก่อน จำนวน · ราคารวม
/// ก่อน ราคา) และตัดสินทีละคอลัมน์แบบ "ให้คะแนนความเจาะจง" ไม่ใช่ลำดับ if-else
/// ซึ่งขึ้นกับลำดับที่คนเขียนบังเอิญวางไว้ (defect class เดียวกับ "ใครมาก่อนชนะ")</para>
/// </summary>
public static class OcrTableColumnMapper
{
    /// <summary>กติกา: (คำที่ต้องเจอ, บทบาท, ความเจาะจง) — <b>เจาะจงมากชนะ</b>
    /// ไม่ขึ้นกับลำดับในลิสต์ · คำยาวกว่าย่อมเจาะจงกว่าโดยธรรมชาติ แต่ระบุเป็นตัวเลข
    /// ไว้เพื่อให้อ่านออกและเทสต์ล็อกได้</summary>
    private static readonly (string Token, OcrTableColumn Role, int Specificity)[] Rules =
    {
        // ── ยอดเงินรวมของบรรทัด (ต้องชนะ "จำนวน" ให้ได้) ──
        ("จำนวนเงิน", OcrTableColumn.Amount, 100),
        ("จํานวนเงิน", OcrTableColumn.Amount, 100),   // ไม้หันอากาศคนละตัว (OCR/ฟอนต์)
        ("ราคารวม", OcrTableColumn.Amount, 100),
        ("มูลค่า", OcrTableColumn.Amount, 90),
        ("line total", OcrTableColumn.Amount, 100),
        ("amount", OcrTableColumn.Amount, 80),
        ("total", OcrTableColumn.Amount, 70),
        ("รวมเงิน", OcrTableColumn.Amount, 90),
        ("รวม", OcrTableColumn.Amount, 40),

        // ── ราคาต่อหน่วย ──
        ("ราคา/หน่วย", OcrTableColumn.UnitPrice, 100),
        ("ราคาต่อหน่วย", OcrTableColumn.UnitPrice, 100),
        ("ราคาต่อ", OcrTableColumn.UnitPrice, 90),
        ("หน่วยละ", OcrTableColumn.UnitPrice, 90),
        ("unit price", OcrTableColumn.UnitPrice, 100),
        ("price/unit", OcrTableColumn.UnitPrice, 100),
        ("ราคา", OcrTableColumn.UnitPrice, 50),
        ("price", OcrTableColumn.UnitPrice, 50),

        // ── จำนวน (ต้องไม่ชนะ "จำนวนเงิน") ──
        ("จำนวน", OcrTableColumn.Quantity, 45),
        ("จํานวน", OcrTableColumn.Quantity, 45),
        ("ปริมาณ", OcrTableColumn.Quantity, 60),
        ("quantity", OcrTableColumn.Quantity, 80),
        ("qty", OcrTableColumn.Quantity, 80),

        // ── หน่วยนับ ──
        ("หน่วยนับ", OcrTableColumn.Unit, 100),
        ("หน่วย", OcrTableColumn.Unit, 30),          // ต่ำกว่า "ราคา/หน่วย" เสมอ
        ("unit", OcrTableColumn.Unit, 30),

        // ── อัตราภาษี ──
        ("อัตราภาษี", OcrTableColumn.VatRate, 100),
        ("vat", OcrTableColumn.VatRate, 80),
        ("ภาษี", OcrTableColumn.VatRate, 60),

        // ── คำอธิบาย ──
        ("รายละเอียด", OcrTableColumn.Description, 90),
        ("รายการ", OcrTableColumn.Description, 90),
        ("description", OcrTableColumn.Description, 90),
        ("desc", OcrTableColumn.Description, 60),
        ("ชื่อสินค้า", OcrTableColumn.Description, 100),
        ("สินค้า", OcrTableColumn.Description, 80),
        ("ชื่อ", OcrTableColumn.Description, 50),
        ("item", OcrTableColumn.Description, 70),
    };

    /// <summary>บทบาทของหัวคอลัมน์เดียว — <c>Unknown</c> เมื่อไม่ตรงกติกาไหนเลย</summary>
    public static OcrTableColumn Classify(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return OcrTableColumn.Unknown;
        var h = header.Trim().ToLowerInvariant();
        var best = OcrTableColumn.Unknown;
        var bestScore = -1;
        foreach (var (token, role, spec) in Rules)
        {
            if (!h.Contains(token, StringComparison.Ordinal)) continue;
            if (spec > bestScore) { bestScore = spec; best = role; }
        }
        return best;
    }

    /// <summary>จับคู่หัวคอลัมน์ทั้งแถว — คอลัมน์แรกที่ได้บทบาทหนึ่ง ๆ ชนะ
    /// (ตารางที่มีสองคอลัมน์บทบาทเดียวกันเกิดได้ เช่น "รวม" ซ้ำท้ายตาราง)</summary>
    public static OcrTableColumns Map(IReadOnlyList<string?> headers)
    {
        int desc = -1, qty = -1, price = -1, amount = -1, unit = -1, vat = -1;
        for (var c = 0; c < headers.Count; c++)
        {
            switch (Classify(headers[c]))
            {
                case OcrTableColumn.Description: if (desc < 0) desc = c; break;
                case OcrTableColumn.Quantity: if (qty < 0) qty = c; break;
                case OcrTableColumn.UnitPrice: if (price < 0) price = c; break;
                case OcrTableColumn.Amount: if (amount < 0) amount = c; break;
                case OcrTableColumn.Unit: if (unit < 0) unit = c; break;
                case OcrTableColumn.VatRate: if (vat < 0) vat = c; break;
            }
        }
        return new(desc, qty, price, amount, unit, vat);
    }
}
