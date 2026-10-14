namespace Accounting.Helpers;

/// <summary>
/// "จำนวนเงินเป็นตัวอักษร" ภาษาไทย (เลข → ตัวอักษร) — <b>ตัวตั้งเดียว</b>ของทั้งระบบ
/// เช่น <c>72.64</c> → <c>"เจ็ดสิบสองบาทหกสิบสี่สตางค์"</c>
///
/// ═══ ผู้ใช้ (ทุกตัวต้องเรียกที่นี่ ห้ามมีสำเนาสูตร) ═══
/// <list type="bullet">
/// <item>เอกสารขาย/ซื้อ — HTML renderer (<c>PdfGenerationService.BuildDocumentHtml</c>) + QuestPDF (<c>ComposeSummary</c>)</item>
/// <item>หนังสือรับรอง 50 ทวิ — HTML (<c>BuildWithholdingTaxCertHtml</c>) + QuestPDF (<c>BuildTotalInWords</c>)
///   + หน้าพิมพ์ <c>wht.html</c> ผ่าน DTO <c>WithholdingTaxCertResponse.TotalTaxAmountInWords</c></item>
/// <item>พรีวิวสด (ขณะพิมพ์) ฝั่งหน้าเว็บ — <c>wwwroot/js/ux-helpers.js</c> <c>UX.AmountInWords</c> เป็นสำเนา JS
///   ตัวเดียวที่ต้องคงไว้ (คำนวณทุกครั้งที่กดแป้น) — ล็อกให้ตรงกับไฟล์นี้ทุกตัวอักษรด้วย
///   <c>tools/thai_baht_text_sim.js</c> ซึ่งอ่านตารางเคสจาก <c>ThaiBahtTextTests</c> ชุดเดียวกัน</item>
/// </list>
/// ขากลับ (ตัวอักษร → เลข สำหรับ OCR) อยู่ที่ <see cref="ThaiAmountInWords"/> — เทสต์ round-trip ผูกสองขาไว้
///
/// ═══ ที่มา (2026-10-14 · ทีม PDF) ═══
/// เดิมมีสูตรเขียนเอง 4 ชุด (C# 2 · JS 2) และทุกชุดของฝั่งพิมพ์ใช้ <c>units[pos % 7]</c> ไม่มีการวนกลุ่ม "ล้าน"
/// ⇒ ยอด ≥ 10,000,000 พิมพ์ผิดบนเอกสารจริง (10,000,000 → "หนึ่งบาทถ้วน" ·
/// 12,345,678 → "หนึ่งสองล้าน…" ) · สูตรเอกสารยังพิมพ์ 0.50 เป็น "ศูนย์บาทถ้วน" และโยน exception เมื่อยอดติดลบ ·
/// สำเนา JS พรีวิวพิมพ์ 101 เป็น "หนึ่งร้อยหนึ่ง" ขณะที่กระดาษพิมพ์ "หนึ่งร้อยเอ็ด"
///
/// ═══ กติกา ═══
/// <list type="bullet">
/// <item>ปัดเป็นสตางค์ก่อนแยกบาท/สตางค์ ด้วย <see cref="MidpointRounding.AwayFromZero"/> (1.995 → "สองบาทถ้วน" ไม่ใช่ "หนึ่งร้อยสตางค์")</item>
/// <item>"ล้าน" วนซ้ำได้ไม่จำกัด (1,000,000,000,000 → "หนึ่งล้านล้าน") — ทุกกลุ่ม 6 หลักใช้กติกาเดียวกัน</item>
/// <item>หลักสิบ: 1 → "สิบ" · 2 → "ยี่สิบ"</item>
/// <item>หลักหน่วย 1 → "เอ็ด" เมื่อมีหลักที่สูงกว่าไม่เป็นศูนย์ <b>ในตัวเลขทั้งจำนวน</b> (ตามแบบธนาคาร/BAHTTEXT):
///   11 = สิบเอ็ด · 21 = ยี่สิบเอ็ด · 101 = หนึ่งร้อยเอ็ด · 1,000,001 = หนึ่งล้านเอ็ด · 1 = หนึ่ง ·
///   สตางค์นับแยก (0.01 = หนึ่งสตางค์ · 1.01 = หนึ่งบาทหนึ่งสตางค์)</item>
/// <item>ศูนย์บาท: 0 → "ศูนย์บาทถ้วน" · 0.50 → "ศูนย์บาทห้าสิบสตางค์" (ขากลับ <see cref="ThaiAmountInWords.Parse"/> ต้องเห็นคำว่า "บาท")</item>
/// <item>สตางค์ = 0 → ลงท้าย "ถ้วน"</item>
/// <item>ยอดติดลบ → อ่านขนาด (ไม่มีคำว่า "ลบ") — คงพฤติกรรมเดิมของ 50 ทวิ ซึ่งเป็นสูตรเดียวที่ไม่พังกับยอดติดลบ</item>
/// </list>
/// </summary>
public static class ThaiBahtText
{
    private static readonly string[] Digits =
        { "", "หนึ่ง", "สอง", "สาม", "สี่", "ห้า", "หก", "เจ็ด", "แปด", "เก้า" };

    /// <summary>ชื่อหลักภายในกลุ่ม 6 หลัก (หลักที่ 7 ขึ้นไปวนด้วย "ล้าน")</summary>
    private static readonly string[] Places = { "", "สิบ", "ร้อย", "พัน", "หมื่น", "แสน" };

    /// <summary>จำนวนเงิน → ตัวอักษรไทย เช่น <c>"สิบสองล้านสามแสนสี่หมื่นห้าพันหกร้อยเจ็ดสิบแปดบาทถ้วน"</c></summary>
    public static string Format(decimal amount)
    {
        var a = Math.Round(Math.Abs(amount), 2, MidpointRounding.AwayFromZero);
        var baht = decimal.Truncate(a);
        var satang = (int)((a - baht) * 100m);
        if (baht == 0 && satang == 0) return "ศูนย์บาทถ้วน";

        var text = (baht == 0 ? "ศูนย์" : Integer(baht, hasHigher: false)) + "บาท";
        return text + (satang == 0 ? "ถ้วน" : Integer(satang, hasHigher: false) + "สตางค์");
    }

    /// <summary>จำนวนเต็มบวก → ตัวอักษร · วนกลุ่ม "ล้าน" ซ้ำ (ตัดกลุ่มล่าง 6 หลัก แล้วอ่านส่วนบนด้วยกติกาเดียวกัน)</summary>
    /// <param name="hasHigher">มีหลักที่สูงกว่ากลุ่มนี้ที่ไม่เป็นศูนย์ ⇒ หน่วย 1 อ่าน "เอ็ด"</param>
    private static string Integer(decimal n, bool hasHigher)
    {
        if (n >= 1_000_000m)
        {
            var low = (int)(n % 1_000_000m);
            var high = (n - low) / 1_000_000m;     // หารลงตัวเสมอ — ไม่พึ่งการปัดของ decimal
            return Integer(high, hasHigher) + "ล้าน" + (low == 0 ? "" : Group(low, hasHigher: true));
        }
        return Group((int)n, hasHigher);
    }

    /// <summary>กลุ่มเดียว 0..999,999</summary>
    private static string Group(int v, bool hasHigher)
    {
        var sb = new System.Text.StringBuilder();
        var div = 100_000;
        for (var pos = 5; pos >= 0; pos--, div /= 10)
        {
            var d = v / div % 10;
            if (d == 0) continue;
            if (pos == 1)
                sb.Append(d == 1 ? "" : d == 2 ? "ยี่" : Digits[d]).Append("สิบ");
            else if (pos == 0 && d == 1 && (hasHigher || v > 9))
                sb.Append("เอ็ด");
            else
                sb.Append(Digits[d]).Append(Places[pos]);
        }
        return sb.ToString();
    }
}
