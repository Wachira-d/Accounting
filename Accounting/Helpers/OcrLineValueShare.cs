namespace Accounting.Helpers;

/// <summary>บรรทัดที่มีเงินบนใบ (คำอธิบาย + ยอดก่อน VAT) — ตัวป้อนของ <see cref="OcrLineValueShare"/></summary>
public readonly record struct OcrPricedLine(string? Description, decimal Amount);

/// <summary>
/// **"บรรทัดที่ทำให้หมวดนี้ชนะ คิดเป็นมูลค่ากี่ส่วนของใบ"** — ตัวตัดสินเดียวของไปป์ไลน์ OCR (pure)
///
/// <para>═══ ที่มา (สแกนจริง f1690d11 · 2026-10-09 · ซีอาร์ซี ไทวัสดุ SRCIE26100075384 · บริษัทโรงแรม) ═══
/// ใบซื้ออุปกรณ์แอร์/วัสดุ 8 บรรทัด 2,954.21 บาท + "ค่าขนส่ง CTD" 37.38 บาท (1.25% ของมูลค่า) ⇒ ตัวจัดหมวด
/// รวมคำทุกบรรทัดเป็นก้อนเดียว ไม่ดูยอด ⇒ หมวด "ค่าขนส่ง / ค่าจัดส่ง" ชนะ (คำบนบรรทัดที่ "มีเงิน" = หลักฐานผูกกับเงิน)
/// แล้วเสนอ <c>[WHT-SUGGEST]</c> ม.40(8) ค่าขนส่ง 1% <b>บนยอดทั้งใบ</b> ≈ 29.92 บาท — ทั้งที่ (ก) ซื้อของจากร้านค้าปลีก
/// ไม่ใช่เงินได้ที่ต้องหัก (ข) ค่าขนส่งเอง 40 บาทรวม VAT ต่ำกว่าเกณฑ์ 1,000 บาท (ท.ป.4/2528 ข้อ 12)</para>
///
/// <para>กติกา: บรรทัดที่มีคำของหมวดหนึ่ง ๆ ต้องคิดเป็นมูลค่า <b>≥ <see cref="MinDominantShare"/></b> ของบรรทัดที่มีเงินทั้งใบ
/// จึงจะ "ตัดสินหมวดของทั้งใบ" ได้ · ส่วนน้อยกว่านั้น = บรรทัดประกอบ (ค่าส่ง/ค่าธรรมเนียมเล็ก ๆ ท้ายบิล) ใช้ตัดสินหมวด
/// ไม่ได้ และ<b>ห้ามเสนอประเภทเงินได้/อัตราหักบนยอดทั้งใบ</b> · ยอดของบรรทัดส่วนน้อยยังรายงานได้ เพื่อบอกผู้ใช้ว่า
/// ส่วนนั้นถึงเกณฑ์ 1,000 บาทไหม (ถึง = ต้องตรวจหักเฉพาะส่วนนั้น)</para>
///
/// <para><b>ไม่รู้ยอด = ไม่ตัดสินแทน</b> (DECISION_DOCTRINE G3): ไม่มีบรรทัดที่มียอด หรือรวมยอดได้ 0 ⇒
/// <see cref="Share.Known"/> = false และผู้เรียกต้องใช้พฤติกรรมเดิม (ไม่ใช่ถือว่า "ส่วนน้อย")</para>
/// </summary>
public static class OcrLineValueShare
{
    /// <summary>สัดส่วนขั้นต่ำที่บรรทัดของหมวดหนึ่งต้องมีจึงตัดสินหมวดทั้งใบได้ — "ส่วนใหญ่ของมูลค่า" ตามตัวอักษร
    /// (ใบผสมที่บริการ 50% ขึ้นไปยังได้หมวดบริการ + ข้อเสนอหัก ณ ที่จ่ายเหมือนเดิม — G5 ทิศที่ผู้จ่ายรับผิด)</summary>
    public const decimal MinDominantShare = 0.50m;

    /// <param name="MatchedValue">มูลค่ารวมของบรรทัดที่ตรงกับหมวด</param>
    /// <param name="TotalValue">มูลค่ารวมของทุกบรรทัดที่มีเงิน</param>
    /// <param name="MatchedLines">จำนวนบรรทัดที่ตรง</param>
    /// <param name="MatchedDescriptions">คำอธิบายของบรรทัดที่ตรง (ไว้เขียนเหตุผล)</param>
    public readonly record struct Share(
        decimal MatchedValue, decimal TotalValue, int MatchedLines, IReadOnlyList<string> MatchedDescriptions)
    {
        /// <summary>รู้สัดส่วนจริงไหม (มีบรรทัดที่มีเงิน และมีบรรทัดที่ตรงอย่างน้อยหนึ่ง)</summary>
        public bool Known => TotalValue > 0m && MatchedLines > 0;

        /// <summary>สัดส่วน 0–1 (0 เมื่อไม่รู้)</summary>
        public decimal Ratio => TotalValue > 0m ? MatchedValue / TotalValue : 0m;

        /// <summary>บรรทัดของหมวดนี้เป็น "ส่วนน้อย" ของใบ — <b>เฉพาะเมื่อรู้สัดส่วน</b> (ไม่รู้ ≠ ส่วนน้อย)</summary>
        public bool IsMinority => Known && Ratio < MinDominantShare;
    }

    /// <summary>วัดสัดส่วนมูลค่าของบรรทัดที่ <paramref name="matches"/> ตอบจริง</summary>
    /// <param name="lines">บรรทัดที่มีเงิน (ยอด ≤ 0 ไม่นับทั้งตัวตั้งและตัวหาร)</param>
    /// <param name="matches">ตัวบอกว่าคำอธิบายบรรทัดนี้เป็นของหมวดไหม (ผู้เรียกส่งกติกาคำของตัวเองมา)</param>
    public static Share Measure(IEnumerable<OcrPricedLine>? lines, Func<string, bool> matches)
    {
        var total = 0m;
        var matched = 0m;
        var count = 0;
        var descs = new List<string>();
        foreach (var l in lines ?? Enumerable.Empty<OcrPricedLine>())
        {
            if (l.Amount <= 0m) continue;
            total += l.Amount;
            if (string.IsNullOrWhiteSpace(l.Description) || !matches(l.Description)) continue;
            matched += l.Amount;
            count++;
            descs.Add(l.Description.Trim());
        }
        return new Share(matched, total, count, descs);
    }
}
