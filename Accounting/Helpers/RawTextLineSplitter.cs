using System.Globalization;
using System.Text.RegularExpressions;

namespace Accounting.Helpers;

/// <summary>
/// **แตกรายการสินค้าจากข้อความล้วน โดยไม่ต้องพึ่ง AI**
///
/// ═══ ทำไมต้องมี (กฎเหล็ก #1 · Local-First Sovereignty · ผลตรวจ 2026-09-06 T3-02) ═══
/// เมื่อ engine ไม่คืนตาราง (Tesseract ฝัง · PDF text-layer · python ที่อ่านตารางไม่ออก)
/// ระบบเดิมมีทางเดียวคือ <c>AiFeatureKey.OcrLineItemSplit</c> — และ feature นั้น
/// <b>ไม่มี student</b> ⇒ ปิด provider = เอกสารได้บรรทัดสรุปใบเดียวตลอดกาล ซึ่งขัด
/// กฎเหล็ก #3 ข้อ 6 ("ตาราง line items ต้อง parse ครบทุกแถว") และทำให้ kill-switch test
/// ไม่ผ่านสำหรับ tenant ที่ไม่มี Azure
///
/// <para>ตัวนี้ไม่ได้ "ฉลาดกว่า AI" — มันแค่ทำให้ <b>มีคำตอบตั้งต้นเสมอ</b> แล้วปล่อยให้
/// <see cref="OcrLineSplitGuard"/> (ด่านเดียวกับที่ใช้กับคำตอบ AI) เป็นคนตัดสินว่า
/// ยอมให้กลายเป็นรายการทางบัญชีไหม — ผลรวมต้องตรงยอดบนกระดาษเท่านั้นถึงผ่าน
/// ⇒ "เดาผิด" จะถูกปฏิเสธ ไม่ใช่ถูกบันทึก</para>
///
/// <para>คืน JSON รูปเดียวกับที่โมเดลตอบ (<c>{"lines":[{description,quantity,unit,unit_price,amount}]}</c>)
/// เพื่อให้เดินผ่านด่านตัวเดิมได้โดยไม่ต้องมีเส้นทางที่สอง</para>
/// </summary>
public static class RawTextLineSplitter
{
    /// <summary>บรรทัดสรุป/ยอดรวม — ห้ามกลายเป็นรายการสินค้า</summary>
    private static readonly string[] SummaryMarkers =
    {
        "รวมเงิน", "รวมทั้งสิ้น", "ยอดรวม", "รวมสุทธิ", "จำนวนเงินรวม", "ราคารวม",
        "มูลค่าสินค้า", "ส่วนลด", "ภาษีมูลค่าเพิ่ม", "ภาษีมูลค่า", "vat", "ภาษี",
        "หัก ณ ที่จ่าย", "หักภาษี", "เงินสด", "เงินทอน", "เงินรับ", "รับเงิน",
        "total", "subtotal", "sub total", "grand", "change", "cash", "balance",
        "amount due", "ยอดชำระ", "ชำระเงิน", "โอนเงิน", "บัตรเครดิต",
        "เลขที่", "วันที่", "เลขประจำตัว", "โทร", "tel", "tax id", "สาขา",
        "ลายมือชื่อ", "ผู้รับเงิน", "ผู้มีอำนาจ", "หมายเหตุ",
    };

    /// <summary>จำนวนเงินท้ายบรรทัด (มีจุดทศนิยม 2 ตำแหน่ง หรือมีคอมมาคั่นหลักพัน)</summary>
    private static readonly Regex TrailingAmount = new(
        @"(?<amt>-?\d{1,3}(?:,\d{3})+(?:\.\d{1,2})?|-?\d+\.\d{2})\s*(?:บาท|฿|thb)?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>จำนวน+หน่วยที่นำหน้าราคา เช่น "2 ถุง x 150.00" หรือ "3 ชิ้น 45.00"</summary>
    private static readonly Regex QtyUnitPrice = new(
        @"(?<qty>\d{1,4}(?:\.\d{1,3})?)\s*(?<unit>[A-Za-zก-๛\.]{1,12})?\s*(?:x|×|@)?\s*(?<price>\d{1,3}(?:,\d{3})*(?:\.\d{1,2})?)\s*$",
        RegexOptions.Compiled);

    /// <summary>จำนวนบรรทัดสูงสุดที่ยอมรับ — มากกว่านี้แปลว่าจับข้อความทั้งหน้ามาเป็นรายการ</summary>
    public const int MaxLines = 60;

    /// <summary>แตกบรรทัดจากข้อความ · คืน <c>null</c> เมื่อหาไม่ได้เลย
    /// (ผู้เรียกต้องส่งผลลัพธ์ผ่าน <see cref="OcrLineSplitGuard"/> เสมอ)</summary>
    public static string? SplitToJson(string? rawText)
    {
        var lines = Split(rawText);
        if (lines.Count == 0) return null;
        var items = lines.Select(l => string.Format(CultureInfo.InvariantCulture,
            "{{\"description\":{0},\"quantity\":{1},\"unit\":{2},\"unit_price\":{3},\"amount\":{4}}}",
            JsonString(l.Description), l.Quantity.ToString(CultureInfo.InvariantCulture),
            l.Unit == null ? "null" : JsonString(l.Unit),
            l.UnitPrice.ToString(CultureInfo.InvariantCulture),
            l.Amount.ToString(CultureInfo.InvariantCulture)));
        return "{\"lines\":[" + string.Join(",", items) + "]}";
    }

    public sealed record Candidate(string Description, decimal Quantity, string? Unit, decimal UnitPrice, decimal Amount);

    /// <summary>บรรทัดที่ "ลงท้ายด้วยจำนวนเงิน และไม่ใช่บรรทัดสรุป"</summary>
    public static List<Candidate> Split(string? rawText)
    {
        var result = new List<Candidate>();
        if (string.IsNullOrWhiteSpace(rawText)) return result;

        foreach (var raw in rawText.Split('\n'))
        {
            var line = raw.Replace('\t', ' ').Trim();
            if (line.Length < 4) continue;

            var lower = line.ToLowerInvariant();
            if (SummaryMarkers.Any(m => lower.Contains(m))) continue;

            var m = TrailingAmount.Match(line);
            if (!m.Success) continue;
            if (!TryMoney(m.Groups["amt"].Value, out var amount) || amount <= 0m) continue;

            var head = line[..m.Index].Trim().TrimEnd('.', ',', ';', ':', '-', '|');
            if (head.Length < 2) continue;
            // ต้องมีตัวอักษรอย่างน้อย 2 ตัว — กันแถวที่เป็นตัวเลขล้วน (เลขที่/รหัส/บาร์โค้ด)
            if (head.Count(char.IsLetter) < 2) continue;

            decimal qty = 1m, unitPrice = amount;
            string? unit = null;
            var qm = QtyUnitPrice.Match(head);
            if (qm.Success
                && TryMoney(qm.Groups["price"].Value, out var price) && price > 0m
                && decimal.TryParse(qm.Groups["qty"].Value, NumberStyles.Number,
                    CultureInfo.InvariantCulture, out var q) && q > 0m
                // ยอมรับเฉพาะเมื่อ qty × ราคา = ยอดบรรทัดจริง (ไม่งั้นคือเลขคนละเรื่อง)
                && Math.Abs(q * price - amount) <= 0.05m)
            {
                var desc = head[..qm.Index].Trim().TrimEnd('.', ',', ';', ':', '-', '|', 'x', '×', '@');
                if (desc.Count(char.IsLetter) >= 2)
                {
                    qty = q;
                    unitPrice = price;
                    unit = qm.Groups["unit"].Success && qm.Groups["unit"].Value.Count(char.IsLetter) >= 1
                        ? qm.Groups["unit"].Value.Trim('.', ' ')
                        : null;
                    head = desc;
                }
            }

            result.Add(new Candidate(head, qty, unit, unitPrice, amount));
            if (result.Count > MaxLines) return new List<Candidate>();   // จับมั่ว = ไม่ตอบดีกว่า
        }
        return result;
    }

    private static bool TryMoney(string s, out decimal value)
        => decimal.TryParse(s.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out value);

    private static string JsonString(string s)
        => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", " ") + "\"";
}
