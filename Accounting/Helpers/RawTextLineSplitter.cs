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
    public static string? SplitToJson(string? rawText) => SplitToJson(rawText, null, null);

    /// <summary>แตกบรรทัดโดยรู้ยอดบนกระดาษ (ยอดก่อน VAT / ยอดรวม) — ใช้เลือกระหว่างแถวตารางกับกติกาท้ายบรรทัด
    /// (<see cref="Split(string?, decimal?, decimal?)"/>) · ผู้เรียกยังต้องส่งผลผ่าน <see cref="OcrLineSplitGuard"/></summary>
    public static string? SplitToJson(string? rawText, decimal? subTotal, decimal? total)
    {
        var lines = Split(rawText, subTotal, total);
        return lines.Count == 0 ? null : ToJson(lines);
    }

    private static string ToJson(IEnumerable<Candidate> lines)
    {
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

    /// <summary>ตัวคั่นช่องของตาราง — tab (<see cref="PdfPositionalText.ColumnSeparator"/> ของข้อความ PDF ที่ประกอบตามพิกัด · ทีม OCR 13) หรือช่องว่างตั้งแต่สองตัว</summary>
    private static readonly Regex CellSeparator = new(@" {2,}|\t", RegexOptions.Compiled);

    /// <summary>ตัวเลขทั้งช่อง (คอมมาหลักพัน · ทศนิยม) — ช่องตารางต้องเป็นตัวเลขล้วน ไม่ใช่ "มีตัวเลขอยู่ข้างใน" · ทีม OCR 13: ยอมคำว่า "บาท/฿/THB" ต่อท้ายช่องเงิน
    /// ("1,000.00 บาท" — เดิมทั้งแถวหลุด)</summary>
    private static readonly Regex CellNumber = new(
        @"^(?<n>(?:\d{1,3}(?:,\d{3})+|\d+)(?:\.\d+)?)(?:\s*(?:บาท|฿|thb))?$", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>ช่องจำนวนที่พิมพ์หน่วยไว้ในช่องเดียวกัน ("2 ชิ้น" · "3.5 กก.") — ทีม OCR 13 (เดิมทั้งแถวหลุด)</summary>
    private static readonly Regex CellQuantity = new(
        @"^(?<n>(?:\d{1,3}(?:,\d{3})+|\d+)(?:\.\d+)?)\s*(?<unit>[A-Za-zก-๛][A-Za-zก-๛\.]{0,11})?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>เลขลำดับแถวหน้าคำอธิบาย ("1. " · "12) ")</summary>
    private static readonly Regex RowNumber = new(@"^\d{1,3}[.)]\s*", RegexOptions.Compiled);

    /// <summary>บรรทัดที่ "ลงท้ายด้วยจำนวนเงิน และไม่ใช่บรรทัดสรุป" — ข้อความที่มีหัวตาราง (ช่องคั่นด้วย tab/ช่องว่าง ≥ 2) อ่านตามคอลัมน์ก่อน
    /// (<see cref="TableRows(string?)"/> · ทีม OCR 10) · ไม่เจอตาราง = กติกาท้ายบรรทัดเดิมทุกตัวอักษร</summary>
    public static List<Candidate> Split(string? rawText) => Split(rawText, null, null);

    /// <summary>
    /// แตกบรรทัด — <b>รู้ยอดบนกระดาษ</b> ⇒ เลือกระหว่างแถวตารางกับกติกาท้ายบรรทัดด้วยด่าน Σ ตัวเดียวกับผู้เรียก (<see cref="OcrLineSplitGuard"/>)
    ///
    /// <para>ทีม OCR 13 (ฝ่ายค้าน OCR 10 P1-3): เดิมเจอแถวตารางแม้แถวเดียว ⇒ ทิ้งแถวที่เหลือและไม่ลองกติกาท้ายบรรทัด ⇒ Σ ไม่ตรง ⇒ ด่านปฏิเสธ ⇒ เอกสารได้ 0 บรรทัด
    /// (ช่องส่วนลดว่าง · ยอดมีคำว่า "บาท" · จำนวนรวมหน่วย "2 ชิ้น") · ตอนนี้: <b>รันทั้งสองแบบบนข้อความเดียวกัน แล้วเก็บแบบที่ Σ ตรงยอด</b> (ตารางก่อน) ·
    /// ไม่ตรงทั้งคู่ ⇒ ตาราง (ด่านของผู้เรียกปฏิเสธเหมือนเดิม) · ไม่รู้ยอด ⇒ ตารางที่ข้ามแถวหน้าตาเป็นรายการ ถอยไปกติกาท้ายบรรทัด</para>
    /// </summary>
    public static List<Candidate> Split(string? rawText, decimal? subTotal, decimal? total)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return new List<Candidate>();
        var table = TableRows(rawText, out var skipped);
        if (table.Count == 0) return TrailingRows(rawText);
        if (table.Count > MaxLines) return new List<Candidate>();
        if ((subTotal ?? 0m) > 0m || (total ?? 0m) > 0m)
        {
            if (Reconciles(table, subTotal, total)) return table;
            var trailing = TrailingRows(rawText);
            return trailing.Count > 0 && Reconciles(trailing, subTotal, total) ? trailing : table;
        }
        if (skipped > 0)
        {
            var trailing = TrailingRows(rawText);
            if (trailing.Count > 0) return trailing;
        }
        return table;
    }

    /// <summary>Σ ของชุดบรรทัดผ่านด่านเดียวกับผู้เรียก (<see cref="OcrLineSplitGuard.Evaluate"/> — รวมการตัดแถวซ้ำต้นฉบับ+สำเนา)</summary>
    private static bool Reconciles(List<Candidate> lines, decimal? subTotal, decimal? total)
    {
        return OcrLineSplitGuard.Evaluate(ToJson(lines), subTotal ?? 0m, total ?? 0m).Accepted;
    }

    /// <summary>กติกาท้ายบรรทัดเดิม (ก่อนทีม OCR 10) — ทุกบรรทัดที่ลงท้ายด้วยจำนวนเงินและไม่ใช่บรรทัดหัว/สรุป</summary>
    private static List<Candidate> TrailingRows(string rawText)
    {
        var result = new List<Candidate>();
        foreach (var raw in rawText.Split('\n'))
        {
            if (TrailingCandidate(raw) is not { } c) continue;
            result.Add(c);
            if (result.Count > MaxLines) return new List<Candidate>();   // จับมั่ว = ไม่ตอบดีกว่า
        }
        return result;
    }

    private static Candidate? TrailingCandidate(string raw)
    {
        var line = raw.Replace('\t', ' ').Trim();
        if (line.Length < 4) return null;

        var lower = line.ToLowerInvariant();
        if (MetaMarkers.Any(m => lower.Contains(m))) return null;
        if (OcrNonItemRow.MentionsSummaryLabel(line)) return null;

        var m = TrailingAmount.Match(line);
        if (!m.Success) return null;
        if (!TryMoney(m.Groups["amt"].Value, out var amount) || amount <= 0m) return null;

        var head = line[..m.Index].Trim().TrimEnd('.', ',', ';', ':', '-', '|');
        if (head.Length < 2) return null;
        // ต้องมีตัวอักษรอย่างน้อย 2 ตัว — กันแถวที่เป็นตัวเลขล้วน (เลขที่/รหัส/บาร์โค้ด)
        if (head.Count(char.IsLetter) < 2) return null;

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
        return new Candidate(head, qty, unit, unitPrice, amount);
    }

    /// <summary>
    /// **แถวตารางตามคอลัมน์** (ทีม OCR 10 · 2026-10-16) — ข้อความ PDF ที่ประกอบตามพิกัด (<see cref="PdfPositionalText"/>) คั่นช่องตารางด้วย tab (เดิมช่องว่าง ≥ 2) ⇒
    /// หัวตาราง (ทุกช่องไม่มีตัวเลข · ≥ 3 ช่อง · บทบาทจาก <see cref="OcrTableColumnMapper"/> ตัวเดียวกับตาราง Azure: ต้องมีคำอธิบาย + ยอด + (จำนวนหรือราคา)) ·
    /// แถวรายการ = บรรทัดใต้หัวที่<b>จำนวนช่องเท่าหัว</b> และช่องตัวเลขทุกช่องเป็นตัวเลขล้วน · ไม่ลงตัว = ไม่ใช่แถว (บรรทัดคำอธิบายต่อ/หัวภาษาอังกฤษ/แถวสรุป)
    ///
    /// <para>ทีม OCR 13 (ฝ่ายค้าน OCR 10 P1-3/P2) — แถวที่ขาดหนึ่งช่อง: (ก) ช่องว่างหนึ่งช่องกลางแถว (ส่วนลดว่าง) หรือ จำนวน+หน่วยรวมช่องเดียว ("2 ชิ้น") ⇒
    /// ลองวางช่องที่เหลือทุกตำแหน่งที่ "ว่างได้" แล้วรับเฉพาะเมื่อ<b>มีวิธีเดียว</b>ที่ จำนวน × ราคา = ยอด (ไม่มีคอลัมน์ราคา = ไม่เดา) · (ข) ตัวเลขจัดกึ่งกลางแนวตั้ง
    /// ระหว่างคำอธิบายสองบรรทัด (แถวมีแต่ตัวเลข ขาดช่องคำอธิบาย) ⇒ คำอธิบาย = บรรทัดข้อความล้วนข้างบน + ข้างล่าง · ช่องเงินมี "บาท" ต่อท้ายได้ ·
    /// แถวใต้หัวที่หน้าตาเป็นรายการ (ลงท้ายด้วยยอด) แต่อ่านตามคอลัมน์ไม่ได้ นับใน <paramref name="skipped"/> ⇒ <see cref="Split(string?, decimal?, decimal?)"/> ถอยไปกติกาท้ายบรรทัด</para>
    ///
    /// <para>ที่มา: ใบ RE-20261000111 (พรามี่) "1.00ชิ้น1,330.00293.007%1,037.00" — ข้อความเดิมช่องติดกัน ⇒ กติกาท้ายบรรทัดได้ "ราคา" 1,037 + คำอธิบายที่มีตัวเลขทั้งแถว ·
    /// บิล กฟภ. "ค่าไฟฟ้า ประจำเดือน 08/2569  4,272.00  19,381.50" (จำนวนหน่วย · จำนวนเงิน · ไม่มีคอลัมน์ราคา) ⇒ จำนวน 4,272 ราคาว่าง (ตัวกันจำนวนตัดสิน)</para>
    ///
    /// <para><b>ไม่ตัดสินเงิน</b>: ไม่หาร ยอด ÷ จำนวน · ไม่อ่านคอลัมน์ส่วนลด/อัตรา VAT (ส่วนลดบนแถวเป็นหน้าที่ของ <see cref="OcrLineQuantityGuard"/> ตัวเดียว) ·
    /// ผลยังต้องผ่าน <see cref="OcrLineSplitGuard"/> (Σ = ยอดบนกระดาษ) เหมือนคำตอบโมเดล</para>
    /// </summary>
    internal static List<Candidate> TableRows(string? rawText) => TableRows(rawText, out _);

    internal static List<Candidate> TableRows(string? rawText, out int skipped)
    {
        skipped = 0;
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
            var lastRow = -1;      // บรรทัดของแถวล่าสุด — บรรทัดข้อความต่อจากแถว = คำอธิบายต่อ ไม่ใช่คำอธิบายของแถวถัดไป
            var usedText = -1;     // บรรทัดข้อความที่ถูกใช้เป็นคำอธิบายของแถวกึ่งกลางแล้ว
            var pendingSkipped = 0; // บรรทัดหน้าตารายการที่อ่านตามคอลัมน์ไม่ได้ — นับเป็น "ข้ามแถว" เมื่อมีแถวตารางตามมา (อยู่กลางตาราง ไม่ใช่ท้ายหน้า)
            for (; j < lines.Length; j++)
            {
                var cells = Cells(lines[j]);
                var row = TableRow(cells, header.Count, cols) ?? GappedRow(cells, header.Count, cols);
                if (row == null && CenteredRow(cells, header.Count, cols) is { } centred)
                {
                    var above = j - 1 > i && j - 1 != lastRow && j - 1 != usedText && IsPlainText(lines[j - 1]) ? lines[j - 1].Trim() : null;
                    var below = j + 1 < lines.Length && IsPlainText(lines[j + 1]) ? lines[j + 1].Trim() : null;
                    var desc = string.Join(" ", new[] { above, below }.Where(s => !string.IsNullOrEmpty(s))).Replace('\t', ' ');
                    if (desc.Count(char.IsLetter) >= 2 && !OcrNonItemRow.IsSummaryOrTenderRow(desc))
                    {
                        row = centred with { Description = desc };
                        if (below != null) { usedText = j + 1; j++; }
                    }
                }
                if (row != null) { result.Add(row); lastRow = j; skipped += pendingSkipped; pendingSkipped = 0; continue; }
                // แถวสรุปหลังรายการแรก = จบตาราง (หัวตารางถัดไป เช่น หน้าสำเนา เริ่มนับใหม่ที่ลูปนอก)
                if (result.Count > 0 && OcrNonItemRow.MentionsSummaryLabel(lines[j])) break;
                if (TrailingCandidate(lines[j]) != null) pendingSkipped++;
            }
            i = j;
        }
        return result;
    }

    private static Candidate? TableRow(List<string> cells, int width, OcrTableColumns cols)
    {
        if (cells.Count != width) return null;
        return RowFrom(cells, Enumerable.Range(0, width).ToArray(), cols, false, out _);
    }

    /// <summary>แถวที่ขาดหนึ่งช่อง (ช่องว่างกลางแถว · จำนวน+หน่วยรวมช่องเดียว) — ลองให้แต่ละคอลัมน์ที่ "ว่างได้" (ไม่ใช่คำอธิบาย/ยอด) เป็นช่องที่ขาด ·
    /// รับเฉพาะเมื่อมีวิธีวางเดียวที่ จำนวน × ราคา = ยอด (±0.05) · ตารางไม่มีคอลัมน์ราคา ⇒ ไม่มีอะไรพิสูจน์ ⇒ ไม่รับ</summary>
    private static Candidate? GappedRow(List<string> cells, int width, OcrTableColumns cols)
    {
        if (cells.Count != width - 1 || cols.UnitPrice < 0) return null;
        Candidate? found = null;
        for (var missing = 0; missing < width; missing++)
        {
            if (missing == cols.Description || missing == cols.Amount) continue;
            var map = Enumerable.Range(0, width).Select(c => c < missing ? c : c == missing ? -1 : c - 1).ToArray();
            var row = RowFrom(cells, map, cols, true, out var proven);
            if (row == null || !proven) continue;
            if (found != null && found != row) return null;   // วางได้สองแบบ = ไม่รู้ ⇒ ไม่เดา
            found = row;
        }
        return found;
    }

    /// <summary>แถวที่มีแต่ตัวเลข ขาดช่องคำอธิบาย (ตัวเลขจัดกึ่งกลางแนวตั้งระหว่างคำอธิบายสองบรรทัด) — คืนแถวที่คำอธิบายว่าง ให้ผู้เรียกเติมจากบรรทัดข้างเคียง</summary>
    private static Candidate? CenteredRow(List<string> cells, int width, OcrTableColumns cols)
    {
        if (cells.Count != width - 1 || cols.Description != 0 || !CellQuantity.IsMatch(cells[0])) return null;
        // ช่องที่เหลือ: ตัวเลข/อัตรา % หรือคำหน่วยสั้น ๆ คำเดียว — มีข้อความยาว = แถวคำอธิบายที่ขาดช่องอื่น ไม่ใช่ตัวเลขจัดกึ่งกลาง
        if (cells.Any(c => !CellQuantity.IsMatch(c) && !c.EndsWith('%') && (c.Any(char.IsDigit) || c.Contains(' ') || c.Length > 12))) return null;
        var padded = new List<string> { "xx" };
        padded.AddRange(cells);
        var row = RowFrom(padded, Enumerable.Range(0, width).ToArray(), cols, false, out _);
        return row == null ? null : row with { Description = "" };
    }

    /// <summary>บรรทัดข้อความล้วน (ไม่มีช่องตัวเลข · ไม่ใช่ป้ายสรุป) — คำอธิบายบรรทัดต่อที่ตัวเลขจัดกึ่งกลางอยู่ระหว่าง</summary>
    private static bool IsPlainText(string line)
    {
        var cells = Cells(line);
        return cells.Count == 1 && cells[0].Count(char.IsLetter) >= 2 && !CellQuantity.IsMatch(cells[0])
               && !OcrNonItemRow.MentionsSummaryLabel(line) && TrailingAmount.Match(cells[0]).Success == false;
    }

    /// <param name="map">ตำแหน่งในแถวของแต่ละคอลัมน์หัว (-1 = ช่องว่าง)</param>
    /// <param name="strictOthers">ช่องที่ไม่ได้อ่าน (ส่วนลด/อัตรา) ต้องไม่ใช่ข้อความ — ใช้กับแถวที่ขาดช่อง (กันวางช่องผิดตำแหน่ง) · แถวเต็มคงกติกาเดิม</param>
    /// <param name="proven">จำนวน × ราคา = ยอด (มีคอลัมน์ราคาและตรง)</param>
    private static Candidate? RowFrom(List<string> cells, int[] map, OcrTableColumns cols, bool strictOthers, out bool proven)
    {
        proven = false;
        string? Cell(int col) => col < 0 || map[col] < 0 ? null : cells[map[col]];
        var desc = RowNumber.Replace(Cell(cols.Description) ?? "", "").Trim();
        if (desc.Count(char.IsLetter) < 2 || OcrNonItemRow.IsSummaryOrTenderRow(desc)) return null;
        if (Cell(cols.Amount) is not { } amountCell || !TryCell(amountCell, out var amount) || amount <= 0m) return null;
        decimal qty = 1m;
        string? cellUnit = null;
        if (cols.Quantity >= 0)
        {
            if (Cell(cols.Quantity) is not { } qtyCell || !TryQuantity(qtyCell, out qty, out cellUnit) || qty <= 0m) return null;
        }
        decimal? price = null;
        if (cols.UnitPrice >= 0)
        {
            if (Cell(cols.UnitPrice) is not { } priceCell || !TryCell(priceCell, out var p)) return null;
            price = p;
            proven = Math.Abs(qty * p - amount) <= 0.05m;
        }
        else if (cols.Quantity < 0) price = amount;   // ไม่มีทั้งคอลัมน์จำนวนและราคา — ไม่เกิด (หัวต้องมีอย่างใดอย่างหนึ่ง) · คงรูปเดิม 1 × ยอด
        // ช่องอื่นที่ไม่ได้อ่าน (ส่วนลด/อัตรา) ต้องไม่ใช่ข้อความ — ไม่งั้นการวางช่องผิดแถว
        for (var c = 0; strictOthers && c < map.Length; c++)
        {
            if (c == cols.Description || c == cols.Amount || c == cols.Quantity || c == cols.UnitPrice || c == cols.Unit || map[c] < 0) continue;
            var other = cells[map[c]];
            if (other.Count(char.IsLetter) >= 2 && !other.EndsWith('%')) return null;
        }
        var unitCell = Cell(cols.Unit);
        var unit = unitCell != null && unitCell.Any(char.IsLetter) && !unitCell.Any(char.IsDigit) ? unitCell : cellUnit;
        return new Candidate(desc, qty, unit, price, amount);
    }

    private static List<string> Cells(string line)
        => CellSeparator.Split(line.Trim()).Select(c => c.Trim()).Where(c => c.Length > 0).ToList();

    private static bool TryCell(string cell, out decimal value)
    {
        value = 0m;
        var m = CellNumber.Match(cell);
        return m.Success && TryMoney(m.Groups["n"].Value, out value);
    }

    private static bool TryQuantity(string cell, out decimal value, out string? unit)
    {
        value = 0m;
        unit = null;
        var m = CellQuantity.Match(cell);
        if (!m.Success || !TryMoney(m.Groups["n"].Value, out value)) return false;
        if (m.Groups["unit"].Success && m.Groups["unit"].Value.Length > 0) unit = m.Groups["unit"].Value.TrimEnd('.');
        return true;
    }

    private static bool TryMoney(string s, out decimal value)
        => decimal.TryParse(s.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out value);

    /// <summary>สตริง JSON — ทีม OCR 13: escape อักขระควบคุมทุกตัว (เดิม "\0" จาก PDF HeadlessChrome ทำ JSON พัง ⇒ ด่าน Σ ปฏิเสธ "ไม่ใช่ JSON" ทั้งชุด)</summary>
    private static string JsonString(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length + 2).Append('"');
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': case '\r': case '\t': sb.Append(' '); break;
                default:
                    if (char.IsControl(ch)) sb.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(ch);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }
}
