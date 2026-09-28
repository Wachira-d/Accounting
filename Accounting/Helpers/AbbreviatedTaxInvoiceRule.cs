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
    /// <summary>สลิปจากเครื่องบันทึกการเก็บเงิน (POS) แต่ยังไม่ได้รับอนุมัติ ภ.พ.06
    /// (คำขออนุมัติใช้เครื่องบันทึกการเก็บเงิน) — <b>เฉพาะช่องทางสลิป</b></summary>
    NoPhoR06Approval = 2,
    /// <summary>บิลนี้ไม่มี VAT (ขายสินค้ายกเว้น §81 หรืออัตรา 0%) — ออกใบกำกับ
    /// อย่างย่อไม่ได้ ต้องเป็นใบเสร็จรับเงินธรรมดา</summary>
    NoVatOnBill = 3,

    /// <summary>บิลผูกกับ<b>สาขา</b> แต่สาขานั้นยังไม่มีรหัสสาขา 5 หลัก (ภ.พ.09) —
    /// §86/4(2) + ประกาศอธิบดีฯ ฉบับที่ 199 บังคับให้ระบุว่าใบนี้ออกจาก "สำนักงานใหญ่"
    /// หรือ "สาขาที่ ____" · เดาเป็น <c>00000</c> = กระดาษประกาศเท็จว่าเป็นสำนักงานใหญ่
    /// <b>และ</b>เลขรันของสาขาไปปนกับเล่มสำนักงานใหญ่ ซึ่งแก้ย้อนหลังไม่ได้</summary>
    BranchTaxCodeMissing = 4,

    /// <summary>ยังไม่ได้ระบุว่ากิจการเป็นการขายปลีก/ให้บริการในลักษณะขายปลีก — §86/6 ให้ออกใบกำกับอย่างย่อ
    /// ได้เฉพาะกิจการลักษณะนี้ (ทุกช่องทาง)</summary>
    NotRetailBusiness = 5,
}

/// <summary>ใบกำกับภาษีอย่างย่อออกจากช่องทางไหน — กติกาต่างกัน (คำตัดสินเจ้าของ 2026-09-28)
///
/// <para><b>ภ.พ.06 คือคำขออนุมัติใช้เครื่องบันทึกการเก็บเงิน</b> — คุมเฉพาะใบกำกับอย่างย่อที่พิมพ์เป็น
/// <b>สลิปจากเครื่อง</b> (POS) · ใบกำกับอย่างย่อที่ออกเป็นเอกสารจากหน้าเอกสาร (พิมพ์/PDF) อาศัยสิทธิ์ §86/6
/// ของกิจการขายปลีกโดยตรง ไม่ต้องใช้ ภ.พ.06</para></summary>
public enum AbbreviatedInvoiceChannel
{
    /// <summary>เอกสารจากหน้าเอกสาร (HTML/PDF) — ต้องจด VAT + เป็นกิจการขายปลีก</summary>
    Document = 1,
    /// <summary>สลิปจากเครื่องบันทึกการเก็บเงิน/POS — ต้องจด VAT + ขายปลีก + ภ.พ.06 ที่อนุมัติแล้ว</summary>
    CashRegisterSlip = 2,
}

/// <summary>ตัวตัดสิน<b>ตัวเดียว</b>ของคำถาม "บริษัทนี้มีสิทธิ์ออกใบกำกับภาษีอย่างย่อ
/// (§86/6) หรือไม่" — ใช้ร่วมกันทั้งสลิป POS (<see cref="PosSlipHeader"/>) และเส้น
/// เอกสาร/PDF (<c>PdfGenerationService</c>)
///
/// ═══ กฎหมาย ═══
/// <para><b>ใบกำกับภาษีเต็มรูป (§86/4)</b> — ผู้ประกอบการที่<b>จด VAT</b> ออกได้เลย
/// ไม่ต้องขออนุญาตอะไรเพิ่ม</para>
/// <para><b>ใบกำกับภาษีอย่างย่อ (§86/6)</b> — ออกได้เฉพาะผู้ประกอบการที่<b>ประกอบ
/// กิจการขายปลีก/ให้บริการในลักษณะขายปลีก</b> · ถ้าออกเป็น<b>สลิปจากเครื่องบันทึกการเก็บเงิน</b> ต้องได้รับอนุมัติ
/// <b>ภ.พ.06</b> ด้วย (ภ.พ.06 = คำขออนุมัติใช้เครื่อง ไม่ใช่ใบอนุญาตออกใบกำกับอย่างย่อ — แก้ 2026-09-28 ตามคำตัดสิน
/// เจ้าของ: เดิมระบบบังคับ ภ.พ.06 กับทุกช่องทาง) · ผู้ที่ไม่มีสิทธิ์แล้วออก =
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
    /// <param name="isRetailApproved">ธง "ประกอบกิจการขายปลีก/ให้บริการในลักษณะขายปลีก" (§86/6)
    /// — ชื่อฟิลด์เดิม (<c>Company.IsRetailApproved</c>) คงไว้เพื่อไม่ต้อง migrate</param>
    /// <param name="phoR06ApprovedDate">วันที่อนุมัติ ภ.พ.06 — ใช้<b>เฉพาะช่องทางสลิป</b></param>
    /// <param name="issueDateUtc">วันที่บนเอกสาร — สลิปห้ามลงวันที่ก่อนวันอนุมัติ ภ.พ.06</param>
    /// <param name="requirePhoR06">สวิตช์แพลตฟอร์ม (<c>false</c> = ปิดด่านชั่วคราว → จด VAT อย่างเดียวก็ออกได้
    /// ทุกช่องทาง — พฤติกรรมเดิมของสวิตช์ ไม่เปลี่ยน)</param>
    /// <param name="channel">ช่องทางที่ออก — <b>ไม่มีค่าตั้งต้นโดยตั้งใจ</b>: ผู้เรียกต้องรู้ว่ากำลังออกสลิปหรือเอกสาร</param>
    public static AbbreviatedInvoiceBlockReason Judge(
        bool isVatRegistered,
        bool isRetailApproved,
        DateTime? phoR06ApprovedDate,
        DateTime issueDateUtc,
        bool requirePhoR06,
        AbbreviatedInvoiceChannel channel)
    {
        // §77/1 — ไม่จด VAT = ออกใบกำกับชนิดใดไม่ได้เลย · สวิตช์แอดมินไม่ครอบข้อนี้
        if (!isVatRegistered)
            return AbbreviatedInvoiceBlockReason.NotVatRegistered;

        // แอดมินปิดด่าน (เผื่อกฎหมายเปลี่ยน) → จด VAT แล้วออกได้เลย
        if (!requirePhoR06)
            return AbbreviatedInvoiceBlockReason.None;

        // §86/6 — ทุกช่องทาง: ต้องเป็นกิจการขายปลีก/บริการลักษณะขายปลีก
        if (!isRetailApproved)
            return AbbreviatedInvoiceBlockReason.NotRetailBusiness;

        // เอกสารจากหน้าเอกสาร (พิมพ์/PDF) ไม่ใช้เครื่องบันทึกการเก็บเงิน ⇒ ไม่ต้องมี ภ.พ.06
        if (channel == AbbreviatedInvoiceChannel.Document)
            return AbbreviatedInvoiceBlockReason.None;

        // สลิปจากเครื่อง — ต้องได้รับอนุมัติ ภ.พ.06 และสลิปลงวันที่ไม่ก่อนวันอนุมัติ
        if (phoR06ApprovedDate is not DateTime approved || issueDateUtc.Date < approved.Date)
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
        bool requirePhoR06,
        AbbreviatedInvoiceChannel channel)
        => Judge(isVatRegistered, isRetailApproved, phoR06ApprovedDate, issueDateUtc, requirePhoR06, channel)
           == AbbreviatedInvoiceBlockReason.None;

    /// <summary>
    /// **หัวกระดาษของเอกสารใบนี้ใช้ "ใบกำกับภาษีอย่างย่อ" ได้ไหม** — ตัวตัดสินตัวเดียวของทั้งสอง renderer (HTML + QuestPDF) และหัวที่
    /// หน้าเว็บ/อีเมลแสดง (รอบ 199 ฝ่ายค้าน C-2)
    /// <para>ใบที่<b>ออกเลขแล้ว</b>และตรึงบทบาทไว้ (<c>Document.IsTaxInvoiceByLaw</c> ≠ null — ตรึงพร้อมเลขที่ตอนอนุมัติ) ⇒ ใช้ค่าที่ตรึง
    /// <b>ไม่ใช่</b>สิทธิ์ของบริษัท ณ วันพิมพ์: เลขที่ออกไปแล้ว (REC/TIV) · รายงานภาษีขาย · e-Tax อ่านค่าที่ตรึง ⇒ หัวที่พิมพ์ซ้ำต้องตรงกับ
    /// ค่าเดียวกัน (§86/4 ห้ามแก้ย้อนหลัง). เดิมหัวคำนวณสดจากธงบริษัท ⇒ ใบ REC ที่ออกก่อนรอบ 199 (บริษัทยังไม่มีสิทธิ์) พิมพ์ซ้ำหลังรอบ 199
    /// เป็น "ใบเสร็จรับเงิน/ใบกำกับภาษีอย่างย่อ" ทั้งที่ระบบ/รายงานไม่นับเป็นใบกำกับ · ทิศกลับ: เจ้าของเอาธงขายปลีกออกทีหลัง ⇒ ใบอย่างย่อ
    /// เดิมพิมพ์ซ้ำเป็น "ใบเสร็จรับเงิน"</para>
    /// <para>ยังไม่ออก (Draft/รออนุมัติ/ถูกปฏิเสธ) หรือใบเก่าที่ไม่เคยตรึง (null — ห้ามตีความว่า false) ⇒ ใช้สิทธิ์ปัจจุบัน
    /// (<paramref name="companyMayIssueNow"/> จาก <see cref="CanIssue"/>) ตามเดิม</para>
    /// <para>⚠️ ค่าที่ตรึง = "หัวมีคำว่าใบกำกับภาษี" (<c>TaxInvoiceSeriesPolicy.CarriesTaxInvoiceRole</c>) · true บนใบรูปอย่างย่อจึงแปลว่า
    /// ตอนออกเป็นอย่างย่อ — ยกเว้นใบชนิด "ใบกำกับภาษี" มี VAT ที่ตอนออกบริษัทไม่มีสิทธิ์ (หัวถูกลดเป็นใบเสร็จแต่ตรึง true เพราะชนิด+VAT):
    /// พิมพ์ซ้ำจะได้หัวอย่างย่อให้ตรงกับเลข TIV/รายงานที่นับเป็นใบกำกับแล้ว (ดู TEST_PLAN ABB-14)</para>
    /// </summary>
    /// <param name="documentIssued"><c>DocumentStatusRules.IsIssued(doc.Status)</c></param>
    /// <param name="frozenTaxInvoiceRole"><c>Document.IsTaxInvoiceByLaw</c></param>
    /// <param name="companyMayIssueNow">ผลของ <see cref="CanIssue"/> ช่องทางเอกสาร ณ ตอนนี้</param>
    public static bool HeadingMayUseAbbreviated(bool documentIssued, bool? frozenTaxInvoiceRole, bool companyMayIssueNow)
        => (documentIssued && frozenTaxInvoiceRole is bool frozen) ? frozen : companyMayIssueNow;

    /// <summary>ข้อความอธิบายพร้อม<b>ทางไปต่อ</b> — <c>null</c> เมื่อออกได้ปกติ</summary>
    public static string? Message(AbbreviatedInvoiceBlockReason reason) => reason switch
    {
        AbbreviatedInvoiceBlockReason.NotVatRegistered =>
            "บริษัทยังไม่ได้จดทะเบียนภาษีมูลค่าเพิ่ม — เอกสารพิมพ์เป็น \"ใบเสร็จรับเงิน\" "
            + "(จด VAT แล้วมาตั้งค่าในหน้าข้อมูลบริษัท)",
        AbbreviatedInvoiceBlockReason.NoPhoR06Approval =>
            "ยังไม่ได้รับอนุมัติ ภ.พ.06 (ขออนุมัติใช้เครื่องบันทึกการเก็บเงิน) — สลิปจากเครื่องจึงออกเป็น"
            + "ใบกำกับภาษีอย่างย่อไม่ได้ · สลิปพิมพ์เป็น \"ใบเสร็จรับเงิน\" ไปก่อน · ยื่น ภ.พ.06 แล้วกรอกวันที่อนุมัติ"
            + "ในหน้าข้อมูลบริษัท (ใบกำกับภาษีอย่างย่อที่ออกจากหน้าเอกสารไม่ต้องใช้ ภ.พ.06)",
        AbbreviatedInvoiceBlockReason.NotRetailBusiness =>
            "ยังไม่ได้ระบุว่ากิจการเป็นการขายปลีก/ให้บริการในลักษณะขายปลีก — ใบกำกับภาษีอย่างย่อออกได้เฉพาะ"
            + "กิจการลักษณะนี้ (§86/6) · เอกสารพิมพ์เป็น \"ใบเสร็จรับเงิน\" ไปก่อน · ติ๊ก \"ประกอบกิจการขายปลีก\" "
            + "ในหน้าข้อมูลบริษัท หรือออกใบกำกับภาษีเต็มรูปแทน",
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
