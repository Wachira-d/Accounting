using System.Text.RegularExpressions;

namespace Accounting.Services;

/// <summary>
/// แกะ "รหัสสาขา" ของ **ผู้ขาย** และ **ผู้ซื้อ** จากข้อความบนกระดาษ
/// (ประกาศอธิบดีฯ ฉบับที่ 199 / §86/4: ใบกำกับต้องระบุสาขาทั้งสองฝั่ง
///  "00000" = สำนักงานใหญ่, อื่น ๆ = "สาขาที่ NNNNN")
///
/// ที่มา: เดิม OCR ยิง regex "สาขาที่ …" ทับทั้งหน้า แล้วยัดผลลัพธ์เป็นสาขา
/// **ผู้ขาย** ตัวเดียว — สาขาผู้ซื้อจึงไม่เคยถูกอ่านเลย (ตกเป็น 00000 เสมอ)
/// ⇒ ขายให้สาขาลูกค้าแล้วรายงานภาษีขายขึ้นเป็นสำนักงานใหญ่ผิดแบบเงียบ ๆ
/// และในทางกลับกัน ถ้าบล็อกผู้ซื้ออยู่บนสุดของกระดาษ สาขาผู้ซื้อจะถูกอ่าน
/// เป็นของผู้ขายแทน
///
/// วิธี: หา "จุดเริ่มบล็อกผู้ซื้อ" แล้วตัดข้อความเป็น 2 ส่วน — ส่วนก่อนหน้า
/// เป็นของผู้ขาย ส่วนหลังเป็นของผู้ซื้อ (จำกัดหน้าต่างไม่ให้ไหลลงไปถึงตาราง
/// รายการสินค้า). กฎการอ่านค่าใช้ฟังก์ชันเดียวกันทั้งสองฝั่ง (canonical —
/// กฎเหล็ก #4 C ห้ามเขียนสองที่)
/// </summary>
public static class BranchCodeExtractor
{
    /// <summary>"สาขาที่ 3" / "BRANCH: 00003" / "สาขา เลขที่ 3" / "Branch No. 8" / "สาขาที 3"
    /// (รอบ 190: ยอม "ที" ที่ OCR ทำไม้เอกหล่น และ "No." ของหัวกระดาษอังกฤษ — เดิมสองรูปนี้
    /// ไม่เจอตัวเลขแล้วตกไปอ่าน "สำนักงานใหญ่" ของหัวกระดาษแทน)</summary>
    private static readonly Regex BranchRegex = new(
        @"(?:สาขา(?:ที่|ที)?|BRANCH)\s*(?:เลข(?:ที่)?\s*|NO\.?\s*|#\s*)?[:：]?\s*(\d{1,5})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HeadOfficeRegex = new(
        @"สำนักงานใหญ่|HEAD\s*OFFICE",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>จุดเริ่มบล็อกผู้ซื้อ — เลือกเฉพาะคำที่ "ชี้ตัวผู้ซื้อ" จริง ๆ
    /// ไม่เอา "ผู้รับเงิน/ผู้รับมอบอำนาจ" ที่เป็นช่องเซ็นฝั่งผู้ขาย</summary>
    private static readonly Regex BuyerAnchorRegex = new(
        @"(?:นาม)?(?:ลูกค้า|ผู้ซื้อ|ผู้ว่าจ้าง)|ชื่อผู้ซื้อ|ส่งถึง|เรียน\s|BILL\s*TO|SOLD\s*TO|CUSTOMER|BUYER",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>หัวตารางรายการสินค้า — ใช้ตัดท้ายหน้าต่างผู้ซื้อ ไม่ให้เลข
    /// ในตาราง (ลำดับ/จำนวน) ถูกอ่านเป็นรหัสสาขา</summary>
    private static readonly Regex ItemTableAnchorRegex = new(
        @"ลำดับ|รายการ(?:สินค้า)?|รายละเอียด|DESCRIPTION|ITEM\s*(?:NO|CODE)?|QTY|จำนวนเงิน",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>ความยาวสูงสุดของ "บล็อกผู้ซื้อ" ที่ยอมให้มองหาสาขา — บล็อก
    /// ที่อยู่ผู้ซื้อบนใบกำกับไทยยาวไม่เกินนี้ กว้างกว่านี้เริ่มกินเนื้อหาอื่น</summary>
    private const int BuyerWindowChars = 500;

    /// <summary>
    /// รหัสสาขาผู้ขายได้มาจาก "ส่วนไหนของกระดาษ" (รอบ 197 ฝ่ายค้าน K-2) — เดิมทุกทางให้คะแนนเท่ากัน 0.85 ซึ่ง<b>เท่าเกณฑ์</b>
    /// <c>OcrVendorBranchContact.ReliableBranchConfidence</c> พอดี ⇒ ค่าที่ได้จากการ<b>ถอยไปอ่านทั้งหน้า</b> (มักเป็น "สาขาที่ 3"
    /// ของผู้ซื้อที่อยู่บนสุด) ถูกนับเป็นหลักฐานพอจะสร้างผู้ติดต่อถาวรตั้งแต่ตอนสแกน
    /// </summary>
    public enum SellerBranchEvidence
    {
        /// <summary>อ่านไม่ได้</summary>
        None,
        /// <summary>ประโยคประกาศสาขาผู้ออกใบ (<c>OcrIssuerBranch</c>) — ป้ายกำกับตรงตัว</summary>
        IssuerStatement,
        /// <summary>บล็อกผู้ขาย (ข้อความก่อนป้ายผู้ซื้อ) — ตำแหน่งบอกฝั่ง</summary>
        SellerBlock,
        /// <summary>ไม่มีป้ายผู้ซื้อทั้งหน้า (ใบเสร็จร้านค้า/สลิป) ⇒ อ่านทั้งหน้าเป็นของผู้ขาย — ไม่มีอะไรยืนยันฝั่ง</summary>
        WholePageNoBuyerBlock,
        /// <summary>มีป้ายผู้ซื้อ แต่บล็อกผู้ขายไม่มีสาขา ⇒ ถอยไปอ่านทั้งหน้า — ตัวแรกของหน้ามักอยู่ในบล็อกผู้ซื้อ</summary>
        WholePageFallback,
    }

    /// <summary>คะแนนของรหัสสาขาผู้ขายตามที่มา — ตัวตั้งตัวเดียวของทุกเส้น engine (<c>EnrichFromRawText</c> ·
    /// <c>ParseThaiDocument</c>) · ต่ำกว่า 0.85 = ไม่พอจะสร้างผู้ติดต่อแถวใหม่ (ไฮไลต์เหลืองให้ตรวจด้วย)</summary>
    public const double IssuerStatementConfidence = 0.90;
    public const double SellerBlockConfidence = 0.85;
    public const double WholePageNoBuyerBlockConfidence = 0.70;
    public const double WholePageFallbackConfidence = 0.60;
    /// <summary>ถอยอ่านทั้งหน้าแล้วได้รหัส<b>เดียวกับสาขาผู้ซื้อ</b> = เกือบแน่ว่าอ่านป้ายของผู้ซื้อซ้ำ</summary>
    public const double FallbackEqualsBuyerConfidence = 0.40;

    public sealed record Result(string? SellerBranchCode, string? BuyerBranchCode,
        SellerBranchEvidence SellerEvidence = SellerBranchEvidence.None)
    {
        /// <summary>คะแนนของ <see cref="SellerBranchCode"/> — null เมื่ออ่านไม่ได้</summary>
        public double? SellerConfidence => SellerBranchCode == null ? null : SellerEvidence switch
        {
            SellerBranchEvidence.IssuerStatement => IssuerStatementConfidence,
            SellerBranchEvidence.SellerBlock => SellerBlockConfidence,
            SellerBranchEvidence.WholePageNoBuyerBlock => WholePageNoBuyerBlockConfidence,
            SellerBranchEvidence.WholePageFallback => string.Equals(SellerBranchCode, BuyerBranchCode, StringComparison.Ordinal)
                ? FallbackEqualsBuyerConfidence : WholePageFallbackConfidence,
            _ => WholePageFallbackConfidence,   // ไม่รู้ที่มา = ไม่พอสร้างแถว (DOCTRINE §1)
        };
    }

    /// <summary>กฎอ่านค่าจาก "ข้อความช่วงเดียว" — ใช้ร่วมทั้งฝั่งผู้ขาย/ผู้ซื้อ.
    /// คืน null เมื่อไม่พบ (ห้ามเดา 00000 ให้ — ผู้เรียกตัดสินเองว่าจะ default
    /// หรือปล่อยว่างให้ผู้ใช้กรอก)</summary>
    public static string? FromSegment(string? segment)
    {
        if (string.IsNullOrWhiteSpace(segment)) return null;
        var m = BranchRegex.Match(segment);
        if (m.Success) return m.Groups[1].Value.PadLeft(5, '0');
        return HeadOfficeRegex.IsMatch(segment) ? "00000" : null;
    }

    public static Result Extract(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return new Result(null, null);

        // ── ขั้นที่ 0 (รอบ 190): ประโยคที่ประกาศตรง ๆ ว่า "สาขาที่ออกใบกำกับภาษีคือ สาขาที่ 8" ──
        // หลักฐานชั้นสูงสุดเรื่องสาขาผู้ออกใบ — ชนะ "สำนักงานใหญ่/Head Office" ในหัวกระดาษเดียวกัน
        // (ใบ B Radisson: หัวพิมพ์ทั้งสำนักงานใหญ่และสาขาผู้ออก) · ตัวตัดสินอยู่ Helpers/OcrIssuerBranch
        var issuer = Accounting.Helpers.OcrIssuerBranch.Detect(rawText)?.Code;

        var buyerAnchorIndex = BuyerAnchorIndex(rawText);
        if (buyerAnchorIndex < 0)
        {
            // ไม่มีบล็อกผู้ซื้อให้แยก (ใบเสร็จร้านค้า/สลิป) — พฤติกรรมเดิม:
            // อ่านทั้งหน้าเป็นของผู้ขาย, ผู้ซื้อไม่ระบุ (ค่าเดิม · คะแนนต่ำกว่าเกณฑ์สร้างแถว — K-2)
            if (issuer != null) return new Result(issuer, null, SellerBranchEvidence.IssuerStatement);
            var page = FromSegment(rawText);
            return new Result(page, null, page == null ? SellerBranchEvidence.None : SellerBranchEvidence.WholePageNoBuyerBlock);
        }

        var sellerSegment = rawText[..buyerAnchorIndex];

        // ── สาขาผู้ซื้อ (รอบ 200 K-11 · ใบ Makro) — อ่านบนข้อความที่กลบสองอย่างที่<b>ไม่ใช่ของผู้ซื้อแน่</b> (ความยาวเท่าเดิม):
        // (1) ป้ายฉบับ "ต้นฉบับลูกค้า / For Customer" (มุมขวาบน — เดิมเป็นจุดเริ่มบล็อกผู้ซื้อ ⇒ บล็อกกินหัวใบผู้ขาย)
        // (2) ประโยคประกาศสาขาผู้ออกใบ "สาขาที่ออกใบกำกับภาษี/ Branch 00005" (ของผู้ขายเสมอ — engine อ่านสองคอลัมน์สลับบรรทัด
        //     ⇒ อยู่ใต้ "ชื่อลูกค้า" ทันที) · เดิมได้สาขาผู้ซื้อ 00005 ทั้งที่กระดาษพิมพ์ "Tax ID 0203562005871 สาขา 00000"
        // จุดแบ่งฝั่ง<b>ผู้ขาย</b>ข้างบนไม่ขยับ (คะแนน/ค่าสาขาผู้ขายของทุกใบเท่าเดิม — ขอบเขตของ K-11 คือสาขาผู้ซื้อ)
        var buyerText = Accounting.Helpers.OcrIssuerBranch.MaskStatements(
            Accounting.Helpers.OcrPartyLabels.MaskCopyNoise(rawText));
        var buyerStart = BuyerAnchorIndex(buyerText);
        string? buyer = null;
        if (buyerStart >= 0)
        {
            var buyerEnd = Math.Min(buyerText.Length, buyerStart + BuyerWindowChars);
            var buyerSegment = buyerText[buyerStart..buyerEnd];

            // ตัดที่หัวตารางรายการ ถ้าเจอก่อนหมดหน้าต่าง (กันเลขในตารางปน)
            var tableAnchor = ItemTableAnchorRegex.Match(buyerSegment);
            if (tableAnchor.Success && tableAnchor.Index > 0)
                buyerSegment = buyerSegment[..tableAnchor.Index];

            buyer = FromSegment(buyerSegment);
        }
        if (issuer != null) return new Result(issuer, buyer, SellerBranchEvidence.IssuerStatement);
        var sellerBlock = FromSegment(sellerSegment);
        if (sellerBlock != null) return new Result(sellerBlock, buyer, SellerBranchEvidence.SellerBlock);
        // บล็อกผู้ขายมักอยู่หัวกระดาษเสมอ — แต่ถ้าหน้าตัดมาแล้วไม่เจอ (เช่น
        // ผู้ซื้ออยู่บนสุด) ยอมถอยไปอ่านทั้งหน้าเพื่อไม่ให้แย่กว่าเดิม
        // ⚠️ ค่านี้ยังเติมฟอร์มได้ (กฎเหล็ก #3) แต่ติดที่มา WholePageFallback ⇒ คะแนน < 0.85 ⇒ ไม่สร้างผู้ติดต่อถาวร
        // จากค่าที่อาจเป็นสาขาของผู้ซื้อ (ฝ่ายค้าน K-2 รอบ 197)
        var page2 = FromSegment(rawText);
        return new Result(page2, buyer, page2 == null ? SellerBranchEvidence.None : SellerBranchEvidence.WholePageFallback);
    }

    /// <summary>จุดเริ่มบล็อกผู้ซื้อ — รายการคำของตัวนี้ก่อน · ไม่เจอจึงใช้ป้ายผู้ซื้อชุดกลาง (Helpers/OcrPartyLabels) · -1 = ไม่มี
    /// <para>ขั้นสำรอง (รอบ 190): ใบเสร็จ "ได้รับเงินจาก / Received From" ไม่อยู่ในรายการคำของตัวนี้ ⇒ เดิมทั้งหน้า
    /// (รวม ☑ สำนักงานใหญ่ ในช่องติ๊กของผู้ซื้อ) ถูกอ่านเป็นของผู้ขาย · ใช้เฉพาะเมื่อรายการเดิมหาไม่เจอ ⇒ ใบที่เคยแยกบล็อกได้อยู่แล้ว ผลไม่ขยับ</para></summary>
    private static int BuyerAnchorIndex(string text)
    {
        var buyerAnchor = BuyerAnchorRegex.Match(text);
        if (buyerAnchor.Success) return buyerAnchor.Index;
        var label = Accounting.Helpers.OcrPartyLabels.FindBuyer(text);
        return label > 0 ? label : -1;
    }
}
