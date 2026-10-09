using System.Text.Json;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ข้อเท็จจริงของ "กระดาษหลักฐานรับเงิน" หนึ่งใบ — ผู้เรียกคำนวณจาก entity/ตัวตัดสินที่มีอยู่แล้ว
/// (ตาม DECISION_DOCTRINE §1 G6: ตัวตัดสินรับข้อเท็จจริง ไม่รับ service/DbContext)</summary>
/// <param name="CompanyVatRegistered"><c>Company.IsVatRegistered</c> (§77/1)</param>
/// <param name="CompanyMayIssueAbbreviated">ผลของ <see cref="AbbreviatedTaxInvoiceRule.CanIssue"/> ของ<b>ช่องทางนี้</b> (เอกสาร = จด VAT + ขายปลีก · สลิป = + ภ.พ.06)</param>
/// <param name="HasVatLines">ใบนี้พิมพ์ยอด VAT (<c>VatAmount &gt; 0</c>)</param>
/// <param name="DepositTreatment">วิธีบันทึกมัดจำที่ตรึงบนใบ (<see cref="DepositPolicyResolver.OfDocument"/>) · <c>null</c> = ไม่ใช่ใบมัดจำ ·
/// <b>ตัวตัดสินนี้ไม่ตัดสินว่ามัดจำเป็นจุดความรับผิดไหม</b> — รับผลของนโยบายบริษัท/ประเภทมัดจำมาใช้ (คำตัดสินเจ้าของ #34 + 2026-10-09)</param>
/// <param name="BuyerNeedsFullForm">ผู้ซื้อต้องการใบกำกับเต็มรูปเพื่อใช้ภาษีซื้อ — นิติบุคคล/ผู้ประกอบการจด VAT (<c>TaxInvoiceCompletenessChecker.IsJuristicBuyer</c>)</param>
/// <param name="BuyerFullInfoComplete">ข้อมูลผู้ซื้อครบ §86/4(3) (ชื่อ+ที่อยู่ · นิติบุคคล + เลขภาษี 13 หลัก) และผู้ซื้อไม่ได้ปฏิเสธใบกำกับ/ไม่ใช่ walk-in</param>
/// <param name="SettlesExistingTaxInvoice">ใบนี้คือใบเสร็จรับชำระของ "ใบกำกับภาษี" ที่ออกไปแล้ว (<c>RelatedDocumentId</c> → TaxInvoice)</param>
/// <param name="IsRefund">กระดาษคืนเงินมัดจำ (ไม่ใช่ใบรับ)</param>
/// <param name="Channel">ช่องทางที่ออก</param>
public sealed record ReceiptFormFacts(
    bool CompanyVatRegistered,
    bool CompanyMayIssueAbbreviated,
    bool HasVatLines,
    DepositVatTreatment? DepositTreatment,
    bool BuyerNeedsFullForm,
    bool BuyerFullInfoComplete,
    bool SettlesExistingTaxInvoice,
    bool IsRefund,
    ReceiptFormChannel Channel);

/// <summary>ผลตัดสินรูปแบบกระดาษของใบหนึ่ง</summary>
/// <param name="Case">กรณี (คีย์ของค่าตั้งบริษัท)</param>
/// <param name="Allowed">รูปแบบที่กฎหมายอนุญาตสำหรับใบนี้ (ว่าง = ออกใบนี้ไม่ได้จนกว่าจะแก้ข้อเท็จจริง)</param>
/// <param name="Default">ค่าแนะนำของระบบใน <paramref name="Allowed"/> (null เมื่อว่าง)</param>
/// <param name="Chosen">รูปแบบที่จะใช้จริง (ตัวเลือกรายใบ → ค่าตั้งบริษัท → ค่าแนะนำ) · null = ถูกบล็อก</param>
/// <param name="BlockMessage">ข้อความไทยพร้อมทางไปต่อ · null = ไม่บล็อก</param>
/// <param name="RuleCode">รหัสกฎ (ลง audit · ข้อความ)</param>
/// <param name="LegalReference">มาตราที่อ้าง</param>
/// <param name="WhyNot">เหตุผลไทยของ<b>ทุก</b>รูปแบบที่ไม่อนุญาต (หน้าจอแสดงได้ตรง ๆ)</param>
public sealed record ReceiptFormDecision(
    ReceiptFormCase Case,
    IReadOnlyList<ReceiptForm> Allowed,
    ReceiptForm? Default,
    ReceiptForm? Chosen,
    string? BlockMessage,
    string RuleCode,
    string LegalReference,
    IReadOnlyDictionary<ReceiptForm, string> WhyNot)
{
    public bool Blocked => BlockMessage != null;
}

/// <summary>ตัวเลือก 1 ข้อของแถว matrix ในหน้าตั้งค่า (เซิร์ฟเวอร์คำนวณ · หน้าเว็บวาดอย่างเดียว)</summary>
public sealed record ReceiptFormOption(string Value, string Label, bool Allowed, string? WhyNot);

/// <summary>แถว matrix หนึ่งกรณี — <paramref name="Configurable"/> = มีรูปแบบที่อนุญาตมากกว่า 1 จึงมีอะไรให้เลือก ·
/// <paramref name="Preference"/> = ค่าที่บริษัทตั้งไว้ (ชื่อ enum · null = ใช้ค่าแนะนำ)</summary>
public sealed record ReceiptFormMatrixRow(
    string Case, string Label, string Description,
    IReadOnlyList<ReceiptFormOption> Options,
    string? Preference, string? Default, bool Configurable,
    string LegalReference, string? Note);

/// <summary>
/// <b>ตัวตัดสินตัวเดียว</b>ของคำถาม "กระดาษหลักฐานรับเงินใบนี้ ออกเป็นรูปแบบไหนได้บ้าง และจะใช้รูปแบบไหน"
/// (รอบ 203 · คำถามเจ้าของ 2026-10-09 เรื่องใบเสร็จค่าห้องพักที่พิมพ์ VAT แต่หัวเป็น "ใบเสร็จรับเงิน")
///
/// ═══ กฎหมาย (ป.รัษฎากร) ═══
/// <list type="bullet">
/// <item><b>ม.86</b> ผู้ประกอบการจดทะเบียน VAT ต้องออก<b>ใบกำกับภาษี</b>ทุกครั้งที่ความรับผิดเกิด (บริการ §78/1 = เมื่อได้รับชำระ) —
///   รูปแบบที่กฎหมายมีให้คือ <b>เต็มรูป §86/4</b> (ทุกกิจการ · ต้องมีชื่อ+ที่อยู่ผู้ซื้อ) หรือ <b>อย่างย่อ §86/6</b>
///   (เฉพาะกิจการขายปลีก/บริการรายย่อยแก่บุคคลจำนวนมาก · สลิปจากเครื่องต้องมี ภ.พ.06 — <see cref="AbbreviatedTaxInvoiceRule"/>)</item>
/// <item><b>ม.82/5(2)</b> ใบกำกับอย่างย่อใช้เป็นภาษีซื้อไม่ได้ ⇒ ผู้ซื้อที่ต้องการภาษีซื้อ (นิติบุคคล/ผู้ประกอบการ) ต้องได้เต็มรูป</item>
/// <item><b>ม.105</b> ใบรับ (ใบเสร็จรับเงิน) ออกทันทีที่รับเงิน — ใบเสร็จ<b>เปล่า</b>ที่พิมพ์ยอด VAT ของผู้จด VAT ไม่มีสถานะทางกฎหมาย:
///   กระดาษอ้างตัวเป็นเอกสารภาษี (มีฐาน/VAT/สุทธิ) แต่ไม่ใช่ใบกำกับรูปแบบใด ⇒ นี่คือ<b>ข้อบังคับเดียวที่ตัดไม่ได้</b></item>
/// <item><b>ม.78/1 + ป.73/2541</b> มัดจำที่เป็นส่วนหนึ่งของราคา = จุดความรับผิดเมื่อรับเงิน · <b>แต่</b>ธุรกิจต่างกัน วิธีคิดต่างกัน —
///   บางที่พักถือว่ามัดจำยังไม่ใช่การให้บริการ คิด VAT ตอนเข้าพักจริง (คำตัดสินเจ้าของ #34 + 2026-10-09) ⇒ ตัวตัดสินนี้<b>รับ</b>ผล
///   นโยบายมัดจำ (<see cref="DepositVatTreatment"/> ที่ตรึงบนใบ) มาเป็นข้อเท็จจริง ไม่ตัดสินเอง: ยังไม่เป็นจุดความรับผิด ⇒ ใบมัดจำไม่มี
///   VAT ที่ถึงกำหนด ⇒ ใบเสร็จธรรมดาคือรูปแบบที่ถูก และใบสุดท้ายถือ VAT เต็มแล้วหักมัดจำเป็นการชำระ</item>
/// </list>
///
/// ═══ หลักการ (คำตัดสินเจ้าของ 2026-10-09) ═══
/// <para>นี่คือ <b>policy engine</b> ไม่ใช่กฎตายตัว: <see cref="Decide"/> คืน "ชุดที่เป็นไปได้ตามกฎหมาย" + ค่าแนะนำ · ค่าตั้งบริษัท
/// (<see cref="ReceiptFormPolicy"/>) และตัวเลือกรายใบเลือกได้<b>ภายในชุดนั้น</b>เท่านั้น · บล็อกเฉพาะ 3 เรื่องที่กระดาษจะเป็นเท็จ:
/// (ก) พิมพ์ VAT ของผู้จด VAT โดยไม่เป็นใบกำกับรูปแบบใด · (ข) อย่างย่อโดยไม่มีสิทธิ์ §86/6 · (ค) ผู้ซื้อที่ต้องการภาษีซื้อได้แต่ใบย่อ
/// — ทุกข้อความบล็อกต้องบอก<b>ค่าตั้งที่ต้องเปลี่ยนหรือรูปแบบที่ต้องเลือก</b> (กฎเหล็ก #4 F2 ข้อ 8)</para>
/// <para>ตัวนี้<b>ไม่</b>แทน <see cref="AbbreviatedTaxInvoiceRule"/> (สิทธิ์ §86/6) · <see cref="ReceiptIssuePolicy"/> (ออกกี่ใบ) ·
/// <see cref="TaxInvoiceSeriesPolicy"/> (เลขชุด) · <see cref="DepositPolicyResolver"/> (มัดจำ) — มันรับผลของตัวเหล่านั้นเข้ามาแล้วตอบคำถามเดียว:
/// "กระดาษใบนี้ประกาศตัวเป็นอะไรได้"</para>
/// </summary>
public static class ReceiptFormRule
{
    public const string RuleVat = "RCPT-FORM-VAT";
    public const string RuleNonVat = "RCPT-FORM-NONVAT";
    public const string RuleNoVat = "RCPT-FORM-NOVAT";
    public const string RuleDepositDeferred = "RCPT-FORM-DEPOSIT-DEFERRED";
    public const string RuleSettlement = "RCPT-FORM-SETTLE";
    public const string RuleRefund = "RCPT-FORM-REFUND";
    public const string RuleChoice = "RCPT-FORM-CHOICE";

    private static readonly ReceiptForm[] AllForms =
    {
        ReceiptForm.PlainReceipt, ReceiptForm.ReceiptTaxInvoiceFull,
        ReceiptForm.ReceiptTaxInvoiceAbbreviated, ReceiptForm.PaymentEvidenceOnly,
    };

    /// <summary>ชนิดเอกสารที่ตัวตัดสินนี้ครอบ — ใบเสร็จ/ใบสำคัญรับทุกใบ · ใบกำกับภาษีเฉพาะที่มี VAT (ใบกำกับ 0%/ยกเว้น เดินกติกา
    /// <see cref="TaxInvoiceSeriesPolicy.IsZeroRatedFullTaxInvoice"/> เดิม ไม่เกี่ยวกับรูปแบบใบเสร็จ)</summary>
    public static bool AppliesTo(DocumentType type, decimal vatAmount)
        => type is DocumentType.Receipt or DocumentType.ReceiptVoucher
           || (type == DocumentType.TaxInvoice && vatAmount > 0.005m);

    /// <summary>ช่องทางจาก <c>Document.OriginModule</c> — "Lodging" = ที่พัก · อื่น/ว่าง = เอกสารทั่วไป (POS ไม่ผ่านเส้นนี้ — สลิปตัดสินที่ <see cref="PosSlipHeader"/>)</summary>
    public static ReceiptFormChannel ChannelOf(string? originModule)
        => string.Equals(originModule?.Trim(), "Lodging", StringComparison.OrdinalIgnoreCase)
            ? ReceiptFormChannel.Lodging : ReceiptFormChannel.Document;

    /// <summary>VAT ของใบนี้ "ถึงกำหนด" ตอนออกใบไหม — มี VAT และ (ไม่ใช่มัดจำ หรือ นโยบายมัดจำ = รับรู้ทันที)</summary>
    public static bool VatDueNow(ReceiptFormFacts f)
        => f.HasVatLines && (f.DepositTreatment is null || f.DepositTreatment == DepositVatTreatment.VatImmediate);

    /// <summary>กรณีของใบ (คีย์ค่าตั้ง) — ลำดับ: รับชำระใบกำกับ → คืนเงิน → มัดจำ → สลิป → ที่พัก (บุคคล/นิติบุคคล) → ขายสด</summary>
    public static ReceiptFormCase CaseOf(ReceiptFormFacts f)
    {
        if (f.SettlesExistingTaxInvoice) return ReceiptFormCase.SettlementOfTaxInvoice;
        if (f.IsRefund) return ReceiptFormCase.DepositRefund;
        if (f.DepositTreatment is not null) return ReceiptFormCase.Deposit;
        if (f.Channel == ReceiptFormChannel.PosSlip) return ReceiptFormCase.PosSlip;
        if (f.Channel == ReceiptFormChannel.Lodging)
            return f.BuyerNeedsFullForm ? ReceiptFormCase.LodgingFinalBusiness : ReceiptFormCase.LodgingFinalConsumer;
        return ReceiptFormCase.CashSale;
    }

    /// <summary>ตัดสินรูปแบบของใบหนึ่ง</summary>
    /// <param name="facts">ข้อเท็จจริง</param>
    /// <param name="companyPreference">ค่าตั้งบริษัทของกรณีนี้ (<see cref="ReceiptFormPolicy.PreferenceFor"/>) · null = ใช้ค่าแนะนำ</param>
    /// <param name="documentChoice">ตัวเลือกรายใบที่ผู้ใช้ตั้งไว้ก่อนอนุมัติ (<c>Document.ReceiptForm</c>) · null = ไม่ได้เลือก ·
    /// เลือกนอกชุดที่อนุญาต = <b>บล็อก</b>พร้อมเหตุผล (ไม่ทับเงียบ)</param>
    public static ReceiptFormDecision Decide(ReceiptFormFacts facts, ReceiptForm? companyPreference, ReceiptForm? documentChoice)
    {
        var @case = CaseOf(facts);
        var allowed = new List<ReceiptForm>();
        var whyNot = new Dictionary<ReceiptForm, string>();
        string ruleCode;
        string legal;
        string? blockWhenEmpty = null;

        if (facts.SettlesExistingTaxInvoice)
        {
            allowed.Add(ReceiptForm.PaymentEvidenceOnly);
            whyNot[ReceiptForm.PlainReceipt] = "ใบนี้ผูกกับใบกำกับภาษีต้นทาง — ต้องเป็น \"หลักฐานรับชำระ\" ที่อ้างเลขใบกำกับนั้น ไม่ใช่ใบเสร็จลอย";
            whyNot[ReceiptForm.ReceiptTaxInvoiceFull] = "ใบกำกับภาษีของการขายนี้ออกไปแล้ว (ใบต้นทาง) — ออกใบกำกับใบที่สอง = VAT ซ้ำใน ภ.พ.30 (ม.86)";
            whyNot[ReceiptForm.ReceiptTaxInvoiceAbbreviated] = whyNot[ReceiptForm.ReceiptTaxInvoiceFull];
            ruleCode = RuleSettlement;
            legal = "ป.รัษฎากร ม.86 (ใบกำกับฉบับเดียวต่อการขาย) · ม.105 (ใบรับ ณ วันรับเงิน)";
        }
        else if (facts.IsRefund)
        {
            allowed.Add(ReceiptForm.PaymentEvidenceOnly);
            var why = "การคืนเงินมัดจำไม่ใช่การรับเงิน — ออกเป็นหลักฐานการจ่ายคืน · ถ้าใบมัดจำออกเป็นใบกำกับแล้ว VAT เดินทาง \"ใบลดหนี้\" (ม.86/10)";
            whyNot[ReceiptForm.PlainReceipt] = why;
            whyNot[ReceiptForm.ReceiptTaxInvoiceFull] = why;
            whyNot[ReceiptForm.ReceiptTaxInvoiceAbbreviated] = why;
            ruleCode = RuleRefund;
            legal = "ป.รัษฎากร ม.86/10 · ป.73/2541";
        }
        else if (!facts.CompanyVatRegistered)
        {
            allowed.Add(ReceiptForm.PlainReceipt);
            var why = "บริษัทยังไม่ได้จดทะเบียนภาษีมูลค่าเพิ่ม — ออกใบกำกับภาษีรูปแบบใดไม่ได้ (ม.77/1 · ม.86) · จด VAT แล้วมาตั้งค่าในหน้าข้อมูลบริษัท";
            whyNot[ReceiptForm.ReceiptTaxInvoiceFull] = why;
            whyNot[ReceiptForm.ReceiptTaxInvoiceAbbreviated] = why;
            whyNot[ReceiptForm.PaymentEvidenceOnly] = "ใบนี้ไม่ได้รับชำระใบกำกับที่ออกไปแล้ว";
            ruleCode = RuleNonVat;
            legal = "ป.รัษฎากร ม.77/1 · ม.86 · ม.105";
        }
        else if (!VatDueNow(facts))
        {
            allowed.Add(ReceiptForm.PlainReceipt);
            string why;
            if (facts.DepositTreatment is not null && facts.HasVatLines)
            {
                // มัดจำที่นโยบายบริษัท/ประเภทมัดจำยังไม่ถือเป็นจุดความรับผิด (VatPendingUndue) — VAT บนใบคือ "ภาษีขายรอเรียกเก็บ"
                why = "นโยบายมัดจำของบริษัท/ประเภทนี้ยังไม่ถือเป็นจุดความรับผิด (ภาษีคิดที่ใบสุดท้ายตอนเข้าพัก/ส่งมอบ) — "
                    + "ใบมัดจำจึงเป็นใบเสร็จรับเงิน ไม่ใช่ใบกำกับ · เปลี่ยนได้ที่ ตั้งค่า → วิธีบันทึกเงินมัดจำ (รับรู้ VAT ทันที)";
                ruleCode = RuleDepositDeferred;
                legal = "ป.รัษฎากร ม.78/1 · ป.73/2541 · นโยบายมัดจำของบริษัท (DepositVatTreatment)";
            }
            else if (facts.DepositTreatment is not null)
            {
                why = "ใบมัดจำแบบ \"เต็มยอด ไม่แยก VAT\" (เงินประกัน/มัดจำที่ยังไม่ใช่การให้บริการ) ไม่มี VAT ให้ใบกำกับรับรอง — "
                    + "VAT เกิดครั้งเดียวที่ใบสุดท้าย · เปลี่ยนได้ที่ ตั้งค่า → วิธีบันทึกเงินมัดจำ";
                ruleCode = RuleDepositDeferred;
                legal = "ป.รัษฎากร ม.78/1 · ป.73/2541 · นโยบายมัดจำของบริษัท (DepositVatTreatment)";
            }
            else
            {
                why = "ใบนี้ไม่มีภาษีมูลค่าเพิ่ม (สินค้า/บริการยกเว้น §81 หรือไม่มี VAT) — ไม่มีอะไรให้ใบกำกับรับรอง จึงเป็นใบเสร็จรับเงินธรรมดา";
                ruleCode = RuleNoVat;
                legal = "ป.รัษฎากร ม.81 · ม.86 · ม.105";
            }
            whyNot[ReceiptForm.ReceiptTaxInvoiceFull] = why;
            whyNot[ReceiptForm.ReceiptTaxInvoiceAbbreviated] = why;
            whyNot[ReceiptForm.PaymentEvidenceOnly] = "ใบนี้ไม่ได้รับชำระใบกำกับที่ออกไปแล้ว";
        }
        else
        {
            // ผู้จด VAT + VAT ถึงกำหนด ⇒ ต้องเป็นใบกำกับรูปแบบใดรูปแบบหนึ่ง (ม.86)
            if (facts.BuyerFullInfoComplete)
                allowed.Add(ReceiptForm.ReceiptTaxInvoiceFull);
            else
                whyNot[ReceiptForm.ReceiptTaxInvoiceFull] = facts.BuyerNeedsFullForm
                    ? "ใบกำกับเต็มรูปต้องมีชื่อ ที่อยู่ และเลขผู้เสียภาษี 13 หลักของผู้ซื้อ (ม.86/4(3)) — เติมข้อมูลผู้ซื้อก่อน"
                    : "ใบกำกับเต็มรูปต้องมีชื่อ+ที่อยู่ผู้ซื้อ (ม.86/4(3)) — เติมข้อมูลผู้ซื้อ (หรือผู้ซื้อแจ้งไม่ประสงค์รับใบกำกับ ⇒ ใช้อย่างย่อ)";

            if (facts.BuyerNeedsFullForm)
                whyNot[ReceiptForm.ReceiptTaxInvoiceAbbreviated] =
                    "ผู้ซื้อเป็นนิติบุคคล/ผู้ประกอบการจด VAT ต้องได้ใบกำกับเต็มรูปเพื่อใช้ภาษีซื้อ — ใบกำกับอย่างย่อใช้เป็นภาษีซื้อไม่ได้ (ม.82/5(2))";
            else if (!facts.CompanyMayIssueAbbreviated)
                whyNot[ReceiptForm.ReceiptTaxInvoiceAbbreviated] = facts.Channel == ReceiptFormChannel.PosSlip
                    ? "สลิปจากเครื่องออกเป็นใบกำกับอย่างย่อได้เมื่อบริษัทติ๊ก \"ประกอบกิจการขายปลีก\" และกรอกวันที่อนุมัติ ภ.พ.06 ในหน้าข้อมูลบริษัท (ม.86/6)"
                    : "บริษัทยังไม่ได้ระบุว่าเป็นกิจการขายปลีก/ให้บริการรายย่อยแก่บุคคลจำนวนมาก — ใบกำกับอย่างย่อออกได้เฉพาะกิจการลักษณะนี้ (ม.86/6) · "
                      + "ติ๊ก \"ประกอบกิจการขายปลีก\" ในหน้าข้อมูลบริษัท";
            else
                allowed.Add(ReceiptForm.ReceiptTaxInvoiceAbbreviated);

            whyNot[ReceiptForm.PlainReceipt] =
                "กระดาษพิมพ์ยอดก่อน VAT / VAT / สุทธิ = อ้างตัวเป็นเอกสารภาษี — ผู้จด VAT ต้องออกใบกำกับภาษี (เต็มรูปหรืออย่างย่อ) "
                + "ทุกครั้งที่รับเงินค่าบริการ (ม.86 · ม.78/1) · ใบเสร็จเปล่าที่มี VAT ไม่มีสถานะทางกฎหมายและผู้ซื้อใช้อะไรไม่ได้";
            whyNot[ReceiptForm.PaymentEvidenceOnly] = "ใบนี้ไม่ได้รับชำระใบกำกับที่ออกไปแล้ว — ใบกำกับของการขายนี้คือใบนี้เอง";
            ruleCode = RuleVat;
            legal = "ป.รัษฎากร ม.86 · ม.86/4 · ม.86/6 · ม.82/5(2) · ม.78/1";

            if (allowed.Count == 0)
                blockWhenEmpty =
                    "ออกใบนี้ไม่ได้ — ใบมีภาษีมูลค่าเพิ่ม แต่ยังออกเป็นใบกำกับภาษีรูปแบบใดไม่ได้ · "
                    + $"เต็มรูป: {whyNot[ReceiptForm.ReceiptTaxInvoiceFull]} · "
                    + $"อย่างย่อ: {whyNot[ReceiptForm.ReceiptTaxInvoiceAbbreviated]} · "
                    + "ทางแก้: (1) เติมชื่อ+ที่อยู่ผู้ซื้อแล้วอนุมัติใหม่ (ใบกำกับเต็มรูป) หรือ (2) ตั้งค่า → ข้อมูลบริษัท → ติ๊ก "
                    + "\"ประกอบกิจการขายปลีก/ให้บริการรายย่อย (§86/6)\" (ใบกำกับอย่างย่อ) · "
                    + "ใบเสร็จรับเงินเปล่าที่พิมพ์ VAT ไม่ใช่ทางเลือก (ม.86)";
        }

        var defaultForm = allowed.Count == 0 ? (ReceiptForm?)null
            : allowed.Contains(ReceiptForm.ReceiptTaxInvoiceFull) ? ReceiptForm.ReceiptTaxInvoiceFull
            : allowed[0];

        string? block = blockWhenEmpty;
        ReceiptForm? chosen = null;
        if (block == null)
        {
            if (documentChoice is ReceiptForm dc)
            {
                if (allowed.Contains(dc)) chosen = dc;
                else
                {
                    block = $"เลือกรูปแบบ \"{Label(dc)}\" ให้ใบนี้ไม่ได้ — "
                        + (whyNot.TryGetValue(dc, out var w) ? w : "ไม่อยู่ในรูปแบบที่กฎหมายอนุญาตสำหรับใบนี้")
                        + " · รูปแบบที่เลือกได้: " + string.Join(" / ", allowed.Select(Label))
                        + " — เปลี่ยนรูปแบบบนใบ หรือล้างเพื่อใช้ค่าตั้งของบริษัท";
                    ruleCode = RuleChoice;
                }
            }
            else if (companyPreference is ReceiptForm pref && allowed.Contains(pref))
                chosen = pref;
            else
                chosen = defaultForm;
        }

        return new ReceiptFormDecision(@case, allowed, defaultForm, chosen, block, ruleCode, legal, whyNot);
    }

    /// <summary>ชื่อสั้นสำหรับ dropdown/ป้าย/ข้อความ (ชุดเดียว — ห้ามหน้าจอแต่งเอง)</summary>
    public static string Label(ReceiptForm form) => form switch
    {
        ReceiptForm.PlainReceipt => "ใบเสร็จรับเงิน (ไม่มี VAT)",
        ReceiptForm.ReceiptTaxInvoiceFull => "ใบเสร็จรับเงิน/ใบกำกับภาษี (เต็มรูป §86/4)",
        ReceiptForm.ReceiptTaxInvoiceAbbreviated => "ใบเสร็จรับเงิน/ใบกำกับภาษีอย่างย่อ (§86/6)",
        ReceiptForm.PaymentEvidenceOnly => "ใบเสร็จรับเงิน — หลักฐานรับชำระของใบกำกับที่ออกแล้ว",
        _ => form.ToString(),
    };

    /// <summary>ชื่อกรณีสำหรับหัวแถว matrix</summary>
    public static string CaseLabel(ReceiptFormCase c) => c switch
    {
        ReceiptFormCase.LodgingFinalConsumer => "ที่พัก — ใบเช็คเอาต์ให้แขกบุคคลธรรมดา",
        ReceiptFormCase.LodgingFinalBusiness => "ที่พัก — ใบเช็คเอาต์ในนามบริษัท/นิติบุคคล",
        ReceiptFormCase.Deposit => "ใบรับมัดจำ (ที่พัก/ทั่วไป)",
        ReceiptFormCase.PosSlip => "สลิป POS (เครื่องบันทึกการเก็บเงิน)",
        ReceiptFormCase.CashSale => "ขายสด — ใบเสนอราคา → ใบเสร็จ / ใบเสร็จที่คีย์เอง (ลูกค้าบุคคลธรรมดา)",
        ReceiptFormCase.SettlementOfTaxInvoice => "ใบเสร็จรับชำระของใบกำกับภาษีที่ออกแล้ว",
        ReceiptFormCase.DepositRefund => "คืนเงินมัดจำ",
        _ => c.ToString(),
    };

    /// <summary>คำอธิบายกรณี (บรรทัดใต้หัวแถว)</summary>
    public static string CaseDescription(ReceiptFormCase c) => c switch
    {
        ReceiptFormCase.LodgingFinalConsumer => "แขกไม่มีเลขผู้เสียภาษีนิติบุคคล — ถ้ามีชื่อ+ที่อยู่ออกเต็มรูปได้ · กิจการขายปลีก/บริการรายย่อยเลือกอย่างย่อได้",
        ReceiptFormCase.LodgingFinalBusiness => "แขกขอใบกำกับในนามบริษัท — ต้องเต็มรูปเท่านั้นเพื่อใช้ภาษีซื้อ",
        ReceiptFormCase.Deposit => "รูปแบบตามนโยบายมัดจำ: \"รับรู้ VAT ทันที\" ⇒ เหมือนใบเช็คเอาต์ · \"เต็มยอด/ภาษีรอเรียกเก็บ\" ⇒ ใบเสร็จรับเงินธรรมดาเท่านั้น",
        ReceiptFormCase.PosSlip => "สลิปตัดสินโดยกติกาช่องทางสลิป (จด VAT + ขายปลีก + ภ.พ.06) — ขอใบกำกับเต็มรูปจาก POS = ออกเอกสารแยกอีกใบ",
        ReceiptFormCase.CashSale => "ลูกค้าบุคคลธรรมดา — นิติบุคคลได้เต็มรูปเสมอ (ไม่ขึ้นกับค่าตั้งนี้)",
        ReceiptFormCase.SettlementOfTaxInvoice => "VAT รายงานที่ใบกำกับต้นทางแล้ว — ใบเสร็จอ้างเลขใบกำกับ ไม่พิมพ์ตัวเองเป็นใบกำกับใบที่สอง",
        ReceiptFormCase.DepositRefund => "ไม่ใช่การรับเงิน — ถ้ามัดจำออกใบกำกับแล้ว ส่วน VAT เดินทางใบลดหนี้ (§86/10)",
        _ => "",
    };

    /// <summary>
    /// ตาราง matrix สำหรับหน้าตั้งค่า — ทุกแถวคำนวณด้วย <see cref="Decide"/> ตัวเดียวกับด่านอนุมัติ บนข้อเท็จจริง "ตัวแทน" ของกรณีนั้น
    /// (ผู้ซื้อข้อมูลครบ · มัดจำแบบรับรู้ VAT ทันที) ⇒ หน้าเว็บเห็นชุดเดียวกับที่ใบจริงจะเจอ · แถวที่เหลือตัวเลือกเดียว = ไม่มีอะไรให้ตั้ง
    /// </summary>
    /// <param name="companyVatRegistered">จด VAT</param>
    /// <param name="mayIssueAbbreviatedDocument"><see cref="AbbreviatedTaxInvoiceRule.CanIssue"/> ช่องทางเอกสาร</param>
    /// <param name="mayIssueAbbreviatedSlip"><see cref="AbbreviatedTaxInvoiceRule.CanIssue"/> ช่องทางสลิป</param>
    /// <param name="policy">ค่าตั้งปัจจุบันของบริษัท</param>
    public static IReadOnlyList<ReceiptFormMatrixRow> Matrix(
        bool companyVatRegistered, bool mayIssueAbbreviatedDocument, bool mayIssueAbbreviatedSlip, ReceiptFormPolicy policy)
    {
        var rows = new List<ReceiptFormMatrixRow>();
        foreach (var c in Enum.GetValues<ReceiptFormCase>())
        {
            var facts = RepresentativeFacts(c, companyVatRegistered, mayIssueAbbreviatedDocument, mayIssueAbbreviatedSlip);
            var pref = policy.PreferenceFor(c);
            var d = Decide(facts, pref, null);
            var options = AllForms.Select(f => new ReceiptFormOption(
                f.ToString(), Label(f), d.Allowed.Contains(f),
                d.Allowed.Contains(f) ? null : (d.WhyNot.TryGetValue(f, out var w) ? w : null))).ToList();
            var configurable = d.Allowed.Count > 1 && c != ReceiptFormCase.PosSlip;
            string? note = c switch
            {
                ReceiptFormCase.PosSlip => "แถวนี้แสดงผลของกติกาช่องทางสลิปอย่างเดียว (ตั้งค่าที่ ข้อมูลบริษัท → ขายปลีก / ภ.พ.06)",
                ReceiptFormCase.Deposit => "ค่าที่ตั้งตรงนี้มีผลเฉพาะมัดจำที่นโยบาย \"รับรู้ VAT ทันที\" — มัดจำที่ยังไม่เป็นจุดความรับผิดเป็นใบเสร็จรับเงินเสมอ",
                _ => null,
            };
            rows.Add(new ReceiptFormMatrixRow(
                c.ToString(), CaseLabel(c), CaseDescription(c), options,
                pref?.ToString(), d.Default?.ToString(), configurable, d.LegalReference, note));
        }
        return rows;
    }

    private static ReceiptFormFacts RepresentativeFacts(ReceiptFormCase c, bool vat, bool mayAbbrevDoc, bool mayAbbrevSlip) => c switch
    {
        ReceiptFormCase.LodgingFinalConsumer => new ReceiptFormFacts(vat, mayAbbrevDoc, true, null, false, true, false, false, ReceiptFormChannel.Lodging),
        ReceiptFormCase.LodgingFinalBusiness => new ReceiptFormFacts(vat, mayAbbrevDoc, true, null, true, true, false, false, ReceiptFormChannel.Lodging),
        ReceiptFormCase.Deposit => new ReceiptFormFacts(vat, mayAbbrevDoc, true, DepositVatTreatment.VatImmediate, false, true, false, false, ReceiptFormChannel.Lodging),
        // สลิปไม่มีข้อมูลผู้ซื้อ ⇒ เต็มรูปไม่ใช่ตัวเลือกของสลิป (ขอเต็มรูป = ออกเอกสารแยก)
        ReceiptFormCase.PosSlip => new ReceiptFormFacts(vat, mayAbbrevSlip, true, null, false, false, false, false, ReceiptFormChannel.PosSlip),
        ReceiptFormCase.CashSale => new ReceiptFormFacts(vat, mayAbbrevDoc, true, null, false, true, false, false, ReceiptFormChannel.Document),
        ReceiptFormCase.SettlementOfTaxInvoice => new ReceiptFormFacts(vat, mayAbbrevDoc, false, null, false, true, true, false, ReceiptFormChannel.Document),
        ReceiptFormCase.DepositRefund => new ReceiptFormFacts(vat, mayAbbrevDoc, false, DepositVatTreatment.VatImmediate, false, true, false, true, ReceiptFormChannel.Document),
        _ => new ReceiptFormFacts(vat, mayAbbrevDoc, true, null, false, true, false, false, ReceiptFormChannel.Document),
    };
}

/// <summary>
/// ค่าตั้งบริษัท "แต่ละกรณีให้ออกใบแบบไหน" — เก็บเป็น JSON object คอลัมน์เดียว <c>CompanySettings.ReceiptFormPolicyJson</c>
/// (<c>{"LodgingFinalConsumer":"ReceiptTaxInvoiceAbbreviated", …}</c>) · คีย์/ค่า = ชื่อ enum
///
/// <para>ทำไม JSON คอลัมน์เดียวแทน 7 คอลัมน์: กรณีมีแค่ 3 ที่มีอะไรให้เลือกจริง (ที่เหลือกฎหมายเหลือตัวเลือกเดียว) · กรณีจะเพิ่ม/ลด
/// ตามโมดูลที่ tenant ใช้ (ที่พัก/POS) · ไม่มีใครต้อง query ค่านี้ด้วย SQL · ตัวแปลง <see cref="Parse"/> ทนค่าขยะ (คีย์/ค่าไม่รู้จัก = ข้าม)
/// และ <see cref="Normalize"/> ปฏิเสธดัง ๆ ตอน<b>บันทึก</b> (ไม่ใช่ตอนอ่าน) — ค่าที่เก็บไว้จึงถูกเสมอ</para>
/// </summary>
public sealed class ReceiptFormPolicy
{
    public static readonly ReceiptFormPolicy Empty = new(new Dictionary<ReceiptFormCase, ReceiptForm>());

    private readonly IReadOnlyDictionary<ReceiptFormCase, ReceiptForm> _prefs;
    private ReceiptFormPolicy(IReadOnlyDictionary<ReceiptFormCase, ReceiptForm> prefs) => _prefs = prefs;

    public IReadOnlyDictionary<ReceiptFormCase, ReceiptForm> Preferences => _prefs;

    /// <summary>ค่าตั้งของกรณี · null = ไม่ได้ตั้ง (ใช้ค่าแนะนำของ <see cref="ReceiptFormRule.Decide"/>)</summary>
    public ReceiptForm? PreferenceFor(ReceiptFormCase c) => _prefs.TryGetValue(c, out var f) ? f : null;

    /// <summary>อ่านจาก JSON ที่เก็บไว้ — ทนทุกอย่าง (null/ว่าง/ผิดรูป/คีย์ไม่รู้จัก ⇒ ข้าม) เพราะเส้นอ่านอยู่ในด่านอนุมัติ ห้ามล้มเพราะค่าตั้ง</summary>
    public static ReceiptFormPolicy Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Empty;
        Dictionary<string, string?>? raw;
        try { raw = JsonSerializer.Deserialize<Dictionary<string, string?>>(json); }
        catch (JsonException) { return Empty; }
        if (raw == null || raw.Count == 0) return Empty;
        var prefs = new Dictionary<ReceiptFormCase, ReceiptForm>();
        foreach (var kv in raw)
        {
            if (!Enum.TryParse<ReceiptFormCase>(kv.Key, ignoreCase: true, out var c) || !Enum.IsDefined(c)) continue;
            if (string.IsNullOrWhiteSpace(kv.Value)) continue;
            if (!Enum.TryParse<ReceiptForm>(kv.Value, ignoreCase: true, out var f) || !Enum.IsDefined(f)) continue;
            prefs[c] = f;
        }
        return new ReceiptFormPolicy(prefs);
    }

    /// <summary>ตรวจ JSON ที่ผู้ใช้ส่งมาบันทึก — คืน JSON มาตรฐาน (คีย์/ค่าเป็นชื่อ enum เรียงตามกรณี · ค่าว่าง = ลบกรณีนั้น) ·
    /// <c>null</c> = ไม่มีอะไรตั้งเลย (เก็บ NULL) · คีย์/ค่าที่ไม่รู้จัก ⇒ <see cref="BusinessRuleException"/> (ห้ามข้ามเงียบ = silent no-op)</summary>
    public static string? Normalize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        Dictionary<string, string?>? raw;
        try { raw = JsonSerializer.Deserialize<Dictionary<string, string?>>(json); }
        catch (JsonException ex)
        {
            throw new BusinessRuleException("รูปแบบค่าตั้ง \"รูปแบบใบเสร็จ\" ไม่ถูกต้อง (JSON): " + ex.Message, "RCPT-FORM-POLICY");
        }
        if (raw == null || raw.Count == 0) return null;
        var prefs = new SortedDictionary<int, KeyValuePair<string, string>>();
        foreach (var kv in raw)
        {
            if (!Enum.TryParse<ReceiptFormCase>(kv.Key, ignoreCase: true, out var c) || !Enum.IsDefined(c))
                throw new BusinessRuleException($"ไม่รู้จักกรณีใบเสร็จ \"{kv.Key}\"", "RCPT-FORM-POLICY");
            if (string.IsNullOrWhiteSpace(kv.Value)) continue;   // ว่าง = ใช้ค่าแนะนำ (ไม่เก็บ)
            if (!Enum.TryParse<ReceiptForm>(kv.Value, ignoreCase: true, out var f) || !Enum.IsDefined(f))
                throw new BusinessRuleException($"ไม่รู้จักรูปแบบใบเสร็จ \"{kv.Value}\" ของกรณี {ReceiptFormRule.CaseLabel(c)}", "RCPT-FORM-POLICY");
            prefs[(int)c] = new KeyValuePair<string, string>(c.ToString(), f.ToString());
        }
        if (prefs.Count == 0) return null;
        var ordered = new Dictionary<string, string>();
        foreach (var p in prefs.Values) ordered[p.Key] = p.Value;
        return JsonSerializer.Serialize(ordered);
    }
}
