namespace Accounting.Helpers;

/// <summary>เหตุผลที่ออก "ใบกำกับภาษีอย่างย่อ" (§86/6) ไม่ได้ — ใช้แสดงให้ผู้ใช้เข้าใจ
/// ว่าต้องทำอะไรต่อ (กติกา: ปฏิเสธแล้วต้องมีทางไปต่อ ห้ามตันเฉย ๆ)
///
/// <para>ย้ายมาจาก <c>PosSlipHeader.cs</c> รอบ 182 — เดิม enum นี้อยู่คู่กับสลิป POS
/// ทั้งที่คำถาม "บริษัทนี้มีสิทธิ์ออกใบกำกับอย่างย่อไหม" ไม่ได้เป็นของ POS เจ้าเดียว
/// (เส้นเอกสาร/PDF ถามคำถามเดียวกันแต่เดิม<b>ไม่เคยถาม</b> — ดู
/// <c>DECISION_AUDIT_2026-09-18.md</c> D1-B4)</para></summary>
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

    /// <summary>บิลผูกกับ<b>สาขา</b> แต่สาขานั้นยังไม่มีรหัสสาขา 5 หลัก (ภ.พ.09) —
    /// §86/4(2) + ประกาศอธิบดีฯ ฉบับที่ 199 บังคับให้ระบุว่าใบนี้ออกจาก "สำนักงานใหญ่"
    /// หรือ "สาขาที่ ____" · เดาเป็น <c>00000</c> = กระดาษประกาศเท็จว่าเป็นสำนักงานใหญ่
    /// <b>และ</b>เลขรันของสาขาไปปนกับเล่มสำนักงานใหญ่ ซึ่งแก้ย้อนหลังไม่ได้</summary>
    BranchTaxCodeMissing = 4,
}

/// <summary>ตัวตัดสิน<b>ตัวเดียว</b>ของคำถาม "บริษัทนี้มีสิทธิ์ออกใบกำกับภาษีอย่างย่อ
/// (§86/6) หรือไม่" — ใช้ร่วมกันทั้งสลิป POS (<see cref="PosSlipHeader"/>) และเส้น
/// เอกสาร/PDF (<c>PdfGenerationService</c>)
///
/// ═══ กฎหมาย ═══
/// <para><b>ใบกำกับภาษีเต็มรูป (§86/4)</b> — ผู้ประกอบการที่<b>จด VAT</b> ออกได้เลย
/// ไม่ต้องขออนุญาตอะไรเพิ่ม</para>
/// <para><b>ใบกำกับภาษีอย่างย่อ (§86/6)</b> — ออกได้เฉพาะผู้ประกอบการที่<b>ประกอบ
/// กิจการค้าปลีก</b> และ<b>ได้รับอนุมัติ ภ.พ.06</b> แล้ว · ผู้ที่ไม่มีสิทธิ์แล้วออก =
/// ออกใบกำกับโดยไม่มีสิทธิ์ และผู้ซื้อที่รับไปตกอยู่ใต้ §82/5(5) คือเคลมภาษีซื้อไม่ได้
/// ทั้งที่หน้ากระดาษบอกว่าเป็นใบกำกับ</para>
///
/// ═══ ทำไมมีสวิตช์ระดับแพลตฟอร์ม (<paramref name="requirePhoR06"/>) ═══
/// <para>คำตัดสินเจ้าของโปรเจกต์ 2026-09-19: ข้อบังคับ ภ.พ.06 เป็น<b>นโยบายที่กฎหมาย
/// อาจเปลี่ยนได้</b> ไม่ใช่ค่าคงที่ของธรรมชาติ · แอดมินแพลตฟอร์มจึงต้องปิดด่านนี้ได้ทั้งระบบ
/// เมื่อกฎเปลี่ยน โดยไม่ต้องรอ deploy (<c>SiteSettings.RequirePhoR06ForAbbreviatedTaxInvoice</c>
/// — ค่าตั้งต้น <c>true</c> = บังคับ ตามกฎหมายวันนี้)</para>
/// <para>⚠️ <b>สวิตช์นี้ปิดได้เฉพาะด่าน ภ.พ.06</b> — ด่าน "ยังไม่จด VAT" ปิดไม่ได้
/// เพราะการออกใบกำกับโดยไม่ได้จด VAT ไม่ใช่เรื่องนโยบาย แต่เป็นสิ่งที่ §77/1 ห้ามขาด
/// (และระบบไม่มีเลขผู้เสียภาษี VAT จะพิมพ์ลงใบด้วยซ้ำ)</para>
///
/// ═══ ทำไมเป็นฟังก์ชันบริสุทธิ์ที่รับ "ข้อเท็จจริง" ไม่ใช่ entity ═══
/// <para>ตาม <c>DECISION_DOCTRINE.md</c> §1 — ตัวตัดสินต้องทดสอบได้โดยไม่ต้องมีฐานข้อมูล
/// และต้องเรียกได้จากทุกชั้น (POS · PDF · ด่านอนุมัติ) โดยไม่มีใครเขียนเกณฑ์เองซ้ำ</para>
/// </summary>
public static class AbbreviatedTaxInvoiceRule
{
    /// <summary>บริษัทนี้ออกใบกำกับภาษีอย่างย่อสำหรับเอกสารที่ลงวันที่
    /// <paramref name="issueDateUtc"/> ได้หรือไม่</summary>
    /// <param name="isVatRegistered">จดทะเบียนภาษีมูลค่าเพิ่มแล้ว (§77/1)</param>
    /// <param name="isRetailApproved">ธงว่าเป็นกิจการค้าปลีกที่ได้รับอนุมัติ — <b>เจตนา</b></param>
    /// <param name="phoR06ApprovedDate">วันที่อนุมัติ ภ.พ.06 — <b>หลักฐาน</b> (ต้องมีคู่กับธง:
    /// ธงอย่างเดียวคือ "ผู้ใช้ติ๊กไว้" ซึ่งไม่ใช่การอนุมัติ)</param>
    /// <param name="issueDateUtc">วันที่บนเอกสาร — กันการออกย้อนไปก่อนวันอนุมัติ</param>
    /// <param name="requirePhoR06">แอดมินแพลตฟอร์มบังคับ ภ.พ.06 อยู่หรือไม่
    /// (<c>false</c> = กฎเปลี่ยนแล้ว/ปิดด่านชั่วคราว → จด VAT อย่างเดียวก็ออกได้)</param>
    public static AbbreviatedInvoiceBlockReason Judge(
        bool isVatRegistered,
        bool isRetailApproved,
        DateTime? phoR06ApprovedDate,
        DateTime issueDateUtc,
        bool requirePhoR06)
    {
        // §77/1 — ไม่จด VAT = ออกใบกำกับชนิดใดไม่ได้เลย · สวิตช์แอดมินไม่ครอบข้อนี้
        if (!isVatRegistered)
            return AbbreviatedInvoiceBlockReason.NotVatRegistered;

        // แอดมินปิดด่าน ภ.พ.06 (เผื่อกฎหมายเปลี่ยน) → จด VAT แล้วออกได้เลย
        if (!requirePhoR06)
            return AbbreviatedInvoiceBlockReason.None;

        // §86/6 — ต้องมีทั้งเจตนาและหลักฐาน และเอกสารต้องลงวันที่ไม่ก่อนวันอนุมัติ
        if (!isRetailApproved
            || phoR06ApprovedDate is not DateTime approved
            || issueDateUtc.Date < approved.Date)
            return AbbreviatedInvoiceBlockReason.NoPhoR06Approval;

        return AbbreviatedInvoiceBlockReason.None;
    }

    /// <summary>ทางลัดที่อ่านออกเป็นประโยคเดียว — ใช้ตรงจุดที่ต้องการแค่ใช่/ไม่ใช่
    /// (เส้น PDF) ส่วนจุดที่ต้องบอกผู้ใช้ว่าทำอะไรต่อ ให้ใช้ <see cref="Judge"/>
    /// แล้วอ่านเหตุผล</summary>
    public static bool CanIssue(
        bool isVatRegistered,
        bool isRetailApproved,
        DateTime? phoR06ApprovedDate,
        DateTime issueDateUtc,
        bool requirePhoR06)
        => Judge(isVatRegistered, isRetailApproved, phoR06ApprovedDate, issueDateUtc, requirePhoR06)
           == AbbreviatedInvoiceBlockReason.None;

    /// <summary>ข้อความอธิบายพร้อม<b>ทางไปต่อ</b> — <c>null</c> เมื่อออกได้ปกติ</summary>
    public static string? Message(AbbreviatedInvoiceBlockReason reason) => reason switch
    {
        AbbreviatedInvoiceBlockReason.NotVatRegistered =>
            "บริษัทยังไม่ได้จดทะเบียนภาษีมูลค่าเพิ่ม — เอกสารพิมพ์เป็น \"ใบเสร็จรับเงิน\" "
            + "(จด VAT แล้วมาตั้งค่าในหน้าข้อมูลบริษัท)",
        AbbreviatedInvoiceBlockReason.NoPhoR06Approval =>
            "ยังไม่ได้รับอนุมัติ ภ.พ.06 (ประกอบกิจการค้าปลีก) — ออกใบกำกับภาษีอย่างย่อไม่ได้ "
            + "ตาม §86/6 · เอกสารพิมพ์เป็น \"ใบเสร็จรับเงิน\" ไปก่อน · ยื่น ภ.พ.06 แล้วกรอกวันที่"
            + "อนุมัติในหน้าข้อมูลบริษัท",
        AbbreviatedInvoiceBlockReason.BranchTaxCodeMissing =>
            "บิลนี้ออกจากสาขาที่ยังไม่ได้กรอก \"รหัสสาขา\" 5 หลัก — ใบกำกับภาษีอย่างย่อ "
            + "ต้องระบุสาขาตาม §86/4(2) · เอกสารพิมพ์เป็น \"ใบเสร็จรับเงิน\" ไปก่อน · "
            + "กรอกรหัสสาขา (จาก ภ.พ.09) ในหน้าตั้งค่าสาขา แล้วปิดบิลใหม่",
        AbbreviatedInvoiceBlockReason.NoVatOnBill =>
            "บิลนี้ไม่มีภาษีมูลค่าเพิ่ม (สินค้ายกเว้น §81 หรืออัตรา 0%) — พิมพ์เป็น"
            + "\"ใบเสร็จรับเงิน\"",
        _ => null,
    };
}
