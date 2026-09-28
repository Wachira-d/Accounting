using System.Globalization;
using Accounting.Models.DTOs.Document;
using Accounting.Models.DTOs.Tax;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

// ═══════════════════════════════════════════════════════════════════════
// Settlement — ชั้น "ลงบัญชีตามแผน" (รอบ 198 เฟส 1 ทีม C) · ตัวตัดสินบริสุทธิ์ทั้งหมดของ
// Services/Settlement/SettlementPostingService — service แค่ "หาข้อเท็จจริงจากฐาน แล้วทำตามที่นี่บอก"
// (เรพนี้ไม่มีเทสต์ที่มี DbContext ⇒ ตรรกะที่ตัดสินเงิน/ภาษีต้องอยู่ที่นี่ + เทสต์ ·
//  จุดเรียกใน service ล็อกด้วย tools/required_call_site_check.py)
// แผนเอง (ยอด/ภาษี/ขา JE) มาจาก SettlementBatchMath.Plan ตัวเดียว — ที่นี่ห้ามคิดยอดใหม่
// ═══════════════════════════════════════════════════════════════════════

/// <summary>ตัวบ่งชี้ที่ผูก "ของที่การลงบัญชีรอบโอนสร้าง" กลับมาหา batch — ทำให้การลงบัญชี<b>ทำต่อจากขั้นที่ค้างได้โดยไม่สร้างซ้ำ</b>
/// (เส้นสร้าง/อนุมัติเอกสารของ <c>DocumentService</c> เปิดธุรกรรมของตัวเอง ⇒ ทั้งรอบอยู่ในธุรกรรมเดียวไม่ได้ · ดูหัว service)</summary>
public static class SettlementPostingKeys
{
    /// <summary>scope ของ advisory lock (ต่อช่องทาง) — คีย์จาก <see cref="AdvisoryLockKey.For(Guid, string, string)"/> ผ่าน <see cref="JobLock"/></summary>
    public const string LockScope = "settle-post";

    /// <summary>ต้นของ <c>Document.CreatedBy</c> ของเอกสารที่รอบโอนสร้าง — ผู้สร้าง = ระบบ (ผู้อนุมัติ = คนกดลงบัญชี · SoD เทียบได้ตามจริง)</summary>
    public static string CreatorPrefix(Guid batchId) => "system:settlement:" + batchId.ToString("N") + ":";

    /// <summary><c>Document.CreatedBy</c> ของเอกสาร 1 ชิ้นของรอบโอน — บันทึกพร้อมเอกสารใน<b>คำสั่งเดียว</b> (ไม่มีช่วงที่เอกสารเกิดแล้วแต่ยังไม่มีป้าย)</summary>
    public static string Creator(Guid batchId, string component) => CreatorPrefix(batchId) + component;

    /// <summary>ชิ้นใบค่าธรรมเนียม 1 ใบต่อกลุ่มภาษี</summary>
    public static string FeeComponent(SettlementFeeVatTreatment treatment) => "fee-" + treatment;

    /// <summary>ชิ้นใบขายสรุปรายวัน (วันตามปฏิทินไทย)</summary>
    public static string SummaryComponent(DateTime day) => "sum-" + day.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    /// <summary>ป้ายใน <c>Payment.Notes</c> ของการรับชำระที่รอบโอนบันทึก (บันทึกพร้อมการรับชำระในคำสั่งเดียว)</summary>
    public static string PaymentMarker(Guid batchId) => "[SETTLEMENT:" + batchId.ToString("N") + "]";

    /// <summary><c>JournalEntry.Reference</c> ของ JE ปิดรายการ chargeback ต่อบรรทัด — กันปิดซ้ำ</summary>
    public static string ChargebackReference(Guid lineId) => "STL-CB-" + lineId.ToString("N");
}

/// <summary>ผังบัญชี 1 แถว<b>ของบริษัทที่กำลังลงบัญชี</b> (ผู้เรียกโหลดด้วย <c>CompanyId == companyId</c>)</summary>
/// <param name="Name">ชื่อผัง — ใช้แสดงในพรีวิว (review198-D D-02) · ไม่มีผลต่อการหาผัง</param>
public sealed record SettlementChartAccount(Guid Id, string Code, bool IsActive, string? Name = null);

/// <summary>ดัชนีผังของบริษัทเดียว — id ที่ไม่อยู่ในดัชนี = <b>ไม่ใช่ผังของบริษัทนี้</b> (หรือถูกลบ) ⇒ ตัวหาผังปฏิเสธ ไม่ตกผังอื่นเงียบ</summary>
public sealed class SettlementChartIndex
{
    private readonly Dictionary<Guid, SettlementChartAccount> _byId = new();
    private readonly Dictionary<string, SettlementChartAccount> _byCode = new(StringComparer.Ordinal);

    public SettlementChartIndex(IEnumerable<SettlementChartAccount> accounts)
    {
        foreach (var a in accounts)
        {
            _byId[a.Id] = a;
            var code = (a.Code ?? "").Trim();
            if (code.Length == 0) continue;
            // รหัสเดียวกันหลายแถว (ข้อมูลเก่า) — แถวที่เปิดใช้ชนะ
            if (!_byCode.TryGetValue(code, out var cur) || (!cur.IsActive && a.IsActive))
                _byCode[code] = a;
        }
    }

    public SettlementChartAccount? ById(Guid id) => _byId.TryGetValue(id, out var a) ? a : null;
    public SettlementChartAccount? ByCode(string code) => _byCode.TryGetValue(code.Trim(), out var a) ? a : null;
}

/// <summary>ขา JE ที่หาผังแล้ว — พร้อมส่งเข้า <c>JournalEntryBuilder</c></summary>
public sealed record SettlementResolvedJournalLine(Guid AccountId, decimal Debit, decimal Credit, string Description);

/// <summary>ผลหาผังของแผนทั้งรอบ</summary>
/// <param name="FeeLineAccounts">ผังรายบรรทัดของใบค่าธรรมเนียม — ขนานกับ <c>plan.FeeDocuments[i].Lines[j]</c></param>
/// <param name="ClearingAccountId">ผังพักของช่องทาง (ตรวจแล้วว่าเป็นของบริษัทนี้และเปิดใช้) — ขาเงินของใบค่าธรรมเนียม/ใบขายสรุป/รับชำระ</param>
/// <param name="Described">ผังที่จะลงจริงของทุกขา (รหัส+ชื่อ) สำหรับพรีวิว — คิดด้วยตัวหาผังตัวเดียวกับ <see cref="Journal"/> (D-02)</param>
public sealed record SettlementAccountResolution(
    IReadOnlyList<SettlementResolvedJournalLine> Journal,
    IReadOnlyList<IReadOnlyList<Guid>> FeeLineAccounts,
    Guid? ClearingAccountId,
    IReadOnlyList<string> Errors,
    SettlementPlanAccounts? Described = null)
{
    public bool Ok => Errors.Count == 0;
}

/// <summary>ผังที่ขาหนึ่งของแผน<b>จะลงจริง</b> (review198-D D-02) — หน้าพรีวิวแสดงค่านี้ ไม่ใช่ "ผังมาตรฐานของบทบาท"</summary>
/// <param name="AccountId">ผังที่ตัวหาผังเลือก (null = หาไม่ได้ ⇒ ดู <paramref name="Problem"/>)</param>
/// <param name="RoleLabel">ชื่อบทบาทภาษาไทย (บัญชีธนาคาร · ผังพัก · ค่าคอม · รายการปรับปรุง …)</param>
/// <param name="Problem">เหตุที่หาผังไม่ได้ — ข้อความเดียวกับปัญหา <c>AccountUnresolved</c> ของแผน</param>
public sealed record SettlementAccountRef(Guid? AccountId, string? Code, string? Name, string RoleLabel, string? Problem);

/// <summary>ผังที่จะลงจริงของแผนทั้งรอบ — ขนานกับ <c>plan.PayoutJournal</c> และ <c>plan.FeeDocuments[i].Lines[j]</c> ·
/// <c>Clearing</c> = ผังพัก (ขาเงินของใบค่าธรรมเนียม/ใบขายสรุป/รับชำระ/คืนเงิน)</summary>
public sealed record SettlementPlanAccounts(
    IReadOnlyList<SettlementAccountRef> PayoutJournal,
    IReadOnlyList<IReadOnlyList<SettlementAccountRef>> FeeLines,
    SettlementAccountRef? Clearing);

/// <summary>
/// **หาผังบัญชีของแผนลงบัญชีรอบโอน — ตัวเดียว** (ลำดับตาม <see cref="SettlementAccountRoles"/>):
/// บทบาท <c>bank</c> = ผังที่ผูกกับบัญชีธนาคารของรอบโอน · มี <c>AccountId</c> (ผังของช่องทาง/FeeAccountMap/ผู้ใช้เลือก) ⇒
/// <b>ต้องอยู่ในผังของบริษัทนี้และเปิดใช้</b> ไม่งั้นล้มพร้อมชื่อบทบาท (ห้ามถอยไปผังมาตรฐานเงียบ ๆ — id ของบริษัทอื่นที่หลุดมาใน JSON
/// ต้องไม่กลายเป็นการลงบัญชีข้าม tenant) · ไม่มี id ⇒ ผังมาตรฐาน (<c>DefaultAccountCode</c>) ในผังของบริษัท
/// </summary>
public static class SettlementAccountResolver
{
    public static SettlementAccountResolution Resolve(SettlementPostingPlan plan, SettlementChartIndex chart, Guid? bankGlAccountId)
    {
        var errors = new List<string>();
        var (journal, jErr) = ResolveJournal(plan.PayoutJournal, chart, bankGlAccountId);
        errors.AddRange(jErr);

        var fees = new List<IReadOnlyList<Guid>>();
        foreach (var d in plan.FeeDocuments)
        {
            var ids = new List<Guid>();
            foreach (var fl in d.Lines)
                if (ResolveOne(fl.AccountRole, fl.AccountId, fl.DefaultAccountCode, chart, bankGlAccountId, errors) is Guid g)
                    ids.Add(g);
            fees.Add(ids);
        }

        // ผังพักว่าง = SettlementBatchMath แจ้ง ClearingAccountMissing พร้อมทางไปต่อแล้ว — ไม่ฟ้องซ้ำที่นี่
        Guid? clearing = null;
        if (plan.ClearingAccountId is not null)
            clearing = ResolveOne(SettlementAccountRoles.Clearing, plan.ClearingAccountId, null, chart, bankGlAccountId, errors);

        return new SettlementAccountResolution(journal, fees, clearing, errors.Distinct().ToList(), DescribePlan(plan, chart, bankGlAccountId));
    }

    /// <summary>
    /// **ผังที่จะลงจริงของทุกขา — สำหรับพรีวิว** (review198-D D-02) · ใช้ <see cref="ResolveOne"/> ตัวเดียวกับการลงจริง ⇒ ผังที่หน้าจอแสดง =
    /// ผังที่ JE/ใบค่าธรรมเนียมลง (ผังที่ผู้ใช้เลือกให้บรรทัดปรับปรุง · ผังใน FeeAccountMap ของช่องทาง · ผังที่ผูกกับบัญชีธนาคาร · ผังพัก) —
    /// เดิมพรีวิวแสดง "รหัสผังมาตรฐานของบทบาท" ขณะที่ JE ลงผังที่ตั้งทับไว้ ⇒ ผู้กดลงบัญชีอนุมัติจากข้อมูลที่ไม่ตรง
    /// </summary>
    private static SettlementPlanAccounts DescribePlan(SettlementPostingPlan plan, SettlementChartIndex chart, Guid? bankGlAccountId)
        => new(
            plan.PayoutJournal.Select(l => Describe(l.AccountRole, l.AccountId, l.DefaultAccountCode, chart, bankGlAccountId)).ToList(),
            plan.FeeDocuments
                .Select(d => (IReadOnlyList<SettlementAccountRef>)d.Lines
                    .Select(fl => Describe(fl.AccountRole, fl.AccountId, fl.DefaultAccountCode, chart, bankGlAccountId)).ToList())
                .ToList(),
            plan.ClearingAccountId is null ? null
                : Describe(SettlementAccountRoles.Clearing, plan.ClearingAccountId, null, chart, bankGlAccountId));

    /// <summary>ผังของขาเดียว (ไม่สะสม error ของแผน — ปัญหาอยู่ใน <see cref="SettlementAccountRef.Problem"/>)</summary>
    private static SettlementAccountRef Describe(string role, Guid? accountId, string? defaultCode, SettlementChartIndex chart,
        Guid? bankGlAccountId)
    {
        var problems = new List<string>();
        var id = ResolveOne(role, accountId, defaultCode, chart, bankGlAccountId, problems);
        var a = id is Guid g ? chart.ById(g) : null;
        return new SettlementAccountRef(a?.Id, a?.Code, a?.Name, RoleLabel(role), problems.FirstOrDefault());
    }

    /// <summary>หาผังของขา JE ชุดหนึ่ง (JE รอบโอน · JE ปิด chargeback) — ขาที่หาผังไม่ได้ไม่อยู่ในผลลัพธ์ แต่อยู่ใน errors เสมอ</summary>
    public static (IReadOnlyList<SettlementResolvedJournalLine> Lines, IReadOnlyList<string> Errors) ResolveJournal(
        IReadOnlyList<SettlementJournalLinePlan> lines, SettlementChartIndex chart, Guid? bankGlAccountId)
    {
        var errors = new List<string>();
        var result = new List<SettlementResolvedJournalLine>();
        foreach (var l in lines)
        {
            if (l.AccountRole == SettlementAccountRoles.Clearing && l.AccountId is null) continue;   // ClearingAccountMissing ของแผน
            if (ResolveOne(l.AccountRole, l.AccountId, l.DefaultAccountCode, chart, bankGlAccountId, errors) is Guid g)
                result.Add(new SettlementResolvedJournalLine(g, l.Debit, l.Credit, l.Description));
        }
        return (result, errors.Distinct().ToList());
    }

    private static Guid? ResolveOne(string role, Guid? accountId, string? defaultCode, SettlementChartIndex chart,
        Guid? bankGlAccountId, List<string> errors)
    {
        var label = RoleLabel(role);
        if (role == SettlementAccountRoles.Bank)
        {
            if (bankGlAccountId is not Guid bankId)
            {
                errors.Add("บัญชีธนาคารที่รับเงินของรอบโอนนี้ยังไม่ได้เลือก หรือยังไม่ได้ผูกผังบัญชี — เลือกบัญชีธนาคารของรอบโอน "
                    + "แล้วตรวจที่ ตั้งค่า → บัญชีธนาคาร ว่าผูกผังของบัญชีนั้นแล้ว");
                return null;
            }
            accountId = bankId;
        }
        if (accountId is Guid id)
        {
            var a = chart.ById(id);
            if (a == null)
            {
                errors.Add($"ผังที่ตั้งไว้สำหรับ \"{label}\" ไม่ใช่ผังของบริษัทนี้ (หรือถูกลบแล้ว) — เลือกผังใหม่ในหน้าตั้งค่าช่องทาง/บรรทัด");
                return null;
            }
            if (!a.IsActive)
            {
                errors.Add($"ผัง {a.Code} ที่ตั้งไว้สำหรับ \"{label}\" ถูกปิดใช้ — เปิดใช้ผังนั้นในหน้าผังบัญชี หรือเลือกผังอื่น");
                return null;
            }
            return a.Id;
        }
        if (!string.IsNullOrWhiteSpace(defaultCode))
        {
            var a = chart.ByCode(defaultCode);
            if (a is { IsActive: true }) return a.Id;
            errors.Add($"ไม่พบผัง {defaultCode.Trim()} ({label}) ที่เปิดใช้ในผังบัญชีของบริษัท — เพิ่มผัง {defaultCode.Trim()} ในหน้าผังบัญชี "
                + "หรือเลือกผังของบทบาทนี้เองในหน้าตั้งค่าช่องทาง");
            return null;
        }
        errors.Add($"ยังไม่ได้เลือกผังสำหรับ \"{label}\" — เลือกในหน้าตั้งค่าช่องทาง (หรือผังของบรรทัดปรับปรุง)");
        return null;
    }

    private static string RoleLabel(string role) => role switch
    {
        SettlementAccountRoles.Bank => "บัญชีธนาคารที่รับเงิน",
        SettlementAccountRoles.Clearing => "ผังพัก (ลูกหนี้แพลตฟอร์ม)",
        SettlementAccountRoles.Reserve => "เงินที่ผู้ให้บริการกันไว้",
        SettlementAccountRoles.Dispute => "พัก chargeback ระหว่างรอผล",
        SettlementAccountRoles.Commission => "ค่าคอมมิชชัน",
        SettlementAccountRoles.PaymentFee => "ค่าธรรมเนียมรับชำระเงิน",
        SettlementAccountRoles.Shipping => "ค่าขนส่ง",
        SettlementAccountRoles.Ads => "ค่าโฆษณา",
        SettlementAccountRoles.ServiceFee => "ค่าบริการแพลตฟอร์ม",
        SettlementAccountRoles.WithdrawalFee => "ค่าธรรมเนียมถอนเงิน",
        SettlementAccountRoles.WhtCredit => "ภาษีถูกหัก ณ ที่จ่าย",
        SettlementAccountRoles.FxGain => "กำไรจากอัตราแลกเปลี่ยน",
        SettlementAccountRoles.FxLoss => "ขาดทุนจากอัตราแลกเปลี่ยน",
        SettlementAccountRoles.Adjustment => "รายการปรับปรุง",
        SettlementAccountRoles.ChargebackLoss => "ขาดทุนจาก chargeback",
        SettlementAccountRoles.WhtPayable => "ภาษีหัก ณ ที่จ่ายค้างนำส่ง ภ.ง.ด.53",
        SettlementAccountRoles.WhtReimbursable => "ภาษีหัก ณ ที่จ่ายรอแพลตฟอร์มคืน",
        _ => role,
    };
}

/// <summary>ใบขายที่แผนจะรับชำระเข้าผังพัก — ข้อเท็จจริงจากฐาน (tenant แล้ว)</summary>
/// <param name="Found">พบใบนี้ในบริษัทนี้ (ไม่พบ = ไม่มี หรือเป็นของบริษัทอื่น)</param>
/// <param name="AlreadyReceived">รอบโอนนี้บันทึกรับชำระใบนี้ไปแล้ว (ลงบัญชีครั้งก่อนค้างครึ่งทาง) ⇒ ข้าม</param>
public sealed record SettlementReceiptTarget(
    Guid DocumentId, bool Found, string? DocumentNumber, DocumentType Type, DocumentStatus Status, decimal BalanceDue, bool AlreadyReceived);

/// <summary>บรรทัดที่แผนนับว่า "อยู่ในผังพักแล้ว" (อ้าง PaymentIntent/การรับชำระ) — ข้อเท็จจริงว่าเงินก้อนนั้นลงไว้ที่ผังไหนจริง (review198-A R-A1)</summary>
/// <param name="PostedClearingAccountId">ผังที่ขาเงินเข้าของรายการนั้นลงไว้จริง (null = ลงธนาคาร/เงินสด หรือหาไม่เจอ)</param>
/// <param name="SettledElsewhere">ถูกล้างออกจากผังพักไปแล้วด้วยเส้นอื่น (รอบโอน gateway เดิม · อีก batch)</param>
/// <param name="RefundOutcomeUnknown">รายการรับชำระที่การคืนเงินค้าง "ผลไม่แน่ชัด" (<c>PaymentIntent.RefundOutcomeUnknownSince</c> · review198-E2 E2-10)</param>
public sealed record SettlementClearingSource(
    IReadOnlyList<Guid> LineIds, string What, bool Found, Guid? PostedClearingAccountId, bool SettledElsewhere,
    bool RefundOutcomeUnknown = false);

/// <summary>หลักฐานว่ายอดขายที่จะออกใบสรุป "มีเอกสารขายอยู่แล้ว" (review198-A R-A7)</summary>
public sealed record SettlementDuplicateSale(IReadOnlyList<Guid> LineIds, string Evidence);

/// <summary>ข้อเท็จจริงจากฐานที่ด่านของผู้ลงบัญชีต้องใช้ (ผู้เรียกหาให้ทั้งหมด — ตัวด่านไม่แตะฐาน)</summary>
/// <param name="SummaryAbbreviatedBlock">ผลของ <c>AbbreviatedTaxInvoiceRule.Judge</c> ช่องทางเอกสาร ณ วันที่ใบขายสรุป (ฝ่ายค้าน C-6)</param>
/// <param name="StaleReceipts">การรับชำระที่มีป้ายของรอบโอนแต่ไม่ตรงแผนปัจจุบัน (<see cref="SettlementReceiptReconcile.Stale"/> · C-4)</param>
/// <param name="OrphanArtifacts">เอกสาร/การรับชำระที่การลงบัญชีสร้างให้รอบโอนที่ถูกยกเลิก/ลบแล้วของช่องทางเดียวกัน (C-1(d))</param>
/// <param name="SodSelfApprovalBlocked">ผลของ <see cref="SettlementPostingGate.SodSelfApproval"/> (คำตัดสินเจ้าของข้อ 7)</param>
public sealed record SettlementPostingFacts(
    SettlementBatchStatus Status,
    DateTime PayoutDay,
    DateTime TodayBangkok,
    string? PayoutPeriodClosed,
    IReadOnlyDictionary<DateTime, string> SummaryDatesClosed,
    IReadOnlyCollection<(int Year, int Month)> FiledVatPeriods,
    IReadOnlyCollection<(int Year, int Month)> FiledWhtPeriods,
    IReadOnlyList<string> AccountErrors,
    bool CounterpartyFound,
    bool CounterpartyHasTaxId,
    Guid? ChannelClearingAccountId,
    IReadOnlyList<SettlementReceiptTarget> ReceiptTargets,
    IReadOnlyList<SettlementClearingSource> ClearingSources,
    IReadOnlyList<SettlementDuplicateSale> DuplicateSales,
    bool CanApproveFeeDocuments,
    bool CanApproveSaleDocuments,
    int PartialItems,
    AbbreviatedInvoiceBlockReason SummaryAbbreviatedBlock = AbbreviatedInvoiceBlockReason.None,
    IReadOnlyList<string>? StaleReceipts = null,
    IReadOnlyList<string>? OrphanArtifacts = null,
    bool SodSelfApprovalBlocked = false);

/// <summary>
/// **ด่านของผู้ลงบัญชีรอบโอน — ต่อจากแผนของ <see cref="SettlementBatchMath.Plan"/>** (ปัญหาที่ต้องรู้ข้อมูลในฐาน)
///
/// <para>คืนแผนเดิมที่เติมปัญหา (ไม่คิดยอดใหม่) · <c>CanPost</c> = แผนเดิมลงได้ <b>และ</b> ไม่มีปัญหาที่บล็อกเพิ่ม · ทุกปัญหามี NextStep
/// (F2 ข้อ 8 — ผู้ที่ถูกกันต้องมีทางไปต่อ) · หน้าพรีวิวกับการลงจริงเรียกตัวเดียวกัน ⇒ พรีวิวผ่านแต่ลงไม่ได้ เกิดได้แค่เมื่อข้อมูลเปลี่ยนระหว่างสองจังหวะ</para>
/// </summary>
public static class SettlementPostingGate
{
    /// <summary>§87 ลงรายงานภาษีขายภายใน 3 วันทำการ</summary>
    public const int SalesReportBusinessDays = 3;

    public static SettlementPostingPlan Evaluate(SettlementPostingPlan plan, SettlementPostingFacts f)
    {
        var extra = new List<SettlementPlanIssue>();
        void Add(SettlementPlanIssueCode code, bool blocking, string message, string next,
            IReadOnlyList<Guid>? ids = null, decimal? amount = null)
            => extra.Add(new SettlementPlanIssue(code, blocking, message, next, ids ?? Array.Empty<Guid>(), amount));

        // ── สถานะ ──
        if (f.Status is SettlementBatchStatus.Posted or SettlementBatchStatus.BankMatched)
            Add(SettlementPlanIssueCode.StatusNotPostable, true, "รอบโอนนี้ลงบัญชีแล้ว",
                "ถ้าต้องแก้รายการ ให้กด \"ยกเลิกการลงบัญชี\" ก่อน (ระบบกลับรายการ JE และยกเลิกเอกสารที่สร้างให้ตามเส้นปกติ)");
        else if (f.Status == SettlementBatchStatus.Voided)
            Add(SettlementPlanIssueCode.StatusNotPostable, true, "รอบโอนนี้ถูกยกเลิกแล้ว",
                "นำเข้ารอบโอนใหม่จากไฟล์ของผู้ให้บริการ");

        // ── งวดบัญชี ──
        var touchesPayoutDay = plan.PayoutJournal.Count > 0 || plan.FeeDocuments.Count > 0 || plan.Receipts.Count > 0;
        if (touchesPayoutDay && f.PayoutPeriodClosed is string closed)
            Add(SettlementPlanIssueCode.PeriodClosed, true, closed,
                "ให้ผู้มีสิทธิ์เปิดงวดของวันที่รอบโอนก่อน — หรือถ้าวันที่รอบโอนผิด แก้วันที่แล้วดูตัวอย่างใหม่");
        foreach (var s in plan.SummarySales)
            if (f.SummaryDatesClosed.TryGetValue(s.Date, out var reason))
                Add(SettlementPlanIssueCode.PeriodClosed, true, reason,
                    "ใบขายสรุปต้องลงวันที่ขาย (จุดความรับผิด) — ให้ผู้มีสิทธิ์เปิดงวดนั้นก่อน หรือออกเอกสารขายของวันนั้นด้วยมือแล้วจับคู่บรรทัดกับเอกสารนั้น",
                    s.LineIds, s.Gross);

        // ── เดือนภาษีที่ยื่นแล้ว (TaxFilingLockPolicy) ──
        foreach (var s in plan.SummarySales)
        {
            if (f.FiledVatPeriods.Contains((s.Date.Year, s.Date.Month)))
                Add(SettlementPlanIssueCode.TaxPeriodFiled, true,
                    $"ใบขายสรุปวันที่ {ThaiDate.ToThaiDisplayString(s.Date)} ยอด {s.Gross:N2} ตกเดือนภาษี {s.Date:MM}/{s.Date.Year + 543} ที่ยื่น ภ.พ.30 แล้ว",
                    "ภาษีขายที่ตกหล่นของเดือนที่ยื่นแล้วต้องยื่นแบบเพิ่มเติม (พร้อมเงินเพิ่ม) — ให้ผู้ทำบัญชีออกเอกสารขายของวันนั้นเองผ่านเส้นแบบเพิ่มเติม "
                    + "แล้วจับคู่บรรทัดขายกับเอกสารนั้น (ระบบจะรับชำระเข้าผังพักให้แทนการออกใบสรุป)",
                    s.LineIds, s.Gross);
            else if (WeekdaysAfter(s.Date, f.TodayBangkok) > SalesReportBusinessDays)
                Add(SettlementPlanIssueCode.SummarySaleLate, false,
                    $"ใบขายสรุปวันที่ {ThaiDate.ToThaiDisplayString(s.Date)} ลงรายงานภาษีขายช้ากว่า {SalesReportBusinessDays} วันทำการ (§87)",
                    "ลงบัญชีได้ — แต่ควรนำเข้ายอดขายรายวัน (ไฟล์คำสั่งซื้อ) ให้ทันรายงานภาษีขาย แทนการรอไฟล์รอบโอน",
                    s.LineIds, s.Gross);
        }
        var whtLegs = plan.FeeDocuments.Any(d => d.WhtAmount > 0m
            && d.WhtMode is SettlementFeeWhtMode.SelfWithholdReimbursed or SettlementFeeWhtMode.SelfWithholdPayerBorne);
        if (whtLegs && f.FiledWhtPeriods.Contains((f.PayoutDay.Year, f.PayoutDay.Month)))
            Add(SettlementPlanIssueCode.TaxPeriodFiled, true,
                $"ภาษีหัก ณ ที่จ่ายของรอบโอนนี้ตกเดือน {f.PayoutDay:MM}/{f.PayoutDay.Year + 543} ที่ยื่น ภ.ง.ด.53 แล้ว",
                "ยื่น ภ.ง.ด.53 เพิ่มเติมของเดือนนั้น แล้วให้ผู้มีสิทธิ์ปลดล็อกรายงานก่อนลงบัญชี — หรือเปลี่ยนโหมดหัก ณ ที่จ่ายของช่องทางถ้าแพลตฟอร์มเป็นผู้หักแทน");

        // ── ผังบัญชี ──
        foreach (var err in f.AccountErrors)
            Add(SettlementPlanIssueCode.AccountUnresolved, true, err, "ตั้งผังให้ครบแล้วดูตัวอย่างใหม่ (ระบบไม่ลงผังอื่นแทนให้เงียบ ๆ)");

        // ── ผู้รับเงินค่าธรรมเนียม (§65 ตรี (18) · 50 ทวิ · ภ.พ.36) ──
        if (plan.FeeDocuments.Count > 0)
        {
            if (!f.CounterpartyFound)
                Add(SettlementPlanIssueCode.CounterpartyMissing, true,
                    "ช่องทางนี้ยังไม่ได้ผูกผู้ติดต่อของแพลตฟอร์ม (ผู้รับเงินค่าธรรมเนียม) หรือผู้ติดต่อถูกลบ",
                    "เลือกผู้ติดต่อ = นิติบุคคลของแพลตฟอร์ม (ตามใบกำกับค่าธรรมเนียม) ในหน้าตั้งค่าช่องทาง");
            else if (!f.CounterpartyHasTaxId)
                Add(SettlementPlanIssueCode.CounterpartyMissing, true,
                    "ผู้ติดต่อของแพลตฟอร์มยังไม่มีเลขผู้เสียภาษี — รายจ่ายที่ไม่ระบุผู้รับเงินเป็นรายจ่ายต้องห้าม (§65 ตรี (18))",
                    "เติมเลขผู้เสียภาษี 13 หลัก + สาขา ของแพลตฟอร์มในหน้าผู้ติดต่อ (ดูจากใบกำกับค่าธรรมเนียมรายเดือน)");
        }

        // ── คืนเงินที่จับคู่ใบเดิมได้: ใบลดหนี้ต้องให้คนเลือกเหตุผล §86/10 ──
        foreach (var r in plan.Refunds)
            Add(SettlementPlanIssueCode.RefundNeedsCreditNote, true,
                $"คืนเงินผู้ซื้อ {r.Amount:N2} ของใบขายที่จับคู่ไว้ — ต้องออกใบลดหนี้อ้างใบเดิม (§86/10) ซึ่งต้องเลือกเหตุผลเอง (คืนสินค้า = คืนสต็อก · ลดราคา/ปรับยอด)",
                "เปิดใบขายเดิม → ออกใบลดหนี้ (เลือกเหตุผล) → บันทึกจ่ายคืนโดยเลือกจ่ายจาก \"ผังพักของช่องทาง\" แล้วผูกบรรทัดคืนเงินนี้กับการจ่ายคืนนั้น "
                + "(ระบบจะนับว่าอยู่ในผังพักแล้ว ไม่ลงซ้ำ)",
                r.LineIds, r.Amount);

        // ── ใบขายที่จะรับชำระ ──
        foreach (var r in plan.Receipts)
        {
            var t = f.ReceiptTargets.FirstOrDefault(x => x.DocumentId == r.DocumentId);
            if (t is { AlreadyReceived: true }) continue;
            string? why = null;
            if (t is null || !t.Found) why = "ไม่พบใบขายที่จับคู่ไว้ในบริษัทนี้";
            else if (!ArApScope.IsReceivable(t.Type)) why = $"ใบ {t.DocumentNumber} ไม่ใช่เอกสารตั้งลูกหนี้ (ใบแจ้งหนี้/ใบกำกับ/ใบเพิ่มหนี้)";
            else if (t.Status is not (DocumentStatus.Approved or DocumentStatus.Sent or DocumentStatus.PartiallyPaid or DocumentStatus.Overdue))
                why = $"ใบ {t.DocumentNumber} อยู่สถานะ {t.Status} — รับชำระได้เฉพาะใบที่อนุมัติแล้วและยังค้างชำระ";
            else if (t.BalanceDue + 0.005m < r.Amount)
                why = $"ใบ {t.DocumentNumber} ค้างชำระ {t.BalanceDue:N2} น้อยกว่ายอดที่แพลตฟอร์มโอน {r.Amount:N2} (เคยรับชำระทางอื่นแล้ว?)";
            if (why != null)
                Add(SettlementPlanIssueCode.ReceiptDocumentNotPayable, true, why,
                    "จับคู่บรรทัดขายกับใบที่ถูกต้อง · ถ้าใบนั้นรับชำระทางอื่นไปแล้ว ให้ผูกบรรทัดกับการรับชำระนั้นแทน (ระบบจะไม่รับชำระซ้ำ)",
                    r.LineIds, r.Amount);
        }

        // ── บรรทัดที่นับว่าอยู่ในผังพักแล้ว (R-A1) ──
        foreach (var c in f.ClearingSources)
        {
            // review198-E2 E2-10: การคืนเงินผ่านระบบที่ผลยังไม่แน่ชัด — ไม่รู้ว่าเงินออกจาก wallet แล้วหรือยัง ⇒ ลงรอบโอนทับไม่ได้
            if (c.RefundOutcomeUnknown)
                Add(SettlementPlanIssueCode.RefundOutcomeUnknown, true,
                    $"{c.What} มีการคืนเงินที่ผลยังไม่แน่ชัด (ส่งคำขอคืนเงินแล้วไม่ได้คำตอบจากผู้ให้บริการ) — ยังไม่รู้ว่าเงินคืนออกจาก wallet แล้วหรือไม่",
                    "ตรวจผลการคืนเงินก่อน: เปิดรายการรับชำระออนไลน์นั้น → ยืนยันผลการคืนเงินกับผู้ให้บริการ แล้วดูตัวอย่างรอบโอนใหม่",
                    c.LineIds, null);
            string? why = null;
            if (!c.Found) why = $"ไม่พบ{c.What}ที่บรรทัดอ้างถึงในบริษัทนี้";
            else if (c.SettledElsewhere) why = $"{c.What}นี้ถูกล้างออกจากผังพักไปแล้วด้วยรอบโอนอื่น — นับซ้ำจะทำให้ผังพักติดลบ";
            else if (c.PostedClearingAccountId is null || c.PostedClearingAccountId != f.ChannelClearingAccountId)
                why = $"{c.What}ลงเงินไว้คนละผังกับผังพักของช่องทางนี้ — ถ้าลงบัญชีรอบนี้ ผังเดิมจะค้างยอดตลอดไป และผังพักของช่องทางติดลบเท่ากัน";
            if (why != null)
                Add(SettlementPlanIssueCode.ClearingSourceMismatch, true, why,
                    "ตั้งผังพักของช่องทางให้เป็นผังเดียวกับที่รับชำระลงไว้ (gateway = ผังพักของการตั้งค่ารับชำระออนไลน์ เช่น 11340) "
                    + "หรือถอดการผูกบรรทัดกับรายการนั้นแล้วจับคู่ใหม่",
                    c.LineIds, null);
        }

        // ── หัวใบขายสรุป (ฝ่ายค้าน C-6): มี VAT แต่บริษัทไม่มีสิทธิ์ §86/6 ⇒ ใบที่ออกให้ลูกค้าเงินสดถูกลดหัวเป็นใบเสร็จ (REC) เงียบ ๆ ──
        if (plan.SummarySales.Any(s => s.Vat > 0m)
            && f.SummaryAbbreviatedBlock is not (AbbreviatedInvoiceBlockReason.None or AbbreviatedInvoiceBlockReason.NotVatRegistered))
            Add(SettlementPlanIssueCode.SummaryTaxInvoiceNotAllowed, true,
                "ใบขายสรุปรายวันออกให้ \"ลูกค้าเงินสด\" (ไม่มีชื่อ/ที่อยู่ผู้ซื้อ) จึงเป็นใบกำกับภาษีเต็มรูป (§86/4) ไม่ได้ และบริษัทยังออกใบกำกับภาษีอย่างย่อ "
                + "(§86/6) ไม่ได้ — ถ้าออกไป หัวจะถูกลดเป็น \"ใบเสร็จรับเงิน\" (เลขชุด REC) ทั้งที่ภาษีขายเข้า ภ.พ.30 · "
                + AbbreviatedTaxInvoiceRule.Message(f.SummaryAbbreviatedBlock),
                "ถ้ากิจการขายปลีก/ให้บริการลักษณะขายปลีกผ่านแพลตฟอร์ม ให้ติ๊ก \"ประกอบกิจการขายปลีก\" ในหน้าข้อมูลบริษัทแล้วดูตัวอย่างใหม่ · "
                + "ไม่ใช่ ⇒ ออกใบกำกับภาษีเต็มรูปรายออเดอร์ (ชื่อ/ที่อยู่ผู้ซื้อ) แล้วจับคู่บรรทัดขายกับใบนั้น (ระบบรับชำระเข้าผังพักแทนการออกใบสรุป)",
                plan.SummarySales.SelectMany(s => s.LineIds).ToList(), plan.SummarySales.Sum(s => s.Gross));

        // ── การรับชำระที่ค้างจากครั้งก่อนไม่ตรงแผน (C-4) · ของกำพร้าจากรอบโอนที่ยกเลิกแล้ว (C-1(d)) ──
        foreach (var why in f.StaleReceipts ?? Array.Empty<string>())
            Add(SettlementPlanIssueCode.StaleDocument, true, why,
                "ยกเลิกการรับชำระนั้นที่หน้าเอกสาร (รอบโอนยังไม่ลงบัญชีครบ จึงยกเลิกทีละรายการได้) แล้วดูตัวอย่างใหม่ — ระบบจะรับชำระตามแผนปัจจุบันให้");
        foreach (var why in f.OrphanArtifacts ?? Array.Empty<string>())
            Add(SettlementPlanIssueCode.OrphanPostingArtifacts, true, why,
                "ยกเลิกเอกสาร/การรับชำระเหล่านั้นที่หน้าเอกสารก่อน (รอบโอนเจ้าของถูกยกเลิกแล้ว จึงยกเลิกทีละรายการได้) — ถ้ายังอยู่ ลงบัญชีรอบนี้ทับ "
                + "= ค่าธรรมเนียม/ภาษีซื้อ/รายได้ซ้ำ");

        // ── รายได้ซ้ำ (R-A7) ──
        foreach (var d in f.DuplicateSales)
            Add(SettlementPlanIssueCode.SummarySaleDuplicate, true, d.Evidence,
                "ถ้าเป็นออเดอร์ชุดเดียวกัน (มีเอกสารขายแล้ว) ให้จับคู่บรรทัดกับเอกสารนั้นแทน — ระบบจะรับชำระเข้าผังพักให้ ไม่ออกใบสรุปซ้ำ · "
                + "ถ้าเป็นออเดอร์คนละชุดจริง ให้ออกเอกสารขายของวันนั้นเองแล้วจับคู่บรรทัด (1 วัน/แพลตฟอร์ม มีใบสรุปได้ใบเดียว — DECISIONS ข้อ 2)",
                d.LineIds, null);

        // ── แยกหน้าที่ (คำตัดสินเจ้าของข้อ 7): ผู้นำเข้ารอบโอน = ผู้ทำ · ผู้กดลงบัญชี = ผู้อนุมัติเอกสารที่ระบบออกให้ ──
        if (f.SodSelfApprovalBlocked && (plan.FeeDocuments.Count > 0 || plan.SummarySales.Count > 0))
            Add(SettlementPlanIssueCode.SodSelfApproval, true,
                "บริษัทเปิด \"แยกหน้าที่ผู้สร้าง/ผู้อนุมัติ\" และผู้กดลงบัญชีคือผู้นำเข้ารอบโอนนี้เอง — เอกสารค่าธรรมเนียม/ใบขายสรุปที่ระบบออกให้จะมีผู้ทำและผู้อนุมัติคนเดียวกัน",
                "ให้ผู้มีสิทธิ์อนุมัติคนอื่น (ไม่ใช่ผู้นำเข้ารอบโอน) เป็นผู้กดลงบัญชี — ระบบบันทึกผู้กดเป็นผู้อนุมัติเอกสารทุกใบของรอบนี้");

        // ── สิทธิ์ (ทุกทางเข้าอนุมัติเอกสารต้องผ่าน DocumentPermissionHelper.CanApproveAsync) ──
        if (plan.FeeDocuments.Count > 0 && !f.CanApproveFeeDocuments)
            Add(SettlementPlanIssueCode.PermissionDenied, true,
                "ผู้ใช้นี้ไม่มีสิทธิ์อนุมัติเอกสารฝั่งซื้อ (ใบสำคัญจ่ายค่าธรรมเนียมที่ระบบจะออกให้)",
                "ให้ผู้มีสิทธิ์อนุมัติเอกสารฝั่งซื้อเป็นผู้กดลงบัญชีรอบโอนนี้");
        if (plan.SummarySales.Count > 0 && !f.CanApproveSaleDocuments)
            Add(SettlementPlanIssueCode.PermissionDenied, true,
                "ผู้ใช้นี้ไม่มีสิทธิ์อนุมัติเอกสารฝั่งขาย (ใบขายสรุปรายวันที่ระบบจะออกให้)",
                "ให้ผู้มีสิทธิ์อนุมัติเอกสารฝั่งขายเป็นผู้กดลงบัญชีรอบโอนนี้");

        if (f.PartialItems > 0 && f.Status is not (SettlementBatchStatus.Posted or SettlementBatchStatus.BankMatched))
            Add(SettlementPlanIssueCode.PartialProgress, false,
                $"การลงบัญชีครั้งก่อนค้างครึ่งทาง — สร้างเอกสาร/รับชำระไปแล้ว {f.PartialItems} รายการ",
                "กด \"ลงบัญชี\" อีกครั้ง — ระบบทำต่อจากขั้นที่ค้าง (ไม่สร้างซ้ำ) · ถ้าต้องการเริ่มใหม่ ยกเลิกเอกสารเหล่านั้นก่อน");

        if (extra.Count == 0) return plan;
        return plan with
        {
            Issues = plan.Issues.Concat(extra).ToList(),
            CanPost = plan.CanPost && !extra.Any(i => i.Blocking),
        };
    }

    /// <summary>
    /// **แยกหน้าที่ของเอกสารที่การลงบัญชีรอบโอนออกให้** (คำตัดสินเจ้าของรอบ 198 ข้อ 7 · review198-C C-7) — ผู้ทำ = ผู้นำเข้ารอบโอน
    /// (<c>SettlementBatch.CreatedBy</c> = user id ของผู้นำเข้า) · ผู้อนุมัติ = คนกดลงบัญชี · บริษัทเปิด <c>SodBlockSelfApproval</c> และเป็นคนเดียวกัน ⇒ true (บล็อก)
    /// <para>ผู้นำเข้าไม่รู้ (null/ว่าง) ⇒ <b>บล็อก</b> เมื่อเปิดแยกหน้าที่ — "ไม่รู้" ห้ามตกเป็น "ผ่าน" (DOCTRINE §1)</para>
    /// </summary>
    public static bool SodSelfApproval(bool sodBlockSelfApproval, string? batchCreatedBy, Guid postingUserId)
        => sodBlockSelfApproval
           && (string.IsNullOrWhiteSpace(batchCreatedBy)
               || string.Equals(batchCreatedBy.Trim(), postingUserId.ToString(), StringComparison.OrdinalIgnoreCase));

    /// <summary>จำนวนวันจันทร์–ศุกร์หลัง <paramref name="from"/> จนถึง <paramref name="to"/> (ไม่หักวันหยุดราชการ ⇒ นับวันทำการ<b>มากกว่าจริง</b>
    /// = เตือนเร็วกว่าจริง ทิศที่ปลอดภัย)</summary>
    private static int WeekdaysAfter(DateTime from, DateTime to)
    {
        var n = 0;
        for (var d = from.Date.AddDays(1); d <= to.Date && n <= 30; d = d.AddDays(1))
            if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) n++;
        return n;
    }
}

/// <summary>
/// **คำขอสร้างเอกสาร/รับชำระ/50 ทวิ จากแผน — ตัวประกอบตัวเดียว** (service ส่งตรงเข้า <c>IDocumentService</c> /
/// <c>IWithholdingTaxCertService</c> — เส้นเลขที่เอกสาร · รายงานภาษีซื้อ/ขาย · ด่าน §82/§86 · e-Tax เดินตามปกติ)
///
/// <para>═══ ตัดสินใจที่ตั้งใจ ═══
/// <list type="bullet">
/// <item><b>ใบค่าธรรมเนียม = ใบสำคัญจ่าย (เงินสด) จ่ายจากผังพัก</b> (<c>PaymentAccountId</c> = ผังพัก) — ค่าธรรมเนียมถูกหักจาก wallet ไปแล้ว
/// ใบเดียวจบ: Dr ค่าธรรมเนียม + ภาษีซื้อ / Cr ผังพัก · ภาษีซื้อไปตามด่าน §86/4 เดิม (ยังไม่มีใบกำกับของแพลตฟอร์ม ⇒ 11640 ยังไม่ถึงกำหนด
/// จนเติมใบกำกับรายเดือนผ่าน "เติมใบกำกับผู้ขาย" · ครบ 6 เดือนงาน §82/3 จัดการเอง) · ต่างประเทศ ⇒ <c>IsForeignService</c> (Cr 21912 ภ.พ.36 ·
/// Cr ผังพักเท่าฐาน — เส้น <c>ForeignServiceVat</c> เดิม) · (เส้นใบบันทึกค่าใช้จ่าย+รับชำระ ใช้กับบริการต่างประเทศไม่ได้: ยอดค้างรวม VAT แต่เจ้าหนี้ตั้งแค่ฐาน)</item>
/// <item><b>ห้ามหัก WHT บนใบ/ตอนจ่าย</b> — ขา WHT (W2/W3) อยู่ใน JE รอบโอนแล้ว · ใบถือข้อมูลไว้ออก 50 ทวิ ผ่าน <see cref="WhtCertificate"/> เท่านั้น</item>
/// <item>VAT ส่งเป็นตัวเลขของแผนตรง ๆ (<c>VatAmountOverride</c>) — ไม่ให้ DocumentService คิด 7% ใหม่ (ต่างจากการถอด 7/107 ราว 6.5% ของยอด · R-A11)</item>
/// <item><b>ใบขายสรุปรายวัน</b>: จด VAT = ใบกำกับภาษี/ใบเสร็จรับเงินใบเดียว (<c>IssuedAsCashReceipt</c> · เงินเข้า = ผังพัก) · ไม่จด VAT = ใบเสร็จรับเงิน ·
/// ผู้ซื้อ = ผู้ติดต่อกลาง "ลูกค้าเงินสด" (<see cref="WalkInCustomerContact"/>) · <b>ไม่</b>ตั้ง <c>BuyerDeclinedTaxInvoice</c> แทนผู้ซื้อ ·
/// จุดความรับผิด = วันที่ของบรรทัด (<c>TxnDate</c> ตามปฏิทินไทย · ไม่มี = วันที่รอบโอน) — D2 ตั้งให้เป็นวันส่งมอบ แต่ไฟล์รอบโอนเฟส 1 ไม่มีวันส่งมอบ</item>
/// </list></para>
/// </summary>
public static class SettlementDocumentBuilder
{
    /// <summary>ธงบน <c>Document.InternalNotes</c> ของใบขายสรุปที่ระบบสร้าง — ป้ายให้ตรวจรายได้ซ้ำ (DECISIONS ข้อ 3)</summary>
    public const string SummaryReviewTag = "[SETTLEMENT-SUMMARY]";

    public static CreateDocumentRequest FeeDocument(SettlementFeeDocumentPlan fee, IReadOnlyList<Guid> lineAccountIds,
        Guid counterpartyContactId, Guid clearingAccountId, DateTime payoutDay, string payoutRef, string channelName)
    {
        if (lineAccountIds.Count != fee.Lines.Count)
            throw new ArgumentException("จำนวนผังไม่เท่าจำนวนบรรทัดของใบค่าธรรมเนียม", nameof(lineAccountIds));
        var foreign = fee.VatTreatment is SettlementFeeVatTreatment.SelfAssessedPp36 or SettlementFeeVatTreatment.SelfAssessedPp36NotClaimable;
        var lines = new List<DocumentLineRequest>();
        for (var i = 0; i < fee.Lines.Count; i++)
        {
            var l = fee.Lines[i];
            if (l.Deducted == 0m && l.Expense == 0m) continue;
            var desc = $"{l.LabelTh} — {channelName} รอบโอน {payoutRef}";
            DocumentLineRequest line = fee.VatTreatment switch
            {
                SettlementFeeVatTreatment.InputVatPending or SettlementFeeVatTreatment.SelfAssessedPp36 =>
                    new DocumentLineRequest(desc, 1m, null, l.Expense, 0m, PartnerVatRate.StatutoryRate, 0m, lineAccountIds[i],
                        VatAmountOverride: l.InputVat),
                // §83/6 ผู้จ่ายไม่จด VAT: ยังตั้งหนี้ ภ.พ.36 แต่ภาษีซื้อเคลมไม่ได้ ⇒ ฐาน + VAT เข้าค่าใช้จ่าย (R-A4)
                SettlementFeeVatTreatment.SelfAssessedPp36NotClaimable =>
                    new DocumentLineRequest(desc, 1m, null, l.Deducted, 0m, PartnerVatRate.StatutoryRate, 0m, lineAccountIds[i],
                        IsVatClaimable: false, VatNonClaimableReason: "บริษัทไม่ได้จด VAT — ภาษีซื้อ ภ.พ.36 เคลมไม่ได้ (§83/6)",
                        VatAmountOverride: l.Pp36Payable),
                // ไม่มี VAT / VAT ที่ถูกเก็บแต่เคลมไม่ได้ (ไม่จด VAT) — ทั้งก้อนเป็นค่าใช้จ่าย
                _ => new DocumentLineRequest(desc, 1m, null, l.Expense, 0m, 0m, 0m, lineAccountIds[i]),
            };
            lines.Add(line);
        }
        return new CreateDocumentRequest(
            DocumentType.PaymentVoucher, payoutDay, null, counterpartyContactId, payoutRef,
            $"ค่าธรรมเนียม/ค่าบริการที่ {channelName} หักจากยอดโอนรอบ {payoutRef} (จ่ายจากเงินพักในแพลตฟอร์ม)",
            lines,
            PaymentAccountId: clearingAccountId,
            Currency: "THB",
            PaymentType: PaymentType.Cash,
            IsForeignService: foreign);
    }

    public static CreateDocumentRequest SummaryDocument(SettlementSummarySalePlan s, bool vatRegistered, Guid walkInContactId,
        Guid clearingAccountId, string payoutRef, string channelName)
    {
        var desc = $"ยอดขายผ่าน {channelName} ประจำวันที่ {ThaiDate.ToThaiDisplayString(s.Date)} ({s.LineIds.Count} รายการ)"
            + (s.SellerVoucher != 0m ? $" · หักส่วนลดร้าน {-s.SellerVoucher:N2}" : "")
            + (s.PlatformVoucher != 0m ? $" · รวมโค้ดส่วนลดที่แพลตฟอร์มออกเงิน {s.PlatformVoucher:N2}" : "");
        var line = s.Vat > 0m
            ? new DocumentLineRequest(desc, 1m, null, s.Net, 0m, PartnerVatRate.StatutoryRate, 0m, null, VatAmountOverride: s.Vat)
            : new DocumentLineRequest(desc, 1m, null, s.Net, 0m, 0m, 0m, null);
        return new CreateDocumentRequest(
            vatRegistered ? DocumentType.TaxInvoice : DocumentType.Receipt,
            s.Date, null, walkInContactId, payoutRef,
            $"สรุปยอดขายผ่าน {channelName} ประจำวันที่ {ThaiDate.ToThaiDisplayString(s.Date)} · รอบโอน {payoutRef}",
            new List<DocumentLineRequest> { line },
            IssuedAsCashReceipt: vatRegistered ? true : null,
            PaymentAccountId: clearingAccountId,
            Currency: "THB",
            DeliveryDate: s.Date);
    }

    /// <summary>หมายเหตุภายใน (ไม่พิมพ์ลงกระดาษ) ของใบขายสรุป — ธงให้ตรวจ + ออเดอร์ที่อยู่ในใบ</summary>
    public static string SummaryReviewNote(SettlementSummarySalePlan s, string payoutRef, string channelName)
    {
        var orders = s.ExternalOrderIds.Take(50).ToList();
        return $"{SummaryReviewTag} ใบขายสรุปรายวันที่ระบบสร้างอัตโนมัติจากรอบโอน {payoutRef} ({channelName}) — ต้องตรวจ: "
            + "ยอดขายเหล่านี้ยังไม่เคยออกเอกสารขายทางอื่น (กันรายได้/ภาษีขายซ้ำ) · ใบนี้ไม่ตัดสต็อก/ต้นทุนขาย (ไฟล์รอบโอนไม่มีรายการสินค้า)"
            + (orders.Count > 0
                ? $"\nออเดอร์ ({s.ExternalOrderIds.Count}): {string.Join(", ", orders)}{(s.ExternalOrderIds.Count > orders.Count ? " …" : "")}"
                : "");
    }

    /// <summary>รับชำระใบขายที่จับคู่ได้ — เงินเข้า = ผังพัก · WHT/ใบเสร็จตามค่าเดิมของเส้นรับชำระ · ป้ายของรอบโอนใน Notes (บันทึกพร้อมการรับชำระ)</summary>
    public static CreatePaymentRequest ReceiptPayment(SettlementReceiptPlan r, Guid batchId, Guid clearingAccountId,
        DateTime payoutDay, string payoutRef, string channelName)
        => new(r.DocumentId, payoutDay, r.Amount, PaymentMethod.EWallet, payoutRef, null,
            $"{SettlementPostingKeys.PaymentMarker(batchId)} รับเงินผ่าน {channelName} รอบโอน {payoutRef} (เงินเข้าผังพักของแพลตฟอร์ม — ยังไม่เข้าธนาคาร)",
            OverridePaymentAccountId: clearingAccountId);

    /// <summary>50 ทวิ ของ WHT ที่ JE รอบโอนตั้ง 21917 (W2 หักเองแล้วแพลตฟอร์มคืน · W3 ออกภาษีแทน) — null = ไม่ต้องออก
    /// (None · W1 แพลตฟอร์มเป็นตัวแทนหัก/ยื่นแทน — ห้ามนับเข้ายอดที่เรายื่นเอง) · ยอด = ชุดบรรทัดเดียวกับขา 21917 (WhtAmount &gt; 0)</summary>
    public static CreateWithholdingTaxCertRequest? WhtCertificate(SettlementFeeDocumentPlan fee, Guid counterpartyContactId,
        Guid feeDocumentId, DateTime payoutDay, string payoutRef)
    {
        if (fee.WhtMode is not (SettlementFeeWhtMode.SelfWithholdReimbursed or SettlementFeeWhtMode.SelfWithholdPayerBorne))
            return null;
        var borne = fee.WhtMode == SettlementFeeWhtMode.SelfWithholdPayerBorne;
        var lines = fee.Lines.Where(l => l.WhtAmount > 0m && !string.IsNullOrWhiteSpace(l.WhtIncomeCode))
            .Select(l => new WithholdingTaxCertLineRequest(
                l.WhtIncomeCode!, $"{l.LabelTh} (รอบโอน {payoutRef})", payoutDay,
                l.WhtCertIncome, l.WhtRatePercent, l.WhtAmount,
                // เงื่อนไขบน 50 ทวิ: 1 = หัก ณ ที่จ่าย · 2 = ออกให้ตลอดไป (ช่องทางตั้งโหมดออกภาษีแทนไว้ถาวร)
                borne ? "2" : "1"))
            .ToList();
        if (lines.Count == 0) return null;
        return new CreateWithholdingTaxCertRequest(counterpartyContactId, TaxType.WithholdingTax53,
            payoutDay.Year, payoutDay.Month,
            borne ? WithholdingTaxCertType.PayAlways : WithholdingTaxCertType.Withhold,
            lines, feeDocumentId);
    }
}

/// <summary>รายการเดินบัญชีที่ผู้ใช้เลือกจับคู่กับรอบโอน — ข้อเท็จจริงจากฐาน (tenant แล้ว)</summary>
/// <param name="Found">พบในบริษัทนี้ (ไม่พบ = ไม่มีจริง หรือเป็นของบริษัทอื่น)</param>
/// <param name="LinkedBatchId">รอบโอนอื่นที่ผูกรายการนี้ไว้แล้ว</param>
public sealed record SettlementBankTxnFacts(
    bool Found, Guid BankAccountId, BankTransactionType Type, decimal Amount,
    ReconciliationStatus Status, Guid? MatchedJournalEntryId, Guid? LinkedBatchId);

/// <param name="AlreadyDone">จับคู่ไว้แล้วกับรายการนี้ (กดซ้ำ = ไม่ทำอะไร)</param>
/// <param name="NeedsReconcile">ต้องให้เจ้าของการจับคู่ธนาคาร (<c>IBankService.ReconcileAsync</c>) ประทับฝั่งรายการเดินบัญชี</param>
public sealed record SettlementBankMatchDecision(bool Ok, bool AlreadyDone, bool NeedsReconcile, string Message);

/// <summary>
/// **ตัดสินว่ารอบโอนจับคู่กับรายการเดินบัญชีได้ไหม** (สถานะ <c>BankMatched</c>) — ราก R1: สถานะ "เงินเข้าธนาคารแล้ว" ตั้งได้เฉพาะเมื่อ
/// มีรายการเดินบัญชี<b>จริง</b> ของบริษัทนี้ ในบัญชีเดียวกัน ทิศเดียวกัน ยอดเท่ากันพอดี · ห้ามประทับเองจากการกดปุ่ม
/// </summary>
public static class SettlementBankMatch
{
    public static SettlementBankMatchDecision Check(SettlementBatchStatus status, Guid batchId, decimal netPayout,
        Guid? batchBankAccountId, Guid? payoutJournalEntryId, Guid? currentTxnId, Guid requestedTxnId, SettlementBankTxnFacts txn)
    {
        SettlementBankMatchDecision No(string m) => new(false, false, false, m);
        if (status == SettlementBatchStatus.BankMatched)
            return currentTxnId == requestedTxnId
                ? new SettlementBankMatchDecision(true, true, false, "รอบโอนนี้จับคู่กับรายการเดินบัญชีนี้ไว้แล้ว")
                : No("รอบโอนนี้จับคู่กับรายการเดินบัญชีอื่นไว้แล้ว — ยกเลิกการลงบัญชีก่อนถ้าต้องการจับคู่ใหม่");
        if (status != SettlementBatchStatus.Posted)
            return No("ลงบัญชีรอบโอนก่อน แล้วค่อยจับคู่กับรายการเดินบัญชี");
        if (netPayout == 0m || payoutJournalEntryId is null)
            return No("รอบโอนนี้ไม่มีเงินเข้า/ออกธนาคาร (ยอดโอน 0) — ไม่มีอะไรให้จับคู่");
        if (!txn.Found)
            return No("ไม่พบรายการเดินบัญชีที่เลือกในบริษัทนี้ — นำเข้าสเตทเมนต์ธนาคารก่อน แล้วเลือกรายการที่เงินเข้าจริง");
        if (txn.LinkedBatchId is Guid other && other != batchId)
            return No("รายการเดินบัญชีนี้ถูกจับคู่กับรอบโอนอื่นแล้ว");
        if (batchBankAccountId is Guid bank && txn.BankAccountId != bank)
            return No("รายการเดินบัญชีนี้อยู่คนละบัญชีธนาคารกับที่รอบโอนระบุ — เลือกรายการของบัญชีที่รับเงินจริง");
        var inflow = txn.Type is BankTransactionType.Deposit or BankTransactionType.Interest;
        if (netPayout > 0m != inflow)
            return No(netPayout > 0m
                ? "รอบโอนนี้เป็นเงินเข้า แต่รายการที่เลือกเป็นเงินออก"
                : "รอบโอนนี้เป็นเงินออก (เติมเงินเข้า wallet) แต่รายการที่เลือกเป็นเงินเข้า");
        if (Math.Abs(txn.Amount) != Math.Abs(netPayout))
            return No($"ยอดรายการเดินบัญชี ({Math.Abs(txn.Amount):N2}) ไม่เท่ายอดโอนของรอบ ({Math.Abs(netPayout):N2}) — "
                + "ตรวจว่าเลือกรายการถูก หรือแก้ยอดโอนของรอบ (ยกเลิกการลงบัญชีก่อน)");
        if (txn.Status == ReconciliationStatus.Excluded)
            return No("รายการเดินบัญชีนี้ถูกตั้งเป็น \"ไม่ต้องกระทบยอด\" — เปลี่ยนสถานะที่หน้ากระทบยอดธนาคารก่อน");
        if (txn.Status == ReconciliationStatus.Matched)
            return txn.MatchedJournalEntryId == payoutJournalEntryId
                ? new SettlementBankMatchDecision(true, false, false, "รายการเดินบัญชีนี้จับคู่กับ JE รอบโอนไว้แล้ว")
                : No("รายการเดินบัญชีนี้จับคู่กับรายการอื่นไว้แล้ว — ยกเลิกการจับคู่นั้นที่หน้ากระทบยอดธนาคารก่อน");
        return new SettlementBankMatchDecision(true, false, true, "จับคู่ได้");
    }
}
