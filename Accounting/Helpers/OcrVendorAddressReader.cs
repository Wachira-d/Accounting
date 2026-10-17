using System.Text.RegularExpressions;

namespace Accounting.Helpers;

/// <summary>
/// **ที่อยู่ผู้ขายจากป้าย "ที่อยู่/ADDRESS" บนกระดาษ** — ตัวอ่านเดียวของสองจุดเรียกใน <c>OcrService</c> (<c>ParseThaiDocument</c> · เติมจากข้อความเมื่อ engine ไม่ให้)
/// (ทีม OCR 13 · ย้ายจาก <c>OcrService.VendorAddressRegex</c> — ตัวตัดสิน OCR อยู่ใน Helpers พร้อมเทสต์)
///
/// <para>ทีม OCR 13 (ฝ่ายค้าน OCR 10 P1-4): (1) ป้ายที่อยู่ที่อยู่<b>ในบล็อกผู้ซื้อ</b> (ป้ายฝั่งที่ใกล้ที่สุดก่อนหน้าเป็นป้ายผู้ซื้อ — "ชื่อลูกค้า/ Customer name … ที่อยู่/ Address …" ·
/// <see cref="OcrPartyLabels.InBuyerBlock"/>) ไม่ใช่ที่อยู่ผู้ขาย ⇒ null (เดิม e-Tax TXE05202609T004679 ได้ที่อยู่เราเป็นที่อยู่ผู้ขาย แล้วถูกผ่าให้ผู้ซื้อได้
/// "18/09/2026 สาขา/ Branch …") · (2) ตัดทุกบรรทัดที่ตัวคั่นคอลัมน์ (tab — <see cref="PdfPositionalText.ColumnSeparator"/>) — คอลัมน์ขวา (วันที่/เลขที่) ไม่ใช่ที่อยู่</para>
/// </summary>
public static class OcrVendorAddressReader
{
    // ⚠️ ทีม OCR 10 (2026-10-16): รูปเดิม `((?:[^\n]+\n?){1,4}?)` แบ่งบรรทัดยาวเป็นท่อนได้ทุกแบบ (\n ไม่บังคับ) ⇒ ตัวหน้าต่อท้ายไม่เจอป้าย =
    // ลองทุกวิธีแบ่ง O(n⁴) · ข้อความ PDF แบบเดิม (page.Text บรรทัดเดียว ~2,900 ตัว เช่น บิล กฟภ.) + "──── TESSERACT ────" ต่อท้ายในเส้น Tier 3 ⇒
    // ParseThaiDocument ค้าง (probe: CPU 16 นาทีไม่จบ) · รูปใหม่ = บรรทัดแรก + ต่อได้อีก 3 บรรทัดเต็ม (ท่อนละบรรทัด แบ่งได้วิธีเดียว)
    // (เทสต์ OcrPdfTextPathTests · ที่อยู่ 1–4 บรรทัด · เว้นบรรทัดก่อนป้าย · เกิน 4 บรรทัดไม่จับ)
    // ⚠️ แก้คำอ้าง (ทีม OCR 13 · ฝ่ายค้าน OCR 10 P2): ผล<b>ไม่</b>เท่าเดิมทุกเคส — fuzz 300k ข้อความ ต่างกัน 5,065 (รูปเดิม timeout 53) · `\n?` ของรูปเดิมให้ท่อนกลาง
    // ข้ามบรรทัดป้ายตัวแรกได้ ("ที่อยู่: 99 ถนน…\nโทร 02-111\nเลขประจำตัว…" ⇒ เดิม "99 ถนน… โทร 02-111" · ใหม่ "99 ถนน…") — รูปใหม่หยุดที่ป้ายแรก (ถูกกว่า) ·
    // ช่อง VendorAddress อยู่ใน replay snapshot แล้ว (OcrReplayHarness · แถว VendorAddress) — ขยับเมื่อไรเห็น
    private static readonly Regex VendorAddressRegex = new(
        @"(?:ที่อยู่|ADDRESS)\s*[:：]?\s*([^\n]+(?:\n[^\n]+){0,3}?)(?=\n\s*(?:โทร|TEL|เลขประจำตัว|TAX\s*ID|อีเมล|EMAIL|FAX|$))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <param name="text">ข้อความทั้งใบ (normalize แล้ว)</param>
    /// <param name="keepLineBreaks">คงการขึ้นบรรทัดของที่อยู่หลายบรรทัด (เส้นเติมจากข้อความ) · false = ต่อด้วยช่องว่าง (ParseThaiDocument)</param>
    public static string? Read(string? text, bool keepLineBreaks = false)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var m = VendorAddressRegex.Match(text);
        if (!m.Success || OcrPartyLabels.InBuyerBlock(text, m.Index)) return null;
        // ทีม OCR 15 (ฝ่ายค้าน OCR 13 P2 · มีตั้งแต่ base): บรรทัดที่ขึ้นบล็อกผู้ซื้อ ("ผู้ซื้อ: บริษัท บี จำกัด") จบที่อยู่ผู้ขาย — เดิมที่อยู่ไหลต่อถึงป้ายท้าย
        // (โทร/เลขประจำตัว) ⇒ "…10110 ผู้ซื้อ: บริษัทบีจำกัด" · บรรทัดแรก (บรรทัดป้ายที่อยู่) ไม่ตรวจ — ป้ายผู้ซื้อก่อนป้ายที่อยู่ถูกตัดสินแล้วด้วย InBuyerBlock
        var raw = m.Groups[1].Value.Replace("\r", "").Split('\n');
        var kept = raw.TakeWhile((l, i) => i == 0 || OcrPartyLabels.FindBuyer(l) < 0);
        var lines = kept
            .Select(l => (l.IndexOf('\t') is var tab and >= 0 ? l[..tab] : l).Trim())
            .Where(l => l.Length > 0);
        return string.Join(keepLineBreaks ? "\n" : " ", lines).Trim();
    }
}
