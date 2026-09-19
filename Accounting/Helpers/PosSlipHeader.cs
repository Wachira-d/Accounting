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
    /// สิทธิ์ตามกฎหมาย ไม่ใช่เผลอรับค่า default ไปเงียบ ๆ</para>
    ///
    /// <para><paramref name="billBelongsToBranch"/> + <paramref name="issuerTaxBranchCode"/>
    /// ตอบข้อ §86/4(2): บิลที่ผูกสาขาไว้ต้องรู้รหัสสาขาก่อนจึงจะออกใบกำกับอย่างย่อได้
    /// — ดู <see cref="BranchSeriesCode"/></para></summary>
    /// <param name="billBelongsToBranch">บิลนี้ผูกกับ <c>Branch</c> ไหม
    /// (<c>PosOrder.BranchId != null</c>) — บริษัทที่ไม่มีสาขาเลยคือสำนักงานใหญ่โดยนิยาม</param>
    /// <param name="issuerTaxBranchCode">รหัสสาขา 5 หลักที่ตรึงบนบิล
    /// (<c>PosOrder.IssuerBranchCode</c>) — ว่าง/ผิดรูป = ยังไม่รู้</param>
    public static Result Resolve(
        bool isVatRegistered,
        bool isRetailApproved,
        DateTime? phoR06ApprovedDate,
        decimal vatAmountOnBill,
        DateTime issueDateUtc,
        bool requirePhoR06,
        bool billBelongsToBranch,
        string? issuerTaxBranchCode)
    {
        var eligibility = AbbreviatedTaxInvoiceRule.Judge(
            isVatRegistered, isRetailApproved, phoR06ApprovedDate, issueDateUtc, requirePhoR06);
        if (eligibility != AbbreviatedInvoiceBlockReason.None)
            return new(Receipt, false, eligibility);

        // ไม่มี VAT บนบิล = ไม่มีอะไรให้ใบกำกับรับรอง — คำว่า "ใบกำกับภาษี" บนใบที่
        // VAT = 0 ทำให้ผู้ซื้อเข้าใจผิดว่ามีภาษีซื้อให้เคลม
        if (vatAmountOnBill <= 0m)
            return new(Receipt, false, AbbreviatedInvoiceBlockReason.NoVatOnBill);

        // §86/4(2) — ใบต้องบอกว่าออกจากสำนักงานใหญ่หรือสาขาไหน. บิลของสาขาที่ยังไม่มี
        // รหัส = ยังตอบคำถามนั้นไม่ได้ ⇒ พิมพ์ "ใบเสร็จรับเงิน" ไปก่อน ห้ามเดา 00000
        if (BranchSeriesCode(billBelongsToBranch, issuerTaxBranchCode) == null)
            return new(Receipt, false, AbbreviatedInvoiceBlockReason.BranchTaxCodeMissing);

        return new(AbbreviatedTaxInvoice, true, AbbreviatedInvoiceBlockReason.None);
    }

    /// <summary>รหัสสาขา 5 หลักที่ใช้เป็น<b>ส่วนหนึ่งของเลขรัน</b>ใบกำกับอย่างย่อ —
    /// <c>null</c> = ยังไม่รู้ ⇒ <b>ห้ามออกเลข</b>
    ///
    /// <para>═══ ทำไม <c>00000</c> ถึงไม่ใช่ค่าสำรองที่ปลอดภัย ═══ มันไม่ใช่ "ไม่ระบุ"
    /// แต่แปลว่า <b>"สำนักงานใหญ่"</b> (ประกาศอธิบดีฯ 199) · บิลของสาขาที่ตกมาใช้ค่านี้
    /// จะ (ก) พิมพ์ข้อความเท็จบนกระดาษ และ (ข) กินเลขรันในเล่มของสำนักงานใหญ่ ทำให้
    /// เล่มทั้งสองเล่มไม่ gap-free ตาม §86/4 — ความเสียหายแบบที่ <b>มองไม่เห็นและ
    /// แก้ย้อนหลังไม่ได้</b> จึงต้องล้มตั้งแต่ก่อนออกเลข (DECISION_DOCTRINE §1 G5)</para>
    ///
    /// <para>บริษัทที่<b>ไม่มีสาขา</b> (<paramref name="billBelongsToBranch"/> = false)
    /// คือสำนักงานใหญ่โดยนิยาม ⇒ <c>00000</c> ถูกต้องและเป็นพฤติกรรมเดิม</para></summary>
    public static string? BranchSeriesCode(bool billBelongsToBranch, string? taxBranchCode)
    {
        var code = taxBranchCode?.Trim();
        if (!string.IsNullOrEmpty(code) && code.Length == 5 && code.All(char.IsDigit))
            return code;
        return billBelongsToBranch ? null : "00000";
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
