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

    public sealed record Result(string? SellerBranchCode, string? BuyerBranchCode);

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

        var buyerAnchorIndex = -1;
        var buyerAnchor = BuyerAnchorRegex.Match(rawText);
        if (buyerAnchor.Success) buyerAnchorIndex = buyerAnchor.Index;
        else
        {
            // ── ขั้นสำรอง (รอบ 190): ป้ายผู้ซื้อชุดกลางของระบบ (Helpers/OcrPartyLabels) ──
            // ใบเสร็จ "ได้รับเงินจาก / Received From" ไม่อยู่ในรายการคำของตัวนี้ ⇒ เดิมทั้งหน้า
            // (รวม ☑ สำนักงานใหญ่ ในช่องติ๊กของผู้ซื้อ) ถูกอ่านเป็นของผู้ขาย · ใช้เฉพาะเมื่อรายการ
            // เดิมหาไม่เจอ ⇒ ใบที่เคยแยกบล็อกได้อยู่แล้ว ผลไม่ขยับ
            var label = Accounting.Helpers.OcrPartyLabels.FindBuyer(rawText);
            if (label > 0) buyerAnchorIndex = label;
        }
        if (buyerAnchorIndex < 0)
        {
            // ไม่มีบล็อกผู้ซื้อให้แยก (ใบเสร็จร้านค้า/สลิป) — พฤติกรรมเดิม:
            // อ่านทั้งหน้าเป็นของผู้ขาย, ผู้ซื้อไม่ระบุ
            return new Result(issuer ?? FromSegment(rawText), null);
        }

        var sellerSegment = rawText[..buyerAnchorIndex];
        var buyerStart = buyerAnchorIndex;
        var buyerEnd = Math.Min(rawText.Length, buyerStart + BuyerWindowChars);
        var buyerSegment = rawText[buyerStart..buyerEnd];

        // ตัดที่หัวตารางรายการ ถ้าเจอก่อนหมดหน้าต่าง (กันเลขในตารางปน)
        var tableAnchor = ItemTableAnchorRegex.Match(buyerSegment);
        if (tableAnchor.Success && tableAnchor.Index > 0)
            buyerSegment = buyerSegment[..tableAnchor.Index];

        var seller = issuer ?? FromSegment(sellerSegment);
        // บล็อกผู้ขายมักอยู่หัวกระดาษเสมอ — แต่ถ้าหน้าตัดมาแล้วไม่เจอ (เช่น
        // ผู้ซื้ออยู่บนสุด) ยอมถอยไปอ่านทั้งหน้าเพื่อไม่ให้แย่กว่าเดิม
        seller ??= FromSegment(rawText);

        return new Result(seller, FromSegment(buyerSegment));
    }
}
