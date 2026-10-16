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
    /// <summary>บรรทัดหัว/ท้ายเอกสารที่ไม่ใช่รายการ (เลขที่ · วันที่ · เลขภาษี · โทร · สาขา · ลายเซ็น · หมายเหตุ) — ห้ามกลายเป็นรายการสินค้า
    /// · คำสรุป/ชำระ/เงินทอน <b>ไม่อยู่ที่นี่</b>: ตัวตัดสินตัวเดียวคือ <see cref="OcrNonItemRow.MentionsSummaryLabel"/>
    /// (ฝ่ายค้าน 2026-10-09 F2 ข้อ 4 — เดิมมีสำเนาคำสรุปชุดที่สองที่ไม่ตรงกับ OcrNonItemRow)</summary>
    private static readonly string[] MetaMarkers =
    {
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
            l.UnitPrice?.ToString(CultureInfo.InvariantCulture) ?? "null",
            l.Amount.ToString(CultureInfo.InvariantCulture)));
        return "{\"lines\":[" + string.Join(",", items) + "]}";
    }

    /// <param name="UnitPrice">ราคาต่อหน่วย — null = กระดาษไม่พิมพ์ (ตารางมีคอลัมน์จำนวนแต่ไม่มีคอลัมน์ราคา · บิลมิเตอร์) ⇒ ตัวกันจำนวน
    /// (<see cref="OcrLineQuantityGuard"/>) ตัดสิน ห้ามหาร ยอด ÷ จำนวน ที่นี่ (ทีม OCR 8)</param>
    public sealed record Candidate(string Description, decimal Quantity, string? Unit, decimal? UnitPrice, decimal Amount);

    /// <summary>ตัวคั่นช่องของตาราง — ช่องว่างตั้งแต่สองตัว (<see cref="PdfPositionalText.ColumnSeparator"/> ของข้อความ PDF ที่ประกอบตามพิกัด) หรือ tab</summary>
    private static readonly Regex CellSeparator = new(@" {2,}|\t", RegexOptions.Compiled);

    /// <summary>ตัวเลขทั้งช่อง (คอมมาหลักพัน · ทศนิยม) — ช่องตารางต้องเป็นตัวเลขล้วน ไม่ใช่ "มีตัวเลขอยู่ข้างใน"</summary>
    private static readonly Regex CellNumber = new(
        @"^(?:\d{1,3}(?:,\d{3})+|\d+)(?:\.\d+)?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>เลขลำดับแถวหน้าคำอธิบาย ("1. " · "12) ")</summary>
    private static readonly Regex RowNumber = new(@"^\d{1,3}[.)]\s*", RegexOptions.Compiled);

    /// <summary>บรรทัดที่ "ลงท้ายด้วยจำนวนเงิน และไม่ใช่บรรทัดสรุป" — ข้อความที่มีหัวตาราง (ช่องคั่นด้วยช่องว่าง ≥ 2) อ่านตามคอลัมน์ก่อน
    /// (<see cref="TableRows"/> · ทีม OCR 10) · ไม่เจอตาราง = กติกาท้ายบรรทัดเดิมทุกตัวอักษร</summary>
    public static List<Candidate> Split(string? rawText)
    {
        var result = new List<Candidate>();
        if (string.IsNullOrWhiteSpace(rawText)) return result;
        var table = TableRows(rawText);
        if (table.Count > 0) return table.Count > MaxLines ? new List<Candidate>() : table;

        foreach (var raw in rawText.Split('\n'))
        {
            var line = raw.Replace('\t', ' ').Trim();
            if (line.Length < 4) continue;

            var lower = line.ToLowerInvariant();
            if (MetaMarkers.Any(m => lower.Contains(m))) continue;
            if (OcrNonItemRow.MentionsSummaryLabel(line)) continue;

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

    /// <summary>
    /// **แถวตารางตามคอลัมน์** (ทีม OCR 10 · 2026-10-16) — ข้อความ PDF ที่ประกอบตามพิกัด (<see cref="PdfPositionalText"/>) คั่นช่องตารางด้วยช่องว่าง ≥ 2 ตัว ⇒
    /// หัวตาราง (ทุกช่องไม่มีตัวเลข · ≥ 3 ช่อง · บทบาทจาก <see cref="OcrTableColumnMapper"/> ตัวเดียวกับตาราง Azure: ต้องมีคำอธิบาย + ยอด + (จำนวนหรือราคา)) ·
    /// แถวรายการ = บรรทัดใต้หัวที่<b>จำนวนช่องเท่าหัว</b> และช่องตัวเลขทุกช่องเป็นตัวเลขล้วน · ไม่ลงตัว = ไม่ใช่แถว (บรรทัดคำอธิบายต่อ/หัวภาษาอังกฤษ/แถวสรุป)
    ///
    /// <para>ที่มา: ใบ RE-20261000111 (พรามี่) "1.00ชิ้น1,330.00293.007%1,037.00" — ข้อความเดิมช่องติดกัน ⇒ กติกาท้ายบรรทัดได้ "ราคา" 1,037 + คำอธิบายที่มีตัวเลขทั้งแถว ·
    /// บิล กฟภ. "ค่าไฟฟ้า ประจำเดือน 08/2569  4,272.00  19,381.50" (จำนวนหน่วย · จำนวนเงิน · ไม่มีคอลัมน์ราคา) ⇒ จำนวน 4,272 ราคาว่าง (ตัวกันจำนวนตัดสิน)</para>
    ///
    /// <para><b>ไม่ตัดสินเงิน</b>: ไม่หาร ยอด ÷ จำนวน · ไม่อ่านคอลัมน์ส่วนลด/อัตรา VAT (ส่วนลดบนแถวเป็นหน้าที่ของ <see cref="OcrLineQuantityGuard"/> ตัวเดียว) ·
    /// ผลยังต้องผ่าน <see cref="OcrLineSplitGuard"/> (Σ = ยอดบนกระดาษ) เหมือนคำตอบโมเดล</para>
    /// </summary>
    internal static List<Candidate> TableRows(string? rawText)
    {
        var result = new List<Candidate>();
        if (string.IsNullOrWhiteSpace(rawText)) return result;
        var lines = rawText.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var header = Cells(lines[i]);
            if (header.Count < 3 || header.Any(c => c.Any(char.IsDigit))) continue;
            var cols = OcrTableColumnMapper.Map(header);
            if (cols.Description < 0 || cols.Amount < 0 || (cols.Quantity < 0 && cols.UnitPrice < 0)) continue;
            var j = i + 1;
            for (; j < lines.Length; j++)
            {
                var row = TableRow(Cells(lines[j]), header.Count, cols);
                if (row != null) { result.Add(row); continue; }
                // แถวสรุปหลังรายการแรก = จบตาราง (หัวตารางถัดไป เช่น หน้าสำเนา เริ่มนับใหม่ที่ลูปนอก)
                if (result.Count > 0 && OcrNonItemRow.MentionsSummaryLabel(lines[j])) break;
            }
            i = j;
        }
        return result;
    }

    private static Candidate? TableRow(List<string> cells, int width, OcrTableColumns cols)
    {
        if (cells.Count != width) return null;
        var desc = RowNumber.Replace(cells[cols.Description], "").Trim();
        if (desc.Count(char.IsLetter) < 2 || OcrNonItemRow.IsSummaryOrTenderRow(desc)) return null;
        if (!TryCell(cells[cols.Amount], out var amount) || amount <= 0m) return null;
        decimal qty = 1m;
        if (cols.Quantity >= 0 && (!TryCell(cells[cols.Quantity], out qty) || qty <= 0m)) return null;
        decimal? price = null;
        if (cols.UnitPrice >= 0)
        {
            if (!TryCell(cells[cols.UnitPrice], out var p)) return null;
            price = p;
        }
        else if (cols.Quantity < 0) price = amount;   // ไม่มีทั้งคอลัมน์จำนวนและราคา — ไม่เกิด (หัวต้องมีอย่างใดอย่างหนึ่ง) · คงรูปเดิม 1 × ยอด
        var unit = cols.Unit >= 0 && cells[cols.Unit].Any(char.IsLetter) && !cells[cols.Unit].Any(char.IsDigit) ? cells[cols.Unit] : null;
        return new Candidate(desc, qty, unit, price, amount);
    }

    private static List<string> Cells(string line)
        => CellSeparator.Split(line.Trim()).Select(c => c.Trim()).Where(c => c.Length > 0).ToList();

    private static bool TryCell(string cell, out decimal value)
    {
        value = 0m;
        return CellNumber.IsMatch(cell) && TryMoney(cell, out value);
    }

    private static bool TryMoney(string s, out decimal value)
        => decimal.TryParse(s.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out value);

    private static string JsonString(string s)
        => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", " ") + "\"";
}
