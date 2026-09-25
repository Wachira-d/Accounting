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

    // ── แจ้งให้ทราบ (ไม่บล็อก) ──
    /// <summary>ยอด wallet ปลายรอบติดลบ — ยกไปหักรอบถัดไป (report-S1 G7)</summary>
    NegativeBalanceCarried = 50,
    /// <summary>มีบรรทัดขายที่จับคู่ใบขายไม่ได้ ⇒ สร้างใบขายสรุปรายวันให้ + ติดป้ายตรวจ (DECISIONS ข้อ 3)</summary>
    SummarySaleCreated = 51,
    /// <summary>แพลตฟอร์มเป็นตัวแทนหัก ณ ที่จ่าย — เก็บ 50 ทวิ แต่ห้ามนับเข้ายอดที่เรายื่นเอง</summary>
    WhtFiledByAgent = 52,
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
public sealed record SettlementFeeDocumentPlan(
    SettlementFeeVatTreatment VatTreatment,
    SettlementFeeWhtMode WhtMode,
    decimal Deducted,
    decimal Expense,
    decimal InputVat,
    decimal Pp36Payable,
    decimal WhtAmount,
    IReadOnlyList<SettlementFeeDocumentLine> Lines);

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
        if (Math.Abs(diff) > ToleranceBaht)
            issues.Add(new SettlementPlanIssue(SettlementPlanIssueCode.Unbalanced, true,
                $"ยอดรวมบรรทัด ({linesTotal:N2}) ไม่เท่ากับยอดโอนเข้า ({batch.NetPayout:N2}) + ยอด wallet ที่เปลี่ยน "
                + $"({batch.ClosingWalletBalance - batch.OpeningWalletBalance:N2}) — ต่างกัน {diff:N2} บาท",
                "ตรวจยอดโอนเข้าและยอดยกมา/ยกไปให้ตรงสเตทเมนต์ของผู้ให้บริการ · ถ้าไฟล์ขาดรายการ เพิ่มบรรทัด \"ปรับปรุงอื่น\" "
                + "พร้อมเหตุผลและผังบัญชี (ระบบไม่เดาส่วนต่างให้ — JE ที่ไม่ตรงเงินจริงกระทบยอดไม่ได้ตลอดไป)",
                Array.Empty<Guid>(), diff));

        // ── 2. ด่านระดับช่องทาง/รอบโอน ──
        if (!string.Equals((batch.Currency ?? "THB").Trim(), "THB", StringComparison.OrdinalIgnoreCase))
            issues.Add(new SettlementPlanIssue(SettlementPlanIssueCode.CurrencyNotSupported, true,
                $"รอบโอนสกุล {batch.Currency} — เฟส 1 รองรับเฉพาะเงินบาท",
                "ลงบัญชีรอบนี้ด้วยมือ (สมุดรายวันทั่วไป) ไปก่อน — สกุลต่างประเทศ/อัตราแลกเปลี่ยนอยู่ในเฟส OTA",
                Array.Empty<Guid>(), null));
        if (channel.RevenueModel == SettlementRevenueModel.NetRate)
            issues.Add(new SettlementPlanIssue(SettlementPlanIssueCode.RevenueModelNotSupported, true,
                "ช่องทางนี้ตั้งเป็น \"รายได้สุทธิ (ขายต่อให้แพลตฟอร์ม)\" — เฟส 1 รองรับเฉพาะรายได้เต็มจำนวน + ค่าธรรมเนียมเป็นค่าใช้จ่าย",
                "ถ้าสัญญาเป็นแบบแพลตฟอร์มเก็บเงินแทน ให้เปลี่ยนเป็น \"รายได้เต็มจำนวน\" ในหน้าตั้งค่าช่องทาง · ถ้าเป็นแบบขายต่อจริง ลงบัญชีด้วยมือไปก่อน",
                Array.Empty<Guid>(), null));
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

        // ── 4. ขาขาย: จับคู่ได้ ⇒ รับชำระต่อใบ · จับไม่ได้ ⇒ ใบขายสรุปรายวัน ──
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
        foreach (var g in sale.Where(l => l.MatchedDocumentId is null)
                     .GroupBy(l => (l.TxnDate ?? batch.PayoutDate).Date).OrderBy(g => g.Key))
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
        var feeLines = BuildFeeLines(fees, channel, feeMap, companyVatRegistered);
        foreach (var g in feeLines.GroupBy(f => f.VatTreatment).OrderBy(g => g.Key))
        {
            var docLines = g.Select(f => f.Line).ToList();
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
            var whtMode = docLines.Any(x => x.WhtAmount != 0m) ? channel.FeeWhtMode : SettlementFeeWhtMode.None;
            feeDocs.Add(new SettlementFeeDocumentPlan(g.Key, whtMode, deducted,
                docLines.Sum(x => x.Expense), docLines.Sum(x => x.InputVat), docLines.Sum(x => x.Pp36Payable),
                docLines.Sum(x => x.WhtAmount), docLines));
            AddWhtLegs(whtJournal, whtMode, docLines, reference);
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
        List<SettlementLine> fees, SettlementChannel channel, IReadOnlyDictionary<string, Guid> feeMap, bool vatRegistered)
    {
        var result = new List<FeeLineWithTreatment>();
        foreach (var g in fees.GroupBy(l => (l.LineType, l.OverrideAccountId)).OrderBy(g => (int)g.Key.LineType))
        {
            var rule = SettlementLineTypeRules.For(g.Key.LineType);
            var role = rule.AccountRole ?? SettlementAccountRoles.ServiceFee;
            var accountId = g.Key.OverrideAccountId ?? RoleAccountId(role, channel, feeMap);
            var parts = new List<(SettlementFeeTaxResult Tax, int Sign)>();
            foreach (var l in g.Where(l => l.VatAmount is not null))
                parts.Add((ComputeTax(Math.Abs(l.Amount), l.VatAmount, rule, channel, vatRegistered), l.Amount < 0m ? 1 : -1));
            var implicitCharge = -g.Where(l => l.VatAmount is null).Sum(l => l.Amount);   // ค่าธรรมเนียม = ยอดลบ ⇒ กลับเป็นบวก
            if (implicitCharge != 0m)
                parts.Add((ComputeTax(Math.Abs(implicitCharge), null, rule, channel, vatRegistered), implicitCharge > 0m ? 1 : -1));

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
        SettlementChannel channel, bool vatRegistered)
        => SettlementFeeTax.Compute(deducted, explicitVat, channel.FeeVatMode, rule.VatApplicable, vatRegistered,
            channel.FeeWhtMode, rule.WhtIncomeCode);

    /// <summary>ขา WHT ของใบค่าธรรมเนียม (ไม่ผ่านการจ่ายเงินของใบ — แพลตฟอร์มหักค่าธรรมเนียมเต็มไปแล้ว):
    /// W2 Dr ลูกหนี้แพลตฟอร์มรอคืน / Cr 21917 · W3 Dr ผังค่าธรรมเนียม (ภาษีที่ออกแทน) / Cr 21917 · W1 ไม่มีขา (ตัวแทนยื่นเอง)</summary>
    private static void AddWhtLegs(List<SettlementJournalLinePlan> journal, SettlementFeeWhtMode mode,
        List<SettlementFeeDocumentLine> docLines, string reference)
    {
        if (mode is SettlementFeeWhtMode.None or SettlementFeeWhtMode.AgentWithholds) return;
        foreach (var d in docLines.Where(d => d.WhtAmount > 0m))
        {
            var ids = d.LineIds;
            var desc = $"ภาษีหัก ณ ที่จ่าย {d.WhtRatePercent:0.##}% {d.LabelTh} รอบ {reference} (ภ.ง.ด.53)";
            if (mode == SettlementFeeWhtMode.SelfWithholdReimbursed)
                journal.Add(new SettlementJournalLinePlan(SettlementAccountRoles.WhtReimbursable, null,
                    SettlementAccountRoles.DefaultCode(SettlementAccountRoles.WhtReimbursable), d.WhtAmount, 0m,
                    desc + " — รอแพลตฟอร์มคืน", ids));
            else
                journal.Add(new SettlementJournalLinePlan(d.AccountRole, d.AccountId, d.DefaultAccountCode, d.WhtBorneExpense, 0m,
                    desc + " — ภาษีที่ออกแทน", ids));
            journal.Add(new SettlementJournalLinePlan(SettlementAccountRoles.WhtPayable, null,
                SettlementAccountRoles.DefaultCode(SettlementAccountRoles.WhtPayable), 0m, d.WhtAmount, desc, ids));
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
