namespace Accounting.Helpers;

/// <summary>หัวกระดาษของสลิป POS + สิทธิ์ออกใบกำกับภาษีอย่างย่อ (§86/6)
///
/// ═══ ที่มา (POS_MULTI_BRANCH_ANALYSIS.md — ทีม CPA) ═══
/// สลิป POS พิมพ์คำว่า <b>"ใบเสร็จรับเงิน / ใบกำกับภาษีอย่างย่อ"</b> เป็น string literal
/// ใน <c>pos.html</c> <b>ทุกใบโดยไม่ตรวจอะไรเลย</b> — บริษัทที่ยังไม่ได้รับอนุมัติ ภ.พ.06
/// (หรือยังไม่จด VAT ด้วยซ้ำ) ก็พิมพ์คำนี้ออกมา ซึ่งเป็นการ<b>ออกใบกำกับภาษีโดยไม่มีสิทธิ์</b>
/// (§86/6 ให้สิทธิ์เฉพาะผู้ประกอบการค้าปลีกที่ได้รับอนุมัติ) และผู้ซื้อที่รับใบไปก็ตกอยู่ใต้
/// §82/5(5) คือเคลมภาษีซื้อไม่ได้ ทั้งที่หน้ากระดาษบอกว่าเป็นใบกำกับ
///
/// ═══ ทำไมเป็นฟังก์ชันบริสุทธิ์ ═══
/// หัวกระดาษต้องคำนวณที่ <b>เซิร์ฟเวอร์ที่เดียว</b> แล้วส่งให้หน้าเว็บ <b>แสดง</b> อย่างเดียว —
/// เดิมมันเป็นสำเนามือฝั่ง JS (defect class เดียวกับ <c>docHeaderLabel</c> ·
/// <c>complianceIssues</c> · <c>MENU_SECTIONS</c>) ซึ่ง drift แน่นอนแค่รอเวลา
/// </summary>
public static class PosSlipHeader
{
    public const string Receipt = "ใบเสร็จรับเงิน";
    public const string AbbreviatedTaxInvoice = "ใบเสร็จรับเงิน / ใบกำกับภาษีอย่างย่อ";

    public readonly record struct Result(
        string Title,
        bool CanIssueAbbreviated,
        AbbreviatedInvoiceBlockReason Reason)
    {
        /// <summary>ข้อความอธิบายพร้อมทางไปต่อ — null เมื่อออกได้ปกติ
        /// (ข้อความอยู่ที่ <see cref="AbbreviatedTaxInvoiceRule"/> เจ้าของกติกา ห้ามเขียนซ้ำที่นี่)</summary>
        public string? Message => AbbreviatedTaxInvoiceRule.Message(Reason);
    }

    /// <summary>ตัดสินหัวสลิป
    ///
    /// <para>สิทธิ์ออกใบกำกับอย่างย่อตัดสินโดย <see cref="AbbreviatedTaxInvoiceRule"/>
    /// <b>ตัวเดียวของระบบ</b> (เส้นเอกสาร/PDF ใช้ตัวเดียวกัน) — ที่นี่เหลือเฉพาะกติกา
    /// ที่เป็นของ<b>สลิป</b>จริง ๆ คือ "บิลนี้ไม่มี VAT ก็ไม่มีอะไรให้ใบกำกับรับรอง"</para>
    ///
    /// <para><paramref name="requirePhoR06"/> มาจาก
    /// <c>SiteSettings.RequirePhoR06ForAbbreviatedTaxInvoice</c> (แอดมินแพลตฟอร์มตั้ง) —
    /// <b>ไม่มีค่าตั้งต้นในลายเซ็นโดยตั้งใจ</b>: ผู้เรียกใหม่ต้องรู้ตัวว่ากำลังตัดสินเรื่อง
    /// สิทธิ์ตามกฎหมาย ไม่ใช่เผลอรับค่า default ไปเงียบ ๆ</para></summary>
    public static Result Resolve(
        bool isVatRegistered,
        bool isRetailApproved,
        DateTime? phoR06ApprovedDate,
        decimal vatAmountOnBill,
        DateTime issueDateUtc,
        bool requirePhoR06)
    {
        var eligibility = AbbreviatedTaxInvoiceRule.Judge(
            isVatRegistered, isRetailApproved, phoR06ApprovedDate, issueDateUtc, requirePhoR06);
        if (eligibility != AbbreviatedInvoiceBlockReason.None)
            return new(Receipt, false, eligibility);

        // ไม่มี VAT บนบิล = ไม่มีอะไรให้ใบกำกับรับรอง — คำว่า "ใบกำกับภาษี" บนใบที่
        // VAT = 0 ทำให้ผู้ซื้อเข้าใจผิดว่ามีภาษีซื้อให้เคลม
        if (vatAmountOnBill <= 0m)
            return new(Receipt, false, AbbreviatedInvoiceBlockReason.NoVatOnBill);

        return new(AbbreviatedTaxInvoice, true, AbbreviatedInvoiceBlockReason.None);
    }

    /// <summary>ข้อความรหัสสาขาบนสลิป (§86/4 · ประกาศอธิบดีฯ ฉบับที่ 199)
    ///
    /// <para>คืน <c>null</c> เมื่อ**ยังไม่รู้** รหัสสาขา — ห้ามเดาเป็น "สำนักงานใหญ่"
    /// (ค่า default ที่แต่งขึ้นอันตรายกว่าการไม่ตอบ: ใบของสาขาที่ 3 จะประกาศตัวเป็น
    /// สำนักงานใหญ่ทุกใบ)</para></summary>
    public static string? BranchLabel(string? taxBranchCode)
    {
        if (string.IsNullOrWhiteSpace(taxBranchCode)) return null;
        var code = taxBranchCode.Trim();
        if (code.Length != 5 || !code.All(char.IsDigit)) return null;
        return code == "00000" ? "สำนักงานใหญ่" : $"สาขาที่ {code}";
    }
}
