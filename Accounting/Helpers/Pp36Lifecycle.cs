using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ข้อเท็จจริงของใบ ภ.พ.36 ใน <b>GL ที่มีผล</b> (บาท) — อ่านจาก JE ของใบเอง (<c>SourceDocumentId</c>) ที่ Posted และไม่ใช่คู่กลับรายการ</summary>
/// <param name="Pp36Payable">Cr − Dr ผัง 21912 (VAT ประเมินเองค้างนำส่ง) — 0 = ใบนี้ไม่มี JE §83/6 ที่มีผล</param>
/// <param name="UndueInputVat">Dr − Cr ผัง 11640 (ภาษีซื้อยังไม่ถึงกำหนด) ของใบ — ยอดที่การรับรู้ต้องย้ายไป 11610 ·
/// 0 ได้แม้มี 21912 (บริษัทไม่จด VAT · บรรทัดภาษีซื้อต้องห้าม ⇒ VAT ลงค่าใช้จ่ายไปแล้ว)</param>
public readonly record struct Pp36LedgerFacts(decimal Pp36Payable, decimal UndueInputVat)
{
    /// <summary>ใบมีหนี้ ภ.พ.36 ใน GL จริงไหม (ตัวตัดสินเดียวของ "นับ/นำส่ง/รายงานได้")</summary>
    public bool HasPp36Journal => Pp36Payable > 0.005m;
}

/// <summary>ใบนี้อยู่ในรายการนำส่ง ภ.พ.36 แล้ว/รับรู้แล้ว (ผลของ <c>Pp36Ledger.RemittedStatusAsync</c>)</summary>
public sealed record Pp36RemitStatus(string DocumentNumber, int PeriodYear, int PeriodMonth, DateTime RemittedAt,
    bool Recognized, string? RdReceiptNumber);

/// <summary>สถานะ ภ.พ.36 ของใบ (ฝั่งเซิร์ฟเวอร์คำนวณ · หน้าเว็บแสดงอย่างเดียว)</summary>
public enum Pp36DocState
{
    /// <summary>ไม่ใช่ใบที่ถือ ภ.พ.36 (ไม่ได้ติ๊ก · ชนิดอื่น · ใบสำคัญจ่ายที่ปิดหนี้ใบต้นทาง · ยังไม่ออก/ยกเลิก)</summary>
    NotApplicable = 0,
    /// <summary>อนุมัติแล้วแต่ GL ไม่มี Cr 21912 ของใบนี้ — ต้องซ่อม JE ก่อน (ไม่นับเงียบ)</summary>
    NoJournal = 1,
    /// <summary>มีหนี้ 21912 แต่ยังไม่อยู่ในรายการนำส่งใด</summary>
    AwaitingRemittance = 2,
    /// <summary>อยู่ในรายการนำส่งแล้ว ภาษีซื้อยังพัก 11640</summary>
    RemittedAwaitingRecognition = 3,
    /// <summary>รับรู้ภาษีซื้อแล้ว (11640 → 11610) เข้า ภ.พ.30 เดือนเคลม</summary>
    Recognized = 4,
    /// <summary>นำส่งแล้ว แต่ไม่มีภาษีซื้อให้รับรู้ (บริษัทไม่จด VAT / ภาษีซื้อต้องห้าม — VAT เป็นต้นทุน)</summary>
    RemittedNoInputVat = 5,
}

/// <summary>
/// <b>วงจรนำส่ง/รับรู้ ภ.พ.36 — ตัวตัดสิน pure ตัวเดียว</b> (รอบ 203 ทีม F3 · PP36_REVIEW E-2..E-10 · คำตัดสินข้อ 129, 133–136)
///
/// <para>═══ ลำดับ ═══ อนุมัติใบ (Dr ค่าใช้จ่าย · Dr 11640 / Cr 21912 · Cr ผู้รับเงิน) → นำส่ง ภ.พ.36 (Dr 21912 / Cr ธนาคาร — ผูกใบเข้ารายการนำส่ง)
/// → ได้ใบเสร็จกรมสรรพากร → รับรู้ภาษีซื้อ (Dr 11610 / Cr 11640 ลงวันใบเสร็จ) → ภ.พ.30 เดือนนั้นเคลม · หลักกฎหมาย §82/4 ประกอบใบเสร็จ RD
/// (เลิกอ้าง §77/2 — คำตัดสินข้อ 129)</para>
/// <para>ทุกเส้น (ยอดค้าง · ปฏิทิน · นำส่ง · รับรู้ · รายงาน ภ.พ.36 · ป้ายบนรายการเอกสาร) ตัดสินผ่านไฟล์นี้ + <c>Pp36Ledger</c> (ตัวโหลด GL) เท่านั้น</para>
/// </summary>
public static class Pp36Lifecycle
{
    public const string LegalReference = "RD-82/4";
    /// <summary>ไม่มีเลข/วันที่ใบเสร็จกรมสรรพากร ⇒ รับรู้ไม่ได้ (คำตัดสินข้อ 136)</summary>
    public const string RuleNoReceipt = "PP36-RD-RECEIPT-REQUIRED";
    /// <summary>วันเคลมก่อนวันใบเสร็จ (คำตัดสินข้อ 129)</summary>
    public const string RuleClaimBeforeReceipt = "PP36-CLAIM-BEFORE-RECEIPT";
    /// <summary>เคลมเข้างวด ภ.พ.30 ที่ยื่น/ล็อกแล้ว</summary>
    public const string RuleClaimPeriodFiled = "PP36-CLAIM-PERIOD-FILED";
    /// <summary>ยกเลิก/ปลดธง/แก้ยอดหลังนำส่งหรือรับรู้ (คำตัดสินข้อ 134)</summary>
    public const string RuleChangeAfterRemit = "PP36-CHANGE-AFTER-REMIT";
    /// <summary>ติ๊กบริการต่างประเทศบนชนิดที่ไม่ใช่เอกสารซื้อ (E-8)</summary>
    public const string RuleWrongDocumentType = "PP36-FLAG-WRONG-TYPE";
    /// <summary>เงินเพิ่ม §89/1 (คำตัดสินข้อ 135)</summary>
    public const string SurchargeLegalReference = "RD-89/1";

    /// <summary>
    /// สถานะของใบ — ลำดับการตัดสิน: ไม่ใช่เจ้าของ/ไม่มีผล ⇒ NotApplicable · รับรู้แล้ว ⇒ Recognized · อยู่ในรายการนำส่ง ⇒ (มีภาษีซื้อพัก ? รอรับรู้ : ไม่มีภาษีซื้อ) ·
    /// GL ไม่มี 21912 ⇒ NoJournal · อื่น ⇒ รอนำส่ง
    /// <para>"อยู่ในรายการนำส่ง" ชนะ "ไม่มี JE" — ใบที่นำส่งแล้ว (ข้อมูลเก่าก่อนมี GL ครบ) ต้องไม่ถูกเชิญให้นำส่งซ้ำ</para>
    /// </summary>
    public static Pp36DocState Classify(bool ownsPp36, bool effective, Pp36LedgerFacts ledger,
        bool inRemittance, bool recognized)
    {
        if (!ownsPp36 || !effective) return Pp36DocState.NotApplicable;
        if (recognized) return Pp36DocState.Recognized;
        if (inRemittance)
            return ledger.UndueInputVat > 0.005m ? Pp36DocState.RemittedAwaitingRecognition : Pp36DocState.RemittedNoInputVat;
        if (!ledger.HasPp36Journal) return Pp36DocState.NoJournal;
        return Pp36DocState.AwaitingRemittance;
    }

    /// <summary>ป้ายภาษาไทยของสถานะ — ตัวเดียวที่หน้ารายการเอกสาร/หน้านำส่งแสดง (null = ไม่ต้องติดป้าย)</summary>
    public static string? Label(Pp36DocState state, DateTime? claimAt) => state switch
    {
        Pp36DocState.NoJournal => "ภ.พ.36 · ไม่มี JE ต้องซ่อม",
        Pp36DocState.AwaitingRemittance => "ภ.พ.36 · รอนำส่ง",
        Pp36DocState.RemittedAwaitingRecognition => "ภ.พ.36 · นำส่งแล้ว รอรับรู้",
        Pp36DocState.RemittedNoInputVat => "ภ.พ.36 · นำส่งแล้ว (ไม่มีภาษีซื้อให้เคลม)",
        Pp36DocState.Recognized => claimAt is DateTime c
            ? $"ภ.พ.36 · รับรู้แล้ว เคลม ภ.พ.30 เดือน {c:MM}/{c.Year + 543}"
            : "ภ.พ.36 · รับรู้แล้ว",
        _ => null,
    };

    /// <summary>
    /// <b>วันเคลมภาษีซื้อ ภ.พ.36</b> (คำตัดสินข้อ 129) — ค่าเริ่มต้น = วันที่ใบเสร็จกรมสรรพากร (วันชำระ ภ.พ.36) · ผู้ใช้เลือกวันอื่นได้แต่<b>ห้ามก่อนวันใบเสร็จ</b>
    /// (ไม่มีหลักฐาน = เคลมไม่ได้) · JE รับรู้ลงวันเดียวกัน
    /// </summary>
    /// <returns>(วันเคลม, null) หรือ (null, ข้อความปฏิเสธพร้อมทางไปต่อ)</returns>
    public static (DateTime? Date, string? Error) ResolveClaimDate(DateTime rdReceiptDate, DateTime? requested)
    {
        var receipt = rdReceiptDate.Date;
        if (requested is not DateTime r) return (receipt, null);
        if (r.Date < receipt)
            return (null, $"วันเคลม {r:dd/MM/yyyy} อยู่ก่อนวันที่ใบเสร็จกรมสรรพากร {receipt:dd/MM/yyyy} — ภาษีซื้อ ภ.พ.36 เคลมได้ตั้งแต่เดือนที่ชำระภาษีนั้น "
                + "(§82/4 ประกอบใบเสร็จ) · เว้นว่างเพื่อใช้วันที่ใบเสร็จ หรือเลือกวันที่ไม่ก่อนใบเสร็จ");
        return (r.Date, null);
    }

    /// <summary>ข้อความปฏิเสธ "งวด ภ.พ.30 ที่จะเคลมยื่น/ล็อกแล้ว" พร้อมทางไปต่อ</summary>
    public static string ClaimPeriodFiledMessage(DateTime claimDate)
        => $"รายงาน ภ.พ.30 งวด {claimDate:MM}/{claimDate.Year + 543} ยื่น/ล็อกแล้ว — เคลมภาษีซื้อ ภ.พ.36 เข้างวดนั้นไม่ได้ · "
           + "เลือกวันเคลมในงวดถัดไปที่ยังไม่ยื่น (ไม่เกิน 6 เดือนตาม §82/3) หรือปลดล็อก/ยื่นแบบเพิ่มเติมก่อน";

    /// <summary>
    /// <b>เงินเพิ่ม §89/1</b> (คำตัดสินข้อ 135) — 1.5% ต่อเดือนหรือเศษของเดือนของภาษีที่นำส่งช้า ไม่เกินจำนวนภาษี · ค่าแนะนำ (ผู้ใช้แก้ได้) ·
    /// สูตร "เดือนหรือเศษของเดือน + เพดาน" ใช้ตัวเดียวกับ ม.27 (<see cref="RevenueCodeSurcharge"/>) · วันครบกำหนดจากผู้เรียก
    /// (<c>TaxFilingDeadline.WarnBy("VatPp36", …)</c> ตัวเดียว)
    /// </summary>
    public static decimal SuggestedSurcharge(decimal vat, DateTime dueDate, DateTime payDate)
        => RevenueCodeSurcharge.Compute(vat, dueDate, payDate);

    /// <summary>
    /// <b>ข้อความบล็อก "แก้ใบหลังนำส่ง/รับรู้"</b> (คำตัดสินข้อ 134) — ยกเลิกใบ · ปลดธงบริการต่างประเทศ · แก้ยอด · ใบลดหนี้
    /// <para>ไม่ทำเส้นขอคืนอัตโนมัติ (ไม่มีหลักฐานภายนอก = R1) · ทางไปต่อคือยื่นแบบเพิ่มเติม/ขอคืนกับสรรพากร แล้วบันทึกปรับปรุงด้วยใบสำคัญทั่วไป</para>
    /// </summary>
    public static string ChangeBlockMessage(string documentNumber, string action, Pp36RemitStatus status)
        => $"{action}ใบ {documentNumber} ไม่ได้ — ใบนี้{SettledReason(status)}"
           + " · ยื่นแบบ ภ.พ.36 เพิ่มเติม/ขอคืนกับกรมสรรพากร แล้วบันทึกปรับปรุงด้วยใบสำคัญทั่วไป";

    /// <summary>เหตุสั้น "นำส่ง/รับรู้แล้ว" (ไม่มีทางไปต่อ — ผู้เรียกต่อเอง เช่นด่านใบลดหนี้ของทีม F2)</summary>
    public static string SettledReason(Pp36RemitStatus s)
        => $"นำส่ง ภ.พ.36 แล้วในรายการนำส่งงวด {s.PeriodMonth:D2}/{s.PeriodYear + 543} (จ่ายเมื่อ {s.RemittedAt:dd/MM/yyyy})"
           + (string.IsNullOrWhiteSpace(s.RdReceiptNumber) ? "" : $" ใบเสร็จกรมสรรพากร {s.RdReceiptNumber}")
           + (s.Recognized ? " และรับรู้ภาษีซื้อเข้า ภ.พ.30 แล้ว" : "");

    /// <summary>ข้อความปฏิเสธการติ๊ก "บริการต่างประเทศ" บนชนิดที่ไม่ใช่เอกสารซื้อ (E-8) · null = ผ่าน</summary>
    public static string? FlagTypeError(DocumentType type, bool isForeignService)
        // ชุดชนิดเดียวกับ AutoPost ที่แยกขา §83/6 จริง (ForeignServiceVat.IsSelfAssessingType · PI/Expense/PV) — ใบรับรองแทนใบเสร็จ (CIL)
        // ไม่มีขา Cr 21912 ใน AutoPost ⇒ ธงบน CIL ไม่มีผลทางบัญชี จึงปฏิเสธเหมือนใบขาย (PP36_REVIEW E-8)
        => isForeignService && !ForeignServiceVat.IsSelfAssessingType(type)
            ? "ติ๊ก \"ซื้อบริการจากต่างประเทศ (ภ.พ.36 §83/6)\" ได้เฉพาะใบแจ้งหนี้ซื้อ · ค่าใช้จ่าย · ใบสำคัญจ่าย — "
              + "ใบชนิดนี้ไม่ตั้งหนี้ ภ.พ.36 (เอาเครื่องหมายออกแล้วบันทึกใหม่ · ใบรับรองแทนใบเสร็จให้ออกเป็นใบสำคัญจ่ายแทน)"
            : null;
}
