using Accounting.Models.Entities;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>รหัสปัญหาของแผนลงบัญชีรอบโอน — ทุกตัวมีข้อความ + ทางไปต่อ (ไม่ใช่ throw เปล่า · CLAUDE.md F2 ข้อ 8)</summary>
public enum SettlementPlanIssueCode
{
    /// <summary>Σ บรรทัด ≠ NetPayout + (ปลายรอบ − ต้นรอบ) เกิน ±0.01</summary>
    Unbalanced = 1,
    Unclassified = 2,
    SignNotAllowed = 3,
    AdjustmentReasonMissing = 4,
    AdjustmentAccountMissing = 5,
    /// <summary>คืนเงินที่ไม่รู้ใบขายเดิม — ใบลดหนี้ §86/10 ต้องอ้างใบเดิม</summary>
    RefundUnmatched = 6,
    /// <summary>ยอดขายรวมของใบขาย/วันหนึ่ง ≤ 0 (มีแต่ส่วนลด)</summary>
    SaleGroupNotPositive = 7,
    /// <summary>ค่าธรรมเนียมสุทธิของกลุ่มภาษีเป็นยอดคืน — ต้องมีใบลดหนี้ค่าธรรมเนียมจากแพลตฟอร์ม</summary>
    FeeGroupNetRefund = 8,
    ClearingAccountMissing = 9,
    ExplicitVatInvalid = 10,
    CurrencyNotSupported = 11,
    RevenueModelNotSupported = 12,
    BankAccountMissing = 13,
    /// <summary>หัก ณ ที่จ่ายค่าธรรมเนียมของผู้ให้บริการต่างประเทศที่ระบบคิดให้ไม่ได้ — ประเภทเงินได้นอก ม.70/ไม่รู้ · โหมดตัวแทนหักแทน ·
    /// ผู้ติดต่อต่างประเทศบนช่องทางที่ไม่ได้ตั้งเป็นต่างประเทศ (review198-A R-A5 · รอบ 200 ทีม W: <see cref="SettlementForeignWht"/>) —
    /// ประเภทใน ม.70 คิด ภ.ง.ด.54 ให้แล้ว ไม่บล็อก</summary>
    ForeignWhtNotSupported = 14,
    /// <summary>บรรทัดขายที่ไม่มีใบขายและตัวจับคู่<b>ไม่ได้</b>ตัดสินว่า "ไม่มีร่องรอยที่ไหน" (<c>MatchStatus != AutoSummary</c> — ผู้สมัครกำกวม ·
    /// มีใบเสร็จ/การจองที่อาจเป็นออเดอร์เดียวกัน) — ออกใบสรุปทับ = รายได้ซ้ำ (สัญญาทีม B · รอบ 198)</summary>
    SaleUnmatched = 15,
    /// <summary>บรรทัดขาย/คืนเงินจับคู่ใบได้แต่ยอดไม่ตรงใบ (<c>MatchStatus == AmountMismatch</c>) — ต้องให้คนยืนยันก่อน</summary>
    SaleAmountMismatch = 16,

    // ── ด่านของผู้ลงบัญชี (ทีม C: Helpers/SettlementPosting.SettlementPostingGate — ต้องรู้ข้อมูลในฐาน) ──
    /// <summary>วันที่ของ JE รอบโอน/เอกสารที่จะสร้าง อยู่ในงวดบัญชีที่ปิดแล้ว</summary>
    PeriodClosed = 20,
    /// <summary>เดือนภาษีของใบขายสรุป (ภ.พ.30) หรือของ WHT (ภ.ง.ด.53) ถูกประกาศว่ายื่นแล้ว (TaxFilingLockPolicy)</summary>
    TaxPeriodFiled = 21,
    /// <summary>หาผังบัญชีของบทบาทหนึ่งไม่เจอ / ผังที่ตั้งไว้ไม่ใช่ของบริษัทนี้หรือถูกปิดใช้</summary>
    AccountUnresolved = 22,
    /// <summary>ช่องทางยังไม่ผูกผู้ติดต่อของแพลตฟอร์ม (ผู้รับเงินค่าธรรมเนียม) หรือผู้ติดต่อไม่มีเลขผู้เสียภาษี (§65 ตรี (18))</summary>
    CounterpartyMissing = 23,
    /// <summary>คืนเงินที่จับคู่ใบเดิมได้แล้ว แต่ใบลดหนี้ต้องให้คนเลือกเหตุผล §86/10 (คืนสินค้า = คืนสต็อก / ปรับราคา) — ทำมือแล้วผูกบรรทัด</summary>
    RefundNeedsCreditNote = 24,
    /// <summary>ใบขายที่จับคู่ไว้รับชำระไม่ได้ (ไม่พบ · คนละบริษัท · ไม่ใช่ใบตั้งลูกหนี้ · สถานะ · ยอดค้างน้อยกว่ายอดโอน)</summary>
    ReceiptDocumentNotPayable = 25,
    /// <summary>บรรทัดที่อ้าง PaymentIntent/การรับชำระ ไม่ได้ลงไว้ที่ผังพักของช่องทางนี้ หรือถูกล้างไปแล้วด้วยเส้นอื่น (review198-A R-A1)</summary>
    ClearingSourceMismatch = 26,
    /// <summary>ผู้กดลงบัญชีไม่มีสิทธิ์อนุมัติเอกสารชนิดที่ระบบจะสร้าง</summary>
    PermissionDenied = 27,
    /// <summary>ออเดอร์/วันเดียวกันของแพลตฟอร์มนี้มีเอกสารขายอยู่แล้ว — ออกใบสรุปซ้ำ = รายได้และภาษีขายซ้ำ (review198-A R-A7)</summary>
    SummarySaleDuplicate = 28,
    /// <summary>รอบโอนนี้ยกเลิกแล้ว / ลงบัญชีแล้ว</summary>
    StatusNotPostable = 29,
    /// <summary>เอกสารจากการลงบัญชีครั้งก่อน (ค้างครึ่งทาง) ไม่อยู่ในแผนปัจจุบัน — บรรทัดถูกแก้ระหว่างนั้น ต้องยกเลิกเอกสารนั้นก่อน</summary>
    StaleDocument = 30,
    /// <summary>ใบขายสรุปมี VAT แต่บริษัทยังไม่มีสิทธิ์ออกใบกำกับภาษีอย่างย่อ (§86/6 · <c>AbbreviatedTaxInvoiceRule</c> ช่องทางเอกสาร) —
    /// ใบที่ออกให้ "ลูกค้าเงินสด" จะถูกลดหัวเป็นใบเสร็จรับเงิน (ชุด REC) ทั้งที่ภาษีขายเข้า ภ.พ.30 (ฝ่ายค้าน review198-C C-6)</summary>
    SummaryTaxInvoiceNotAllowed = 31,
    /// <summary>ช่องทางนี้มีเอกสาร/การรับชำระที่การลงบัญชีสร้างให้รอบโอนที่ถูกยกเลิกแล้ว (ของกำพร้า) — ลงบัญชีรอบใหม่ทับ = ค่าใช้จ่าย/ภาษีซื้อ/
    /// รายได้ซ้ำ (ฝ่ายค้าน review198-C C-1(d))</summary>
    OrphanPostingArtifacts = 32,
    /// <summary>รายการรับชำระที่บรรทัดอ้างถึงมีการคืนเงินที่ผลยังไม่แน่ชัด (<c>PaymentIntent.RefundOutcomeUnknownSince</c>) — ต้องตรวจผลการคืนเงิน
    /// ก่อนลงบัญชี (review198-E2 E2-10)</summary>
    RefundOutcomeUnknown = 33,
    /// <summary>บริษัทเปิดแยกหน้าที่ (SoD) และผู้กดลงบัญชีคือผู้นำเข้ารอบโอนเอง — ผู้ทำ = ผู้อนุมัติของเอกสารที่ระบบออกให้ (คำตัดสินเจ้าของรอบ 198 ข้อ 7)</summary>
    SodSelfApproval = 34,

    // ── แจ้งให้ทราบ (ไม่บล็อก) ──
    /// <summary>ยอด wallet ปลายรอบติดลบ — ยกไปหักรอบถัดไป (report-S1 G7)</summary>
    NegativeBalanceCarried = 50,
    /// <summary>มีบรรทัดขายที่จับคู่ใบขายไม่ได้ ⇒ สร้างใบขายสรุปรายวันให้ + ติดป้ายตรวจ (DECISIONS ข้อ 3)</summary>
    SummarySaleCreated = 51,
    /// <summary>แพลตฟอร์มเป็นตัวแทนหัก ณ ที่จ่าย — เก็บ 50 ทวิ แต่ห้ามนับเข้ายอดที่เรายื่นเอง</summary>
    WhtFiledByAgent = 52,
    /// <summary>ใบขายสรุปลงวันที่เกิน 3 วันทำการก่อนวันนี้ — รายงานภาษีขาย §87 ต้องลงภายใน 3 วันทำการ (review198-A R-A7)</summary>
    SummarySaleLate = 53,
    /// <summary>ลงบัญชีครั้งก่อนค้างครึ่งทาง — มีเอกสาร/การรับชำระที่สร้างไว้แล้ว กดลงบัญชีอีกครั้งจะทำต่อจากขั้นที่ค้าง (ไม่สร้างซ้ำ)</summary>
    PartialProgress = 54,
}

/// <summary>ปัญหา 1 ข้อของแผน</summary>
/// <param name="Blocking">true = ลงบัญชีไม่ได้จนกว่าจะแก้</param>
/// <param name="NextStep">ผู้ใช้ทำอะไรต่อ (ข้อความไทย)</param>
public sealed record SettlementPlanIssue(
    SettlementPlanIssueCode Code,
    bool Blocking,
    string Message,
    string NextStep,
    IReadOnlyList<Guid> LineIds,
    decimal? Amount);

/// <summary>ขา JE 1 บรรทัด — ผู้ลงบัญชีใช้ <c>AccountId</c> ถ้ามี ไม่งั้นหา <c>DefaultAccountCode</c> ในผังของบริษัท (tenant) ·
/// บทบาท <c>bank</c> = ผังที่ผูกกับ <c>SettlementPostingPlan.BankAccountId</c> · หาไม่เจอ = ล้มดังพร้อมชื่อบทบาท</summary>
public sealed record SettlementJournalLinePlan(
    string AccountRole,
    Guid? AccountId,
    string? DefaultAccountCode,
    decimal Debit,
    decimal Credit,
    string Description,
    IReadOnlyList<Guid> LineIds);

/// <summary>1 บรรทัดของใบค่าธรรมเนียม (รวมตามประเภท+ผัง) — ยอดเป็นบวก (ค่าธรรมเนียม) · ยอดคืนถูกหักไว้แล้วในก้อนเดียวกัน</summary>
public sealed record SettlementFeeDocumentLine(
    SettlementLineType LineType,
    string LabelTh,
    string AccountRole,
    Guid? AccountId,
    string? DefaultAccountCode,
    decimal Deducted,
    decimal Expense,
    decimal InputVat,
    decimal Pp36Payable,
    string? WhtIncomeCode,
    decimal WhtRatePercent,
    decimal WhtBase,
    decimal WhtAmount,
    decimal WhtCertIncome,
    decimal WhtBorneExpense,
    IReadOnlyList<Guid> LineIds);

/// <summary>ใบค่าธรรมเนียม 1 ใบ (ต่อ batch ต่อกลุ่มภาษี) — เอกสารซื้อจากคู่ค้า <c>SettlementChannel.CounterpartyContactId</c> ·
/// <b>จ่ายเต็ม <c>Deducted</c> จากบัญชีพัก</b> (<c>OverridePaymentAccountId</c> = clearing) · <b>ห้ามหัก WHT ตอนจ่ายซ้ำ</b> — ขา WHT อยู่ใน
/// <c>PayoutJournal</c> แล้ว ใบนี้ถือข้อมูล WHT ไว้ออก 50 ทวิเท่านั้น</summary>
/// <param name="WhtForm">แบบ ภ.ง.ด. ของหนังสือรับรอง/ขา WHT — ภ.ง.ด.53 (แพลตฟอร์มไทย) · ภ.ง.ด.54 (ผู้ให้บริการต่างประเทศ ม.70 · <see cref="SettlementForeignWht.WhtForm"/>)</param>
public sealed record SettlementFeeDocumentPlan(
    SettlementFeeVatTreatment VatTreatment,
    SettlementFeeWhtMode WhtMode,
    decimal Deducted,
    decimal Expense,
    decimal InputVat,
    decimal Pp36Payable,
    decimal WhtAmount,
    IReadOnlyList<SettlementFeeDocumentLine> Lines,
    TaxType WhtForm = TaxType.WithholdingTax53);

/// <summary>รับชำระใบขายที่จับคู่แล้ว — เงินเข้า = บัญชีพัก (<c>OverridePaymentAccountId</c> = clearing)</summary>
public sealed record SettlementReceiptPlan(Guid DocumentId, decimal Amount, IReadOnlyList<Guid> LineIds);

/// <summary>ใบลดหนี้ + จ่ายคืนจากบัญชีพัก ของใบขายเดิม (§86/10)</summary>
public sealed record SettlementRefundPlan(Guid DocumentId, decimal Amount, IReadOnlyList<Guid> LineIds);

/// <summary>ใบขายสรุปรายวันของบรรทัดขายที่จับคู่ไม่ได้ (DECISIONS ข้อ 2–3) — สร้างอัตโนมัติ + <b>ติดป้ายให้ตรวจ</b> · รับชำระเต็มจากบัญชีพัก ·
/// <c>Gross</c> รวม VAT · <c>Net/Vat</c> แยกด้วย <see cref="SettlementFeeTax.SplitInclusive"/> (ไม่จด VAT ⇒ Vat = 0)</summary>
public sealed record SettlementSummarySalePlan(
    DateTime Date,
    decimal Gross,
    decimal Net,
    decimal Vat,
    decimal SaleAmount,
    decimal SellerVoucher,
    decimal PlatformVoucher,
    IReadOnlyList<Guid> LineIds,
    IReadOnlyList<string> ExternalOrderIds);

/// <summary>แผนลงบัญชีของรอบโอน 1 รอบ — ผลของ <see cref="SettlementBatchMath.Plan"/></summary>
/// <param name="CanPost">ไม่มีปัญหาที่บล็อก</param>
/// <param name="LinesTotal">Σ Amount ทุกบรรทัด</param>
/// <param name="ExpectedTotal">NetPayout + (ปลายรอบ − ต้นรอบ)</param>
/// <param name="Difference">LinesTotal − ExpectedTotal</param>
/// <param name="AlreadyInClearing">ยอดบรรทัดที่ลงบัญชีพักไว้แล้วจากทางอื่น (PaymentIntent/Payment) — ไม่ลงซ้ำ</param>
public sealed record SettlementPostingPlan(
    bool CanPost,
    decimal LinesTotal,
    decimal ExpectedTotal,
    decimal Difference,
    decimal NetPayout,
    decimal AlreadyInClearing,
    Guid? ClearingAccountId,
    Guid? BankAccountId,
    IReadOnlyList<SettlementPlanIssue> Issues,
    IReadOnlyList<SettlementJournalLinePlan> PayoutJournal,
    IReadOnlyList<SettlementFeeDocumentPlan> FeeDocuments,
    IReadOnlyList<SettlementReceiptPlan> Receipts,
    IReadOnlyList<SettlementSummarySalePlan> SummarySales,
    IReadOnlyList<SettlementRefundPlan> Refunds)
{
    /// <summary>ผลรวมการเคลื่อนไหวของบัญชีพักจากทุกส่วนของแผน (บวก = Dr) — ต้องเท่ากับ LinesTotal − AlreadyInClearing − NetPayout
    /// เมื่อแผนลงบัญชีได้ (invariant ที่เทสต์ล็อก)</summary>
    public decimal ClearingMovement =>
        PayoutJournal.Where(l => l.AccountRole == SettlementAccountRoles.Clearing).Sum(l => l.Debit - l.Credit)
        + Receipts.Sum(r => r.Amount) + SummarySales.Sum(s => s.Gross)
        - Refunds.Sum(r => r.Amount) - FeeDocuments.Sum(f => f.Deducted);

    /// <summary>สมการรอบโอนลงตัว (ผลต่างอยู่ในเกณฑ์ <see cref="SettlementBatchMath.ToleranceBaht"/>) — หน้าเว็บแสดงสีของผลต่างตามธงนี้
    /// (เดิม JS เทียบ 0.01 เอง = สำเนาเกณฑ์ชุดที่สอง · review198-D D-10) · ตัวเดียวกับเงื่อนไขของปัญหา <c>Unbalanced</c></summary>
    public bool Balanced => SettlementBatchMath.IsBalanced(Difference);
}

/// <summary>
/// **แผนลงบัญชีของรอบโอน settlement — ฟังก์ชันบริสุทธิ์ตัวเดียว** (รอบ 198 เฟส 1 · report-S1 §2–§4/§7 · report-S2 §3)
///
/// <para>═══ หลัก ═══ ยอดเข้าธนาคารจริง (NetPayout) เป็นตัวตั้ง · สมการ Σ บรรทัด = NetPayout + (ปลายรอบ − ต้นรอบ) ±0.01 ·
/// ไม่ลงตัว = ปัญหาพร้อมทางไปต่อ (<b>ไม่เดาส่วนต่าง</b> — ผู้ใช้เพิ่มบรรทัด Adjustment ที่มีเหตุผล หรือแก้ยอด) · ทุกบรรทัดต้องจัดประเภท ·
/// ค่าธรรมเนียมเป็น<b>เอกสารซื้อ</b> 1 ใบ/กลุ่มภาษี จ่ายจากบัญชีพัก · ขาขายใช้การรับชำระเดิม (จับคู่ได้) หรือใบขายสรุปรายวัน (จับไม่ได้ ·
/// DECISIONS ข้อ 3) · คืนเงินต้องอ้างใบเดิม · reserve/chargeback/ภาษีถูกหัก/FX/ปรับปรุง ลง JE รอบโอนตรง</para>
///
/// <para>แผนนี้ไม่แตะฐานข้อมูล — ผู้ลงบัญชี (ทีม C: <c>SettlementPostingService</c>) แปลงเป็น JE ผ่าน <c>JournalEntryBuilder</c> (ด่านงวด) +
/// เอกสารผ่าน <c>IDocumentService</c> ในธุรกรรมเดียว · <b>ห้ามคำนวณยอด/ภาษีซ้ำเองที่อื่น</b></para>
/// </summary>
public static class SettlementBatchMath
{
    /// <summary>ผลต่างที่ยอมรับ (เศษปัดของผู้ให้บริการ)</summary>
    public const decimal ToleranceBaht = 0.01m;

    /// <summary>สมการรอบโอนลงตัวไหม — ตัวตัดสินเดียวของปัญหา <c>Unbalanced</c> และธง <c>SettlementPostingPlan.Balanced</c> ที่หน้าเว็บแสดง</summary>
    public static bool IsBalanced(decimal difference) => Math.Abs(difference) <= ToleranceBaht;

    public static SettlementPostingPlan Plan(
        SettlementBatch batch,
        IReadOnlyList<SettlementLine> lines,
        SettlementChannel channel,
        bool companyVatRegistered)
    {
        var issues = new List<SettlementPlanIssue>();
        var reference = string.IsNullOrWhiteSpace(batch.PayoutRef) ? "(ไม่มีเลขรอบโอน)" : batch.PayoutRef.Trim();
        var feeMap = SettlementLineTypeRules.ParseFeeAccountMap(channel.FeeAccountMapJson).Map;

        // ── 1. สมการของ batch ──
        var linesTotal = lines.Sum(l => l.Amount);
        var expected = batch.NetPayout + (batch.ClosingWalletBalance - batch.OpeningWalletBalance);
        var diff = linesTotal - expected;
        if (!IsBalanced(diff))
            issues.Add(new SettlementPlanIssue(SettlementPlanIssueCode.Unbalanced, true,
                $"ยอดรวมบรรทัด ({linesTotal:N2}) ไม่เท่ากับยอดโอนเข้า ({batch.NetPayout:N2}) + ยอด wallet ที่เปลี่ยน "
                + $"({batch.ClosingWalletBalance - batch.OpeningWalletBalance:N2}) — ต่างกัน {diff:N2} บาท",
                "ตรวจยอดโอนเข้าและยอดยกมา/ยกไปให้ตรงสเตทเมนต์ของผู้ให้บริการ · ถ้าไฟล์ขาดรายการ เพิ่มบรรทัด \"ปรับปรุงอื่น\" "
                + "พร้อมเหตุผลและผังบัญชี (ระบบไม่เดาส่วนต่างให้ — JE ที่ไม่ตรงเงินจริงกระทบยอดไม่ได้ตลอดไป)",
                Array.Empty<Guid>(), diff));

        // ── 2. ด่านระดับช่องทาง/รอบโอน ──
        // R-A8 (review198-A): ต้องดูทั้งสกุลของรอบโอน**และ**ของช่องทาง — ช่องทาง USD ที่ adapter ไม่ได้ตั้งสกุลของรอบโอน (ค่าเริ่มต้น THB)
        // เคยผ่านด่าน แล้วลง Dr ธนาคาร 1,000 "บาท" จากเงิน 1,000 ดอลลาร์
        var batchCcy = (batch.Currency ?? "THB").Trim();
        var channelCcy = (channel.Currency ?? "THB").Trim();
        if (!string.Equals(batchCcy, "THB", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(channelCcy, "THB", StringComparison.OrdinalIgnoreCase))
            issues.Add(new SettlementPlanIssue(SettlementPlanIssueCode.CurrencyNotSupported, true,
                $"รอบโอนสกุล {batchCcy} · ช่องทางสกุล {channelCcy} — เฟส 1 รองรับเฉพาะเงินบาททั้งสองฝั่ง",
                "ลงบัญชีรอบนี้ด้วยมือ (สมุดรายวันทั่วไป) ไปก่อน — สกุลต่างประเทศ/อัตราแลกเปลี่ยนอยู่ในเฟส OTA",
                Array.Empty<Guid>(), null));
        if (channel.RevenueModel == SettlementRevenueModel.NetRate)
            issues.Add(new SettlementPlanIssue(SettlementPlanIssueCode.RevenueModelNotSupported, true,
                "ช่องทางนี้ตั้งเป็น \"รายได้สุทธิ (ขายต่อให้แพลตฟอร์ม)\" — เฟส 1 รองรับเฉพาะรายได้เต็มจำนวน + ค่าธรรมเนียมเป็นค่าใช้จ่าย",
                "ถ้าสัญญาเป็นแบบแพลตฟอร์มเก็บเงินแทน ให้เปลี่ยนเป็น \"รายได้เต็มจำนวน\" ในหน้าตั้งค่าช่องทาง · ถ้าเป็นแบบขายต่อจริง ลงบัญชีด้วยมือไปก่อน",
                Array.Empty<Guid>(), null));
        // ผู้ให้บริการต่างประเทศ + โหมดหัก — ม.70 ภ.ง.ด.54 ผ่านตัวตัดสินเดียว · บล็อกเฉพาะที่คิดให้ไม่ได้ (รอบ 200 ทีม W · เดิมบล็อกเหมา R-A5)
        issues.AddRange(SettlementForeignWht.PlanIssues(channel, lines, batch.PayoutDate));
        if (channel.ClearingAccountId is null)
            issues.Add(new SettlementPlanIssue(SettlementPlanIssueCode.ClearingAccountMissing, true,
                $"ช่องทาง \"{channel.DisplayName}\" ยังไม่ได้ผูกผังพัก (ลูกหนี้แพลตฟอร์ม)",
                "กด \"สร้างผังพักให้อัตโนมัติ\" (11341–11349) หรือเลือกผังพักเองในหน้าตั้งค่าช่องทาง",
                Array.Empty<Guid>(), null));
        if (batch.NetPayout != 0m && batch.BankAccountId is null)
            issues.Add(new SettlementPlanIssue(SettlementPlanIssueCode.BankAccountMissing, true,
                $"รอบโอน {reference} มีเงินเข้า {batch.NetPayout:N2} แต่ยังไม่ได้เลือกบัญชีธนาคารที่รับเงิน",
                "เลือกบัญชีธนาคารของรอบโอนนี้ (หรือจับคู่กับรายการเดินบัญชี)", Array.Empty<Guid>(), batch.NetPayout));
        if (batch.ClosingWalletBalance < 0m)
            issues.Add(new SettlementPlanIssue(SettlementPlanIssueCode.NegativeBalanceCarried, false,
                $"ยอด wallet ปลายรอบติดลบ {batch.ClosingWalletBalance:N2} — แพลตฟอร์มจะหักจากรอบถัดไป",
                "ไม่ต้องทำอะไรตอนนี้ · ผังพักจะมียอดด้านเครดิตจนรอบถัดไป — ถ้าปิดงวดบัญชีระหว่างนี้ ให้จัดประเภทเป็นเจ้าหนี้อื่น (21220) ในงบ",
                Array.Empty<Guid>(), batch.ClosingWalletBalance));

        // ── 3. ตรวจรายบรรทัด แล้วแยกตามทางลงบัญชี ──
        var sale = new List<SettlementLine>();
        var refunds = new List<SettlementLine>();
        var fees = new List<SettlementLine>();
        var direct = new List<SettlementLine>();
        decimal already = 0m;
        foreach (var l in lines)
        {
            if (l.Amount == 0m) continue;                          // แถว 0 ไม่ใช่หลักฐาน
            var rule = SettlementLineTypeRules.For(l.LineType);
            if (!rule.Postable)
            {
                issues.Add(new SettlementPlanIssue(SettlementPlanIssueCode.Unclassified, true,
                    $"บรรทัดที่ {l.Seq} ({Label(l)}) ยอด {l.Amount:N2} ยังไม่ได้จัดประเภท",
                    "เลือกประเภทรายการของบรรทัดนี้ (ระบบจะจำไว้ใช้กับไฟล์ถัดไป)", new[] { l.Id }, l.Amount));
                continue;
            }
            if (!rule.SignAllowed(l.Amount))
            {
                issues.Add(new SettlementPlanIssue(SettlementPlanIssueCode.SignNotAllowed, true,
                    $"บรรทัดที่ {l.Seq} ประเภท \"{rule.LabelTh}\" ยอด {l.Amount:N2} เครื่องหมายผิดประเภท",
                    "ตรวจประเภทที่เลือก (เช่น ยอดบวกที่เป็นเงินคืนค่าธรรมเนียม vs ยอดลบที่เป็นค่าธรรมเนียม) หรือแก้การจับคู่คอลัมน์ยอดเงิน",
                    new[] { l.Id }, l.Amount));
                continue;
            }
            if (rule.RequiresReason)
            {
                var bad = false;
                if (string.IsNullOrWhiteSpace(l.AdjustmentReason))
                {
                    issues.Add(new SettlementPlanIssue(SettlementPlanIssueCode.AdjustmentReasonMissing, true,
                        $"บรรทัดที่ {l.Seq} เป็นรายการปรับปรุง ยอด {l.Amount:N2} แต่ไม่มีเหตุผล",
                        "ระบุเหตุผลของรายการปรับปรุง (ผู้สอบบัญชีจะถาม)", new[] { l.Id }, l.Amount));
                    bad = true;
                }
                if (l.OverrideAccountId is null)
                {
                    issues.Add(new SettlementPlanIssue(SettlementPlanIssueCode.AdjustmentAccountMissing, true,
                        $"บรรทัดที่ {l.Seq} เป็นรายการปรับปรุง ยอด {l.Amount:N2} แต่ยังไม่ได้เลือกผังบัญชี",
                        "เลือกผังบัญชีที่รายการปรับปรุงนี้ควรลง", new[] { l.Id }, l.Amount));
                    bad = true;
                }
                if (bad) continue;
            }
            if (l.VatAmount is decimal ev && (Math.Abs(ev) > Math.Abs(l.Amount) || (ev != 0m && Math.Sign(ev) != Math.Sign(l.Amount))))
            {
                issues.Add(new SettlementPlanIssue(SettlementPlanIssueCode.ExplicitVatInvalid, true,
                    $"บรรทัดที่ {l.Seq} VAT ในไฟล์ ({ev:N2}) ไม่สอดคล้องกับยอด ({l.Amount:N2})",
                    "ตรวจการจับคู่คอลัมน์ VAT/ยอดเงิน (VAT ต้องรวมอยู่ในยอดและเครื่องหมายเดียวกัน)", new[] { l.Id }, l.Amount));
                continue;
            }

            switch (rule.Posting)
            {
                case SettlementPostingKind.SaleComponent:
                    if (l.PaymentIntentId is not null || l.PaymentId is not null) already += l.Amount;
                    else sale.Add(l);
                    break;
                case SettlementPostingKind.Refund:
                    if (l.PaymentIntentId is not null || l.PaymentId is not null) already += l.Amount;
                    else refunds.Add(l);
                    break;
                case SettlementPostingKind.FeeDocument:
                    fees.Add(l);
                    break;
                default:
                    direct.Add(l);
                    break;
            }
        }

        // ── 4. ขาขาย: จับคู่ได้ ⇒ รับชำระต่อใบ · ตัวจับคู่ยืนยันว่าไม่มีร่องรอย (AutoSummary) ⇒ ใบขายสรุปรายวัน ·
        //       ยอดไม่ตรง/กำกวม ⇒ บล็อกให้คนตัดสิน (สัญญาทีม B: เดิมทุกบรรทัดที่ไม่มีใบเข้าใบสรุป ⇒ ออเดอร์ที่มีใบเสร็จ/ผู้สมัครกำกวม = รายได้ซ้ำ) ──
        var mismatched = sale.Concat(refunds).Where(l => l.MatchStatus == SettlementMatchStatus.AmountMismatch).ToList();
        if (mismatched.Count > 0)
            issues.Add(new SettlementPlanIssue(SettlementPlanIssueCode.SaleAmountMismatch, true,
                $"บรรทัดขาย/คืนเงิน {mismatched.Count} บรรทัด ยอด {mismatched.Sum(l => l.Amount):N2} จับคู่ใบได้แต่ยอดไม่ตรงใบ",
                "เปิดบรรทัดเหล่านั้นแล้วยืนยันการจับคู่ (ยอดต่างเพราะอะไร) หรือเลือกใบที่ถูก — ระบบไม่รับชำระ/ออกใบสรุปให้จนกว่าจะยืนยัน",
                mismatched.Select(l => l.Id).ToList(), mismatched.Sum(l => l.Amount)));
        sale = sale.Where(l => l.MatchStatus != SettlementMatchStatus.AmountMismatch).ToList();
        refunds = refunds.Where(l => l.MatchStatus != SettlementMatchStatus.AmountMismatch).ToList();
        var unresolved = sale.Where(l => l.MatchedDocumentId is null && l.MatchStatus != SettlementMatchStatus.AutoSummary).ToList();
        if (unresolved.Count > 0)
            issues.Add(new SettlementPlanIssue(SettlementPlanIssueCode.SaleUnmatched, true,
                $"บรรทัดขาย {unresolved.Count} บรรทัด ยอด {unresolved.Sum(l => l.Amount):N2} ยังไม่ได้ข้อยุติว่าเป็นของใบขายไหน "
                + "(ผู้สมัครกำกวม หรือมีใบเสร็จ/การจองที่อาจเป็นออเดอร์เดียวกัน)",
                "เลือกใบขายให้บรรทัดเหล่านั้น หรือยืนยันว่า \"ไม่มีเอกสารขาย — ออกใบขายสรุปรายวัน\" (ห้ามให้ระบบเดา — ออกซ้ำ = รายได้และภาษีขายซ้ำ)",
                unresolved.Select(l => l.Id).ToList(), unresolved.Sum(l => l.Amount)));
        var receipts = new List<SettlementReceiptPlan>();
        foreach (var g in sale.Where(l => l.MatchedDocumentId is not null).GroupBy(l => l.MatchedDocumentId!.Value))
        {
            var amt = g.Sum(l => l.Amount);
            if (amt <= 0m)
            {
                issues.Add(new SettlementPlanIssue(SettlementPlanIssueCode.SaleGroupNotPositive, true,
                    $"บรรทัดขายที่จับคู่กับเอกสารเดียวกันรวมได้ {amt:N2} (ไม่เป็นบวก)",
                    "ตรวจการจับคู่ — ส่วนลดของออเดอร์ต้องจับคู่กับใบขายเดียวกับยอดขายของออเดอร์นั้น",
                    g.Select(l => l.Id).ToList(), amt));
                continue;
            }
            receipts.Add(new SettlementReceiptPlan(g.Key, amt, g.Select(l => l.Id).ToList()));
        }
        var summaries = new List<SettlementSummarySalePlan>();
        foreach (var g in sale.Where(l => l.MatchedDocumentId is null && l.MatchStatus == SettlementMatchStatus.AutoSummary)
                     // R-A6 (review198-A): วันตามปฏิทินไทย ไม่ใช่ .Date ของ UTC — ขายตี 1 วันที่ 1 ต.ค. (= 30 ก.ย. 18:00Z) ต้องอยู่ ต.ค.
                     // (เดือนภาษี ภ.พ.30) · ค่าที่ adapter เก็บเป็น "วันไทย 00:00 UTC" อยู่แล้วได้วันเดิม
                     .GroupBy(l => ThaiDate.CalendarDateUtc(l.TxnDate ?? batch.PayoutDate)).OrderBy(g => g.Key))
        {
            var gross = g.Sum(l => l.Amount);
            if (gross <= 0m)
            {
                issues.Add(new SettlementPlanIssue(SettlementPlanIssueCode.SaleGroupNotPositive, true,
                    $"บรรทัดขายที่จับคู่ไม่ได้ของวันที่ {g.Key:yyyy-MM-dd} รวมได้ {gross:N2} (ไม่เป็นบวก)",
                    "จับคู่ส่วนลดกับใบขายของออเดอร์นั้น หรือตรวจวันที่ของรายการ", g.Select(l => l.Id).ToList(), gross));
                continue;
            }
            var (net, vat) = SettlementFeeTax.SplitInclusive(gross, companyVatRegistered);
            summaries.Add(new SettlementSummarySalePlan(g.Key, gross, net, vat,
                g.Where(l => l.LineType == SettlementLineType.Sale).Sum(l => l.Amount),
                g.Where(l => l.LineType == SettlementLineType.SellerVoucher).Sum(l => l.Amount),
                g.Where(l => l.LineType == SettlementLineType.PlatformVoucherSubsidy).Sum(l => l.Amount),
                g.Select(l => l.Id).ToList(),
                g.Select(l => l.ExternalOrderId).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!).Distinct().ToList()));
        }
        if (summaries.Count > 0)
            issues.Add(new SettlementPlanIssue(SettlementPlanIssueCode.SummarySaleCreated, false,
                $"บรรทัดขาย {summaries.Sum(s => s.LineIds.Count)} บรรทัด ยอด {summaries.Sum(s => s.Gross):N2} จับคู่ใบขายในระบบไม่ได้ "
                + $"— ระบบจะออกใบขายสรุปรายวัน {summaries.Count} ใบให้ (ติดป้ายให้ตรวจ)",
                "ตรวจว่ายอดขายเหล่านี้ยังไม่เคยออกเอกสารขายทางอื่น (กันรายได้ซ้ำ) — ถ้าเคยออกแล้ว จับคู่บรรทัดกับใบนั้นก่อนลงบัญชี",
                summaries.SelectMany(s => s.LineIds).ToList(), summaries.Sum(s => s.Gross)));

        // ── 5. คืนเงิน: ต้องอ้างใบเดิม ──
        var refundPlans = new List<SettlementRefundPlan>();
        foreach (var l in refunds.Where(l => l.MatchedDocumentId is null))
            issues.Add(new SettlementPlanIssue(SettlementPlanIssueCode.RefundUnmatched, true,
                $"บรรทัดที่ {l.Seq} คืนเงิน {-l.Amount:N2} (ออเดอร์ {l.ExternalOrderId ?? "-"}) ยังไม่รู้ใบขายเดิม",
                "เลือกใบขายเดิมของออเดอร์นี้ — ใบลดหนี้ต้องอ้างเลขที่และวันที่ใบเดิม (§86/10)", new[] { l.Id }, l.Amount));
        foreach (var g in refunds.Where(l => l.MatchedDocumentId is not null).GroupBy(l => l.MatchedDocumentId!.Value))
            refundPlans.Add(new SettlementRefundPlan(g.Key, -g.Sum(l => l.Amount), g.Select(l => l.Id).ToList()));

        // ── 6. ค่าธรรมเนียม → เอกสารซื้อต่อกลุ่มภาษี ──
        var feeDocs = new List<SettlementFeeDocumentPlan>();
        var whtJournal = new List<SettlementJournalLinePlan>();
        var feeLines = BuildFeeLines(fees, channel, feeMap, companyVatRegistered, batch.PayoutDate);
        foreach (var g in feeLines.GroupBy(f => f.VatTreatment).OrderBy(g => g.Key))
        {
            var docLines = g.Select(f => f.Line).ToList();
            // R-A3 (review198-A): ยอดคืนต้องตัดสิน**รายบรรทัดของใบ** (ประเภท+ผัง) ไม่ใช่รวมทั้งกลุ่มภาษี — ค่าคอม −1,070 + คืนค่าโฆษณา +535
            // เคยผ่าน (กลุ่มสุทธิ 535) แล้วได้บรรทัดติดลบบนเอกสารซื้อ + 50 ทวิ 20.73 แต่ 21917 30.93 (ขา WHT นับเฉพาะบรรทัดบวก)
            var refundLines = docLines.Where(x => x.Deducted < 0m || x.WhtAmount < 0m).ToList();
            if (refundLines.Count > 0)
            {
                foreach (var x in refundLines)
                    issues.Add(new SettlementPlanIssue(SettlementPlanIssueCode.FeeGroupNetRefund, true,
                        $"{x.LabelTh} รอบนี้สุทธิเป็นยอดคืน {-x.Deducted:N2} (แพลตฟอร์มคืนค่าธรรมเนียมมากกว่าที่เก็บ)",
                        "บันทึกใบลดหนี้ค่าธรรมเนียมที่ได้รับจากแพลตฟอร์ม (อ้างใบค่าธรรมเนียมเดิม) แล้วเปลี่ยนประเภทบรรทัดคืนเงินนั้นเป็น "
                        + "\"ปรับปรุงอื่น\" ที่ชี้ผังของใบลดหนี้ — ระบบยังไม่สร้างใบลดหนี้ค่าธรรมเนียมให้อัตโนมัติ",
                        x.LineIds, x.Deducted));
                continue;
            }
            var deducted = docLines.Sum(x => x.Deducted);
            if (deducted <= 0m)
            {
                if (deducted < 0m)
                    issues.Add(new SettlementPlanIssue(SettlementPlanIssueCode.FeeGroupNetRefund, true,
                        $"ค่าธรรมเนียมรอบนี้ (กลุ่ม {g.Key}) สุทธิเป็นยอดคืน {-deducted:N2}",
                        "บันทึกใบลดหนี้ค่าธรรมเนียมที่ได้รับจากแพลตฟอร์ม (อ้างใบค่าธรรมเนียมเดิม) — ระบบยังไม่สร้างให้อัตโนมัติ",
                        docLines.SelectMany(x => x.LineIds).ToList(), deducted));
                continue;
            }
            // ยอด WHT ของใบ = ชุดบรรทัดเดียวกับที่ AddWhtLegs ลง 21917 (บรรทัดบวก) — 50 ทวิ กับ JE ต้องเท่ากันเสมอ (R-A3)
            var whtMode = docLines.Any(x => x.WhtAmount > 0m) ? channel.FeeWhtMode : SettlementFeeWhtMode.None;
            feeDocs.Add(new SettlementFeeDocumentPlan(g.Key, whtMode, deducted,
                docLines.Sum(x => x.Expense), docLines.Sum(x => x.InputVat), docLines.Sum(x => x.Pp36Payable),
                docLines.Where(x => x.WhtAmount > 0m).Sum(x => x.WhtAmount), docLines,
                SettlementForeignWht.WhtForm(channel.FeeVatMode)));
            AddWhtLegs(whtJournal, whtMode, docLines, reference, SettlementForeignWht.IsForeignChannel(channel.FeeVatMode));
        }
        if (feeDocs.Any(d => d.WhtMode == SettlementFeeWhtMode.AgentWithholds))
            issues.Add(new SettlementPlanIssue(SettlementPlanIssueCode.WhtFiledByAgent, false,
                $"แพลตฟอร์มเป็นตัวแทนหัก ณ ที่จ่าย {feeDocs.Sum(d => d.WhtAmount):N2} แทนเรา",
                "เก็บหนังสือรับรอง 50 ทวิ ที่แพลตฟอร์มออก — ยอดนี้ไม่เข้ายอดที่เรายื่น ภ.ง.ด.53 เอง",
                Array.Empty<Guid>(), feeDocs.Sum(d => d.WhtAmount)));

        // ── 7. JE รอบโอน: ธนาคาร + รายการตรง + ขา WHT ──
        var journal = new List<SettlementJournalLinePlan>();
        if (batch.NetPayout > 0m)
        {
            journal.Add(new SettlementJournalLinePlan(SettlementAccountRoles.Bank, null, null, batch.NetPayout, 0m,
                $"รับโอนจาก {channel.DisplayName} รอบ {reference}", Array.Empty<Guid>()));
            journal.Add(ClearingLine(channel, 0m, batch.NetPayout, $"ถอนเงินจาก wallet {channel.DisplayName} รอบ {reference}", Array.Empty<Guid>()));
        }
        else if (batch.NetPayout < 0m)
        {
            journal.Add(ClearingLine(channel, -batch.NetPayout, 0m, $"เติมเงินเข้า wallet {channel.DisplayName} รอบ {reference}", Array.Empty<Guid>()));
            journal.Add(new SettlementJournalLinePlan(SettlementAccountRoles.Bank, null, null, 0m, -batch.NetPayout,
                $"โอนเติมเงินให้ {channel.DisplayName} รอบ {reference}", Array.Empty<Guid>()));
        }
        foreach (var g in direct.GroupBy(l => (Role: DirectRole(l), l.OverrideAccountId, Positive: l.Amount > 0m)))
        {
            var amt = Math.Abs(g.Sum(l => l.Amount));
            if (amt == 0m) continue;
            var ids = g.Select(l => l.Id).ToList();
            var label = SettlementLineTypeRules.For(g.First().LineType).LabelTh;
            var desc = $"{label} — {channel.DisplayName} รอบ {reference}";
            var counterId = g.Key.OverrideAccountId ?? RoleAccountId(g.Key.Role, channel, feeMap);
            var counterCode = SettlementAccountRoles.DefaultCode(g.Key.Role);
            if (g.Key.Positive)
            {
                journal.Add(ClearingLine(channel, amt, 0m, desc, ids));
                journal.Add(new SettlementJournalLinePlan(g.Key.Role, counterId, counterCode, 0m, amt, desc, ids));
            }
            else
            {
                journal.Add(new SettlementJournalLinePlan(g.Key.Role, counterId, counterCode, amt, 0m, desc, ids));
                journal.Add(ClearingLine(channel, 0m, amt, desc, ids));
            }
        }
        journal.AddRange(whtJournal);

        var canPost = !issues.Any(i => i.Blocking);
        return new SettlementPostingPlan(canPost, linesTotal, expected, diff, batch.NetPayout, already,
            channel.ClearingAccountId, batch.BankAccountId, issues, journal, feeDocs, receipts, summaries, refundPlans);
    }

    /// <summary>ผลของ chargeback ที่เปิดไว้ (พักที่ผัง dispute) — <b>แพ้</b>: Dr 57140 ขาดทุนจาก chargeback / Cr dispute
    /// (ภาษีขายเดิมคงอยู่ — ไม่ใช่เหตุออกใบลดหนี้ §86/10 · ถ้าแพ้แล้ว<b>ได้ของคืน</b> = คืนเงินปกติ ใช้ใบลดหนี้ ไม่ใช่เมธอดนี้) ·
    /// <b>ชนะ</b>และแพลตฟอร์มคืนเงินนอกไฟล์ settlement: Dr บัญชีพัก / Cr dispute (ถ้าคืนในไฟล์ ใช้บรรทัด ChargebackReversal แทน — ห้ามใช้ทั้งสองทาง)</summary>
    public static IReadOnlyList<SettlementJournalLinePlan> PlanChargebackResolution(
        decimal amount, bool won, SettlementChannel channel, string reference)
    {
        var amt = Math.Abs(amount);
        if (amt == 0m) return Array.Empty<SettlementJournalLinePlan>();
        var feeMap = SettlementLineTypeRules.ParseFeeAccountMap(channel.FeeAccountMapJson).Map;
        var dispute = new SettlementJournalLinePlan(SettlementAccountRoles.Dispute, channel.DisputeAccountId,
            SettlementAccountRoles.DefaultCode(SettlementAccountRoles.Dispute), 0m, amt,
            $"ปิดรายการ chargeback {reference} — {channel.DisplayName}", Array.Empty<Guid>());
        if (won)
            return new[]
            {
                ClearingLine(channel, amt, 0m, $"ชนะ chargeback {reference} ได้เงินคืน — {channel.DisplayName}", Array.Empty<Guid>()),
                dispute,
            };
        return new[]
        {
            new SettlementJournalLinePlan(SettlementAccountRoles.ChargebackLoss,
                RoleAccountId(SettlementAccountRoles.ChargebackLoss, channel, feeMap),
                SettlementAccountRoles.DefaultCode(SettlementAccountRoles.ChargebackLoss), amt, 0m,
                $"แพ้ chargeback {reference} (ไม่ได้สินค้าคืน) — {channel.DisplayName}", Array.Empty<Guid>()),
            dispute,
        };
    }

    // ───────────────────────── ภายใน ─────────────────────────

    private readonly record struct FeeLineWithTreatment(SettlementFeeVatTreatment VatTreatment, SettlementFeeDocumentLine Line);

    /// <summary>รวมค่าธรรมเนียมตาม (ประเภท · ผัง) — บรรทัดที่ไฟล์ระบุ VAT คิดทีละบรรทัด (เชื่อไฟล์) · ที่เหลือรวมยอดแล้วแยก VAT ครั้งเดียว
    /// (ใกล้ใบกำกับรายเดือนของแพลตฟอร์มกว่าการปัดทีละบรรทัด) · ยอดคืน (บวก) หักออกจากก้อนเดียวกัน</summary>
    private static List<FeeLineWithTreatment> BuildFeeLines(
        List<SettlementLine> fees, SettlementChannel channel, IReadOnlyDictionary<string, Guid> feeMap, bool vatRegistered,
        DateTime paymentDate)
    {
        var result = new List<FeeLineWithTreatment>();
        foreach (var g in fees.GroupBy(l => (l.LineType, l.OverrideAccountId)).OrderBy(g => (int)g.Key.LineType))
        {
            var rule = SettlementLineTypeRules.For(g.Key.LineType);
            var role = rule.AccountRole ?? SettlementAccountRoles.ServiceFee;
            var accountId = g.Key.OverrideAccountId ?? RoleAccountId(role, channel, feeMap);
            var parts = new List<(SettlementFeeTaxResult Tax, int Sign)>();
            foreach (var l in g.Where(l => l.VatAmount is not null))
                parts.Add((ComputeTax(Math.Abs(l.Amount), l.VatAmount, rule, channel, vatRegistered, paymentDate), l.Amount < 0m ? 1 : -1));
            var implicitCharge = -g.Where(l => l.VatAmount is null).Sum(l => l.Amount);   // ค่าธรรมเนียม = ยอดลบ ⇒ กลับเป็นบวก
            if (implicitCharge != 0m)
                parts.Add((ComputeTax(Math.Abs(implicitCharge), null, rule, channel, vatRegistered, paymentDate), implicitCharge > 0m ? 1 : -1));

            // กลุ่มภาษีของก้อน = ชนิดที่มี VAT ตัวแรก (ประเภท+ผังเดียวกันใต้ช่องทางเดียวกันได้ชนิดเดียวอยู่แล้ว) · ไม่มี VAT ทั้งก้อน ⇒ NoVat
            var treatment = parts.Select(p => p.Tax.VatTreatment).FirstOrDefault(t => t != SettlementFeeVatTreatment.NoVat);
            result.Add(new FeeLineWithTreatment(treatment, new SettlementFeeDocumentLine(
                g.Key.LineType, rule.LabelTh, role, accountId, SettlementAccountRoles.DefaultCode(role),
                parts.Sum(p => p.Sign * p.Tax.Deducted),
                parts.Sum(p => p.Sign * p.Tax.Expense),
                parts.Sum(p => p.Sign * p.Tax.InputVat),
                parts.Sum(p => p.Sign * p.Tax.Pp36Payable),
                parts.Select(p => p.Tax.WhtIncomeCode).FirstOrDefault(c => c != null),
                parts.Select(p => p.Tax.WhtRatePercent).FirstOrDefault(r => r != 0m),
                parts.Sum(p => p.Sign * p.Tax.WhtBase),
                parts.Sum(p => p.Sign * p.Tax.WhtAmount),
                parts.Sum(p => p.Sign * p.Tax.WhtCertIncome),
                parts.Sum(p => p.Sign * p.Tax.WhtBorneExpense),
                g.Select(l => l.Id).ToList())));
        }
        return result;
    }

    private static SettlementFeeTaxResult ComputeTax(decimal deducted, decimal? explicitVat, SettlementLineTypeRule rule,
        SettlementChannel channel, bool vatRegistered, DateTime paymentDate)
        => SettlementFeeTax.Compute(deducted, explicitVat, channel.FeeVatMode, rule.VatApplicable, vatRegistered,
            channel.FeeWhtMode, rule.WhtIncomeCode, paymentDate);

    /// <summary>ขา WHT ของใบค่าธรรมเนียม (ไม่ผ่านการจ่ายเงินของใบ — แพลตฟอร์มหักค่าธรรมเนียมเต็มไปแล้ว):
    /// W2 Dr ลูกหนี้แพลตฟอร์มรอคืน / Cr 21917 · W3 Dr ผังค่าธรรมเนียม (ภาษีที่ออกแทน) / Cr 21917 · W1 ไม่มีขา (ตัวแทนยื่นเอง) ·
    /// ผู้ให้บริการต่างประเทศ ⇒ Cr 21918 ภ.ง.ด.54 (ม.70 · บทบาท <see cref="SettlementAccountRoles.WhtPayable54"/>)</summary>
    private static void AddWhtLegs(List<SettlementJournalLinePlan> journal, SettlementFeeWhtMode mode,
        List<SettlementFeeDocumentLine> docLines, string reference, bool foreign)
    {
        if (mode is SettlementFeeWhtMode.None or SettlementFeeWhtMode.AgentWithholds) return;
        foreach (var d in docLines.Where(d => d.WhtAmount > 0m))
        {
            var ids = d.LineIds;
            var desc = $"ภาษีหัก ณ ที่จ่าย {d.WhtRatePercent:0.##}% {d.LabelTh} รอบ {reference} "
                + (foreign ? $"(ภ.ง.ด.54 · {ForeignWhtRateResolver.Section70Reference})" : "(ภ.ง.ด.53)");
            var payableRole = foreign ? SettlementAccountRoles.WhtPayable54 : SettlementAccountRoles.WhtPayable;
            if (mode == SettlementFeeWhtMode.SelfWithholdReimbursed)
                journal.Add(new SettlementJournalLinePlan(SettlementAccountRoles.WhtReimbursable, null,
                    SettlementAccountRoles.DefaultCode(SettlementAccountRoles.WhtReimbursable), d.WhtAmount, 0m,
                    desc + " — รอแพลตฟอร์มคืน", ids));
            else
                journal.Add(new SettlementJournalLinePlan(d.AccountRole, d.AccountId, d.DefaultAccountCode, d.WhtBorneExpense, 0m,
                    desc + " — ภาษีที่ออกแทน", ids));
            journal.Add(new SettlementJournalLinePlan(payableRole, null,
                SettlementAccountRoles.DefaultCode(payableRole), 0m, d.WhtAmount, desc, ids));
        }
    }

    /// <summary>บทบาทคู่บัญชีของบรรทัดที่ลง JE ตรง — FX แยกกำไร/ขาดทุนตามเครื่องหมาย</summary>
    private static string DirectRole(SettlementLine l)
        => l.LineType == SettlementLineType.FxDifference
            ? (l.Amount > 0m ? SettlementAccountRoles.FxGain : SettlementAccountRoles.FxLoss)
            : SettlementLineTypeRules.For(l.LineType).AccountRole ?? SettlementAccountRoles.Adjustment;

    private static Guid? RoleAccountId(string role, SettlementChannel channel, IReadOnlyDictionary<string, Guid> feeMap)
    {
        if (role == SettlementAccountRoles.Clearing) return channel.ClearingAccountId;
        if (role == SettlementAccountRoles.Reserve) return channel.ReserveAccountId;
        if (role == SettlementAccountRoles.Dispute) return channel.DisputeAccountId;
        return feeMap.TryGetValue(role, out var id) ? id : null;
    }

    private static SettlementJournalLinePlan ClearingLine(SettlementChannel channel, decimal dr, decimal cr, string desc, IReadOnlyList<Guid> ids)
        => new(SettlementAccountRoles.Clearing, channel.ClearingAccountId, null, dr, cr, desc, ids);

    private static string Label(SettlementLine l)
        => !string.IsNullOrWhiteSpace(l.RawTypeLabel) ? l.RawTypeLabel!.Trim()
            : !string.IsNullOrWhiteSpace(l.Description) ? l.Description!.Trim() : "ไม่มีคำอธิบาย";
}
