namespace Accounting.Helpers;

/// <summary>เหตุผลที่ออก "ใบกำกับภาษีอย่างย่อ" (§86/6) ไม่ได้ — ใช้แสดงให้ผู้ใช้เข้าใจ
/// ว่าต้องทำอะไรต่อ (กติกา: ปฏิเสธแล้วต้องมีทางไปต่อ ห้ามตันเฉย ๆ)</summary>
public enum AbbreviatedInvoiceBlockReason
{
    None = 0,
    /// <summary>บริษัทยังไม่จด VAT — ไม่มีสิทธิ์ออกใบกำกับชนิดใดเลย (§77/1)</summary>
    NotVatRegistered = 1,
    /// <summary>จด VAT แล้วแต่ยังไม่ได้รับอนุมัติ ภ.พ.06 (ประกอบกิจการค้าปลีก)</summary>
    NoPhoR06Approval = 2,
    /// <summary>บิลนี้ไม่มี VAT (ขายสินค้ายกเว้น §81 หรืออัตรา 0%) — ออกใบกำกับ
    /// อย่างย่อไม่ได้ ต้องเป็นใบเสร็จรับเงินธรรมดา</summary>
    NoVatOnBill = 3,
}

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
        /// <summary>ข้อความอธิบายพร้อมทางไปต่อ — null เมื่อออกได้ปกติ</summary>
        public string? Message => Reason switch
        {
            AbbreviatedInvoiceBlockReason.NotVatRegistered =>
                "บริษัทยังไม่ได้จดทะเบียนภาษีมูลค่าเพิ่ม — สลิปพิมพ์เป็น \"ใบเสร็จรับเงิน\" "
                + "(จด VAT แล้วมาตั้งค่าในหน้าข้อมูลบริษัท)",
            AbbreviatedInvoiceBlockReason.NoPhoR06Approval =>
                "ยังไม่ได้รับอนุมัติ ภ.พ.06 (ประกอบกิจการค้าปลีก) — ออกใบกำกับภาษีอย่างย่อไม่ได้ "
                + "ตาม §86/6 · สลิปพิมพ์เป็น \"ใบเสร็จรับเงิน\" ไปก่อน · ยื่น ภ.พ.06 แล้วกรอกวันที่"
                + "อนุมัติในหน้าข้อมูลบริษัท",
            AbbreviatedInvoiceBlockReason.NoVatOnBill =>
                "บิลนี้ไม่มีภาษีมูลค่าเพิ่ม (สินค้ายกเว้น §81 หรืออัตรา 0%) — พิมพ์เป็น"
                + "\"ใบเสร็จรับเงิน\"",
            _ => null,
        };
    }

    /// <summary>ตัดสินหัวสลิป
    ///
    /// <para><paramref name="phoR06ApprovedDate"/> เป็นตัวชี้ขาดคู่กับ
    /// <paramref name="isRetailApproved"/>: ต้องมี **ทั้งสองอย่าง** — ธงอย่างเดียวคือ
    /// เจตนา ส่วนวันที่คือหลักฐาน (บทเรียน "doc-comment ที่บอกว่ามีด่านแล้ว = เจตนา
    /// ไม่ใช่หลักฐาน" ในรูปข้อมูล)</para>
    ///
    /// <para><paramref name="issueDateUtc"/> ใช้กันการออกย้อนไปก่อนวันอนุมัติ — ใบที่ลงวันที่
    /// ก่อน ภ.พ.06 อนุมัติ ยังออกอย่างย่อไม่ได้</para></summary>
    public static Result Resolve(
        bool isVatRegistered,
        bool isRetailApproved,
        DateTime? phoR06ApprovedDate,
        decimal vatAmountOnBill,
        DateTime issueDateUtc)
    {
        if (!isVatRegistered)
            return new(Receipt, false, AbbreviatedInvoiceBlockReason.NotVatRegistered);

        if (!isRetailApproved || phoR06ApprovedDate is not DateTime approved
            || issueDateUtc.Date < approved.Date)
            return new(Receipt, false, AbbreviatedInvoiceBlockReason.NoPhoR06Approval);

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
