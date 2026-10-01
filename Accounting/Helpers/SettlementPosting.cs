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

    /// <summary>ป้ายใน <c>Payment.Notes</c> ของการรับชำระที่รอบโอนบันทึก — <b>ข้อความให้คนอ่านอย่างเดียว</b> (รอบ 201 ทีม ST · A-ST1): เจ้าของจริง =
    /// คอลัมน์ <c>Payment.SettlementBatchId</c> (<see cref="SettlementPaymentOwner"/>) · ห้ามอ่านป้ายนี้กลับมาตัดสินอะไร (Notes ผู้ใช้พิมพ์ได้ทุกทางเข้า) ·
    /// ผู้อ่านที่เหลือตัวเดียว = backfill ครั้งเดียวตอนสร้างคอลัมน์ (<see cref="PaymentOwnerBackfillSql"/>)</summary>
    internal static string PaymentMarker(Guid batchId) => PaymentMarkerHead + batchId.ToString("N") + "]";

    /// <summary>ต้นของ <see cref="PaymentMarker"/></summary>
    public const string PaymentMarkerHead = "[SETTLEMENT:";

    /// <summary>ข้อความที่ระบบเขียนต่อจากป้ายเสมอ (<see cref="SettlementDocumentBuilder.ReceiptPayment"/> — รูปแบบเดิมตั้งแต่ 84d47dda) — ส่วนหนึ่งของ "หลักฐานว่าระบบเขียน" ใน backfill</summary>
    public const string PaymentNoteLead = " รับเงินผ่าน ";

    /// <summary>
    /// **backfill เจ้าของการรับชำระจากป้ายเดิม — เฉพาะแถวที่พิสูจน์ได้ว่าระบบเขียน** (รอบ 201 ทีม ST · A-ST1) · เงื่อนไขทุกข้อพร้อมกัน:
    /// (1) ป้ายอยู่<b>ต้น</b> Notes ตามด้วย <see cref="PaymentNoteLead"/> (รูปแบบที่ <see cref="SettlementDocumentBuilder.ReceiptPayment"/> เขียนตั้งแต่วันแรก) ·
    /// (2) id ในป้าย = รอบโอนที่มีจริง<b>ของบริษัทเดียวกัน</b> (นับรอบที่ยกเลิก/ลบแล้วด้วย — ตัวหาของกำพร้าต้องเห็น) · (3) <c>Reference</c> = เลขรอบโอนนั้น ·
    /// (4) ช่องทางชำระ = e-Wallet · (5) มีผังเงินเข้าแทนที่ (ผังพัก) · แถวที่ไม่ผ่านแม้ข้อเดียว = ไม่ใช่ของรอบโอน (ป้ายที่ผู้ใช้พิมพ์เองไม่มีผล) ·
    /// <b>รันครั้งเดียวตอนสร้างคอลัมน์</b> (ผู้เรียกตรวจว่าคอลัมน์ยังไม่มี) ⇒ ป้ายที่พิมพ์หลัง deploy ไม่ถูกนับแม้รูปแบบตรง
    /// </summary>
    public static string PaymentOwnerBackfillSql() =>
        "UPDATE \"Payments\" p SET \"SettlementBatchId\" = b.\"Id\" FROM \"SettlementBatches\" b "
        + "WHERE p.\"SettlementBatchId\" IS NULL AND b.\"CompanyId\" = p.\"CompanyId\" "
        + "AND p.\"Notes\" LIKE ('" + PaymentMarkerHead + "' || replace(b.\"Id\"::text, '-', '') || ']" + PaymentNoteLead + "%') "
        + "AND p.\"Reference\" = b.\"PayoutRef\" "
        + "AND p.\"PaymentMethod\" = " + (int)PaymentMethod.EWallet + " "
        + "AND p.\"OverridePaymentAccountId\" IS NOT NULL;";

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
        SettlementAccountRoles.WhtPayable54 => "ภาษีหัก ณ ที่จ่ายค้างนำส่ง ภ.ง.ด.54 (จ่ายต่างประเทศ ม.70)",
        SettlementAccountRoles.WhtReimbursable => "ภาษีหัก ณ ที่จ่ายรอแพลตฟอร์มคืน",
        _ => role,
    };
}

/// <summary>ใบขายที่แผนจะรับชำระเข้าผังพัก — ข้อเท็จจริงจากฐาน (tenant แล้ว)</summary>
/// <param name="Found">พบใบนี้ในบริษัทนี้ (ไม่พบ = ไม่มี หรือเป็นของบริษัทอื่น)</param>
/// <param name="AlreadyReceived">รอบโอนนี้บันทึกรับชำระใบนี้ไปแล้ว (ลงบัญชีครั้งก่อนค้างครึ่งทาง) ⇒ ข้าม</param>
/// <param name="PendingElsewhere">ยอดที่รอบโอน<b>อื่น</b>ที่ยังไม่ลงบัญชีจับคู่ใบนี้ไว้ (<see cref="SettlementCrossBatchReceipts.PendingElsewhere"/> ·
/// review198-B R-B13) — 0 = ไม่มี/ไม่ได้ตรวจ</param>
/// <param name="PendingPayoutRefs">เลขรอบโอนเหล่านั้น (ข้อความทางไปต่อ)</param>
/// <param name="DocumentWht">ภาษีหัก ณ ที่จ่ายของลูกค้าที่ใบตั้งไว้แต่<b>ยังไม่ถูกบันทึก</b> (<see cref="SettlementReceiptWht.Remaining"/> ของ
/// <c>Document.WithholdingTaxAmount</c> · ยอดค้างของใบสุทธิหลังหักแล้ว) — 0 = ใบไม่มี WHT หรือบันทึกครบจากงวดก่อนแล้ว
/// (ฝ่ายค้านรอบ 200 T-1 · DECISIONS ข้อ 27 · ฝ่ายค้านรอบสอง R2M-13 · <see cref="SettlementReceiptWht"/>)</param>
public sealed record SettlementReceiptTarget(
    Guid DocumentId, bool Found, string? DocumentNumber, DocumentType Type, DocumentStatus Status, decimal BalanceDue, bool AlreadyReceived,
    decimal PendingElsewhere = 0m, IReadOnlyList<string>? PendingPayoutRefs = null, decimal DocumentWht = 0m);

/// <summary>การรับชำระใบขายผ่านรอบโอนจัดการภาษีหัก ณ ที่จ่ายของใบอย่างไร (DECISIONS ข้อ 27)</summary>
public enum SettlementReceiptWhtKind
{
    /// <summary>ใบไม่มี WHT — ส่ง 0 (ไม่มีอะไรให้บันทึก)</summary>
    None = 0,
    /// <summary>รอบโอนรับชำระ<b>ยอดสุทธิที่เหลือครบ</b> ⇒ ผู้ซื้อหัก ณ ที่จ่ายไว้แล้ว — ให้ <c>CreatePaymentAsync</c> หยิบ WHT ที่เหลือของใบ (ตรรกะงวดสุดท้ายเดิม ·
    /// Dr 11910 · ลูกหนี้ใน GL ปิดเต็มก้อน)</summary>
    FinalInstallment = 1,
    /// <summary>ใบมี WHT แต่รอบโอนรับชำระ<b>บางส่วน</b> — ระบบไม่รู้ว่าผู้ซื้อหักส่วนไหนในงวดนี้ ⇒ บล็อกพร้อมทางไปต่อ (ห้ามส่ง 0 เงียบ)</summary>
    Undecidable = 2,
}

/// <summary>
/// **WHT ของใบขายที่รอบโอนรับชำระ** (ฝ่ายค้านรอบ 200 T-1 → DECISIONS ข้อ 27) — เดิมส่ง <c>WithholdingTaxAmount = 0</c> ชัดทุกครั้ง ⇒ ใบที่ตั้ง WHT ลูกค้าไว้
/// รับชำระยอดสุทธิครบผ่านรอบโอน = ใบ "ชำระแล้ว" แต่ลูกหนี้ใน GL ค้างเท่ายอด WHT ถาวร และ 11910 ไม่เคยถูกบันทึก (เครดิตภาษีหาย) · pure
/// <para>เกณฑ์ "ครบ" = เกณฑ์งวดสุดท้ายตัวเดียวกับ <c>DocumentService.CreatePaymentAsync</c> (ยอด + 0.01 ≥ ยอดค้าง)</para>
/// </summary>
public static class SettlementReceiptWht
{
    /// <summary>WHT ของใบที่<b>ยังไม่ถูกบันทึก</b> = WHT ที่ใบตั้งไว้ − ที่บันทึกแล้วจากการรับชำระ/ใบเสร็จงวดก่อน (ไม่ติดลบ) — เป็นอินพุต <c>documentWht</c>
    /// ของ <see cref="Decide"/> (ฝ่ายค้านรอบสอง R2M-13: เดิมส่ง WHT ทั้งใบ ⇒ งวดกลางของใบที่ WHT ถูกหักครบจากงวดก่อนแล้วถูกบล็อก ทั้งที่ส่ง 0 ถูกต้อง) ·
    /// ชุดที่นับ "บันทึกแล้ว" = ชุดเดียวกับเพดานของ <c>DocumentService.CreatePaymentAsync</c> (การรับชำระที่ไม่ถูกลบ + ใบเสร็จ/ใบสำคัญที่อ้างใบนี้ซึ่งไม่ใช่ร่าง/ยกเลิก/ปฏิเสธ)</summary>
    public static decimal Remaining(decimal documentWht, decimal withheldViaPayments, decimal withheldViaReceipts)
        => Math.Max(0m, documentWht - withheldViaPayments - withheldViaReceipts);

    public static SettlementReceiptWhtKind Decide(decimal documentWht, decimal balanceDue, decimal amount)
        => documentWht <= 0m ? SettlementReceiptWhtKind.None
            : amount + 0.01m >= balanceDue ? SettlementReceiptWhtKind.FinalInstallment
            : SettlementReceiptWhtKind.Undecidable;

    /// <summary>ข้อความ + ทางไปต่อเมื่อ <see cref="SettlementReceiptWhtKind.Undecidable"/></summary>
    internal static (string Why, string Next) UndecidableMessage(string? documentNumber, decimal documentWht, decimal balanceDue, decimal amount)
        => ($"ใบ {documentNumber} ตั้งภาษีหัก ณ ที่จ่ายของลูกค้าไว้ {documentWht:N2} แต่รอบโอนรับชำระ {amount:N2} ไม่ครบยอดค้าง {balanceDue:N2} — "
            + "ระบบไม่รู้ว่าผู้ซื้อหัก ณ ที่จ่ายส่วนไหนในงวดนี้ (ส่ง 0 = ลูกหนี้ใน GL ค้างเท่ายอดภาษีและเครดิตภาษีถูกหักหาย)",
            "รับชำระใบนี้ที่หน้าเอกสารเอง (ระบุภาษีหัก ณ ที่จ่ายของงวดนี้ตามหนังสือรับรองของลูกค้า · เลือกรับเข้าผังพักของช่องทาง) "
            + "แล้วผูกบรรทัดขายกับการรับชำระนั้น (ระบบนับว่าอยู่ในผังพักแล้ว ไม่รับชำระซ้ำ) · ถ้าลูกค้าไม่ได้หักจริง ให้แก้ใบถอดภาษีหัก ณ ที่จ่ายก่อน");
}

/// <summary>บรรทัดที่แผนนับว่า "อยู่ในผังพักแล้ว" (อ้าง PaymentIntent/การรับชำระ) — ข้อเท็จจริงว่าเงินก้อนนั้นลงไว้ที่ผังไหนจริง (review198-A R-A1)</summary>
/// <param name="PostedClearingAccountId">ผังที่ขาเงินเข้าของรายการนั้นลงไว้จริง (null = ลงธนาคาร/เงินสด หรือหาไม่เจอ)</param>
/// <param name="SettledElsewhere">ถูกล้างออกจากผังพักไปแล้วด้วยเส้นอื่น (รอบโอน gateway เดิม · อีก batch)</param>
/// <param name="RefundOutcomeUnknown">รายการรับชำระที่การคืนเงินค้าง "ผลไม่แน่ชัด" (<c>PaymentIntent.RefundOutcomeUnknownSince</c> · review198-E2 E2-10)</param>
public sealed record SettlementClearingSource(
    IReadOnlyList<Guid> LineIds, string What, bool Found, Guid? PostedClearingAccountId, bool SettledElsewhere,
    bool RefundOutcomeUnknown = false);

/// <summary>หลักฐานว่ายอดขายที่จะออกใบสรุป "มีเอกสารขายอยู่แล้ว" (review198-A R-A7)</summary>
/// <param name="DistinctConfirmable">true = หลักฐานเป็น "เนื้อหาหน้าตาเหมือนรอบที่ออกใบแรก" (ใบสรุปเพิ่มเติม · T-2) ซึ่งผู้มีสิทธิ์ยืนยันรายบรรทัดได้ว่าเป็นรายการจริง
/// (ฝ่ายค้านรอบสอง R2M-12 · ปัญหา <c>SummarySupplementDuplicate</c>) · false = ออเดอร์/วันเดียวกันมีเอกสารขายแล้วจริง (ยืนยันไม่ได้)</param>
public sealed record SettlementDuplicateSale(IReadOnlyList<Guid> LineIds, string Evidence, bool DistinctConfirmable = false);

/// <summary>ข้อเท็จจริงจากฐานที่ด่านของผู้ลงบัญชีต้องใช้ (ผู้เรียกหาให้ทั้งหมด — ตัวด่านไม่แตะฐาน)</summary>
/// <param name="SummaryAbbreviatedBlock">ผลของ <c>AbbreviatedTaxInvoiceRule.Judge</c> ช่องทางเอกสาร ณ วันที่ใบขายสรุป (ฝ่ายค้าน C-6)</param>
/// <param name="StaleReceipts">การรับชำระที่มีป้ายของรอบโอนแต่ไม่ตรงแผนปัจจุบัน (<see cref="SettlementReceiptReconcile.Stale"/> · C-4)</param>
/// <param name="OrphanArtifacts">เอกสาร/การรับชำระที่การลงบัญชีสร้างให้รอบโอนที่ถูกยกเลิก/ลบแล้วของช่องทางเดียวกัน (C-1(d))</param>
/// <param name="SodSelfApprovalBlocked">ผลของ <see cref="SettlementPostingGate.SodSelfApproval"/> (คำตัดสินเจ้าของข้อ 7)</param>
/// <param name="UnvoidableOrphans">ของกำพร้าที่ระบบยกเลิกไม่ได้จริง (e-Tax ตอบรับ · รายงานล็อก · 50 ทวิ ยื่นแล้ว · ใบที่อ้างมันยกเลิกไม่ได้ —
/// <see cref="SettlementOrphanTriage"/> · review198-S3 S3-6 / S4-1) <b>ที่ยังไม่มีคนรับรู้</b> — บล็อก ทางไปต่อ = รับรู้ของกำพร้า (รอบ 200 DECISIONS ข้อ 10 ·
/// เดิมเตือนเฉย ๆ)</param>
/// <param name="AcknowledgedOrphans">ของกำพร้าที่ยกเลิกไม่ได้จริงและรับรู้แล้ว (ผู้/เวลา/เหตุผล) — แสดง ไม่บล็อก (รอบ 200 DECISIONS ข้อ 10)</param>
/// <param name="OrphanNeedsAction">ของกำพร้าที่ยกเลิกได้เมื่อคนทำขั้นก่อน (ภาษีเดือนที่ประกาศว่ายื่นแล้ว · มีเอกสารอ้าง · §78/1) — บล็อก พร้อมทางไปต่อรายชิ้น
/// (review198-S4 S4-1)</param>
/// <param name="Wallet">ผลของ <see cref="SettlementWalletContinuity.Judge"/> (review198-A R-A12) — null = ไม่ได้ตรวจ</param>
/// <param name="FiledPp36Periods">เดือน ภ.พ.36 ที่ประกาศว่ายื่นแล้ว (review198-C C-11) — null = ไม่ได้ตรวจ</param>
/// <param name="WhtFormType">แบบ ภ.ง.ด. <b>ในประเทศ</b>ตามผู้รับ (ตัวเลือกแบบเดียวกับผู้ออก 50 ทวิ — นิติบุคคล 53 · บุคคลธรรมดา 3 · C-11) ·
/// ขา WHT ผู้ให้บริการต่างประเทศใช้แบบของแผน (ภ.ง.ด.54) เสมอ — ตัดสินที่ <c>SettlementForeignWht.GateWhtForm</c> ตัวเดียว (ฝ่ายค้าน W-1 ·
/// ผู้ลงบัญชีส่งผลของ <c>GateWhtForm</c> มาแล้วก็ได้ — เรียกซ้ำได้ผลเดิม) ·
/// <see cref="FiledWhtPeriods"/> ต้องเป็นเดือนที่ยื่นแล้วของแบบที่ <c>GateWhtForm</c> คืน</param>
/// <param name="Supplementary">ใบสรุปของรอบนี้ที่เป็นใบสรุปเพิ่มเติมของวันเดียวกัน (<see cref="SettlementSummarySupplement.Judge"/> · คำตัดสินรอบ 200 ข้อ 15)</param>
/// <param name="Stock">ลักษณะกิจการเรื่องสต็อก (<see cref="SettlementStock.StanceOf"/> · C-15)</param>
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
    bool SodSelfApprovalBlocked = false,
    IReadOnlyList<string>? UnvoidableOrphans = null,
    IReadOnlyList<SettlementOrphanBlock>? OrphanNeedsAction = null,
    IReadOnlyList<string>? AcknowledgedOrphans = null,
    SettlementWalletContinuityResult? Wallet = null,
    IReadOnlyCollection<(int Year, int Month)>? FiledPp36Periods = null,
    TaxType WhtFormType = TaxType.WithholdingTax53,
    IReadOnlyList<SettlementSupplementarySummary>? Supplementary = null,
    SettlementStockStance Stock = SettlementStockStance.NotChecked);

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

    /// <summary>ทางไปต่อเมื่อใบขายสรุป (ลูกค้าเงินสด) ออกเป็นใบกำกับไม่ได้ — คำตัดสินรอบ 200 ข้อ 16 (O-3): คงบล็อก · ภ.พ.06 ไม่เกี่ยว
    /// (คำตัดสินเจ้าของ 2026-09-28: ภ.พ.06 คุมเฉพาะสลิปเครื่อง POS · <c>AbbreviatedTaxInvoiceRule</c> ช่องทางเอกสาร)</summary>
    public const string SummaryRetailNextStep =
        "ถ้าขายให้ผู้บริโภคทั่วไปผ่าน marketplace (ลักษณะขายปลีก) ให้ติ๊ก \"ประกอบกิจการขายปลีก\" ที่หน้าตั้งค่า → ข้อมูลบริษัท แล้วดูตัวอย่างใหม่ — "
        + "ไม่ต้องขอ ภ.พ.06 (ภ.พ.06 ใช้เฉพาะสลิปจากเครื่อง POS/เครื่องบันทึกการเก็บเงิน) · ระบบไม่ติ๊กแทนเพราะเป็นการประกาศลักษณะกิจการของผู้ใช้ · "
        + "ไม่ใช่ขายปลีก (เช่นผู้ซื้อเป็นผู้ประกอบการ) ⇒ ออกใบกำกับภาษีเต็มรูปรายออเดอร์ (ชื่อ/ที่อยู่ผู้ซื้อ) แล้วจับคู่บรรทัดขายกับใบนั้น "
        + "(ระบบรับชำระเข้าผังพักแทนการออกใบสรุป)";

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
        // review198-C C-11: แบบที่ 50 ทวิ จะเป็นจริง (ผู้รับเงินบุคคลธรรมดา ⇒ ภ.ง.ด.3 · ช่องทางต่างประเทศ ⇒ ภ.ง.ด.54 ทีม W) — เดิมดูแต่ ภ.ง.ด.53
        // ฝ่ายค้าน W-1 (ทีม WF): ตัวตั้งเดียว — ขา ภ.ง.ด.54 แผนเป็นเจ้าของ · ในประเทศ (3/53) ผู้ลงบัญชีเป็นเจ้าของ (ผู้เรียกลืมส่งแบบ ≠ ข้อความผิดแบบ)
        var whtForm = WhtUnissuedCertGate.FormLabel(SettlementForeignWht.GateWhtForm(plan, f.WhtFormType));
        if (whtLegs && f.FiledWhtPeriods.Contains((f.PayoutDay.Year, f.PayoutDay.Month)))
            Add(SettlementPlanIssueCode.TaxPeriodFiled, true,
                $"ภาษีหัก ณ ที่จ่ายของรอบโอนนี้ตกเดือน {f.PayoutDay:MM}/{f.PayoutDay.Year + 543} ที่ยื่น {whtForm} แล้ว",
                $"ยื่น {whtForm} เพิ่มเติมของเดือนนั้น แล้วให้ผู้มีสิทธิ์ปลดล็อกรายงานก่อนลงบัญชี — หรือเปลี่ยนโหมดหัก ณ ที่จ่ายของช่องทางถ้าแพลตฟอร์มเป็นผู้หักแทน");
        // review198-C C-11: ใบค่าธรรมเนียมบริการต่างประเทศตั้ง ภ.พ.36 (21912) ในเดือนวันเงินเข้า — เดือนนั้นยื่น ภ.พ.36 แล้ว = ภาษีตกหล่นของเดือนที่ยื่นแล้ว
        var pp36Amount = plan.FeeDocuments.Where(d => d.VatTreatment is SettlementFeeVatTreatment.SelfAssessedPp36
            or SettlementFeeVatTreatment.SelfAssessedPp36NotClaimable).Sum(d => d.Pp36Payable);
        if (pp36Amount > 0m && f.FiledPp36Periods is { } filedPp36 && filedPp36.Contains((f.PayoutDay.Year, f.PayoutDay.Month)))
            Add(SettlementPlanIssueCode.TaxPeriodFiled, true,
                $"VAT แทนผู้ให้บริการต่างประเทศ {pp36Amount:N2} ของรอบโอนนี้ตกเดือน {f.PayoutDay:MM}/{f.PayoutDay.Year + 543} ที่ยื่น ภ.พ.36 แล้ว",
                "ยื่น ภ.พ.36 เพิ่มเติมของเดือนนั้น (ชำระภาษีที่ตกหล่น) แล้วให้ผู้มีสิทธิ์ปลดล็อกรายงานก่อนลงบัญชี — ระบบไม่เพิ่มยอดเข้าแบบที่ยื่นแล้วเงียบ ๆ",
                null, pp36Amount);

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
            var next = "จับคู่บรรทัดขายกับใบที่ถูกต้อง · ถ้าใบนั้นรับชำระทางอื่นไปแล้ว ให้ผูกบรรทัดกับการรับชำระนั้นแทน (ระบบจะไม่รับชำระซ้ำ)";
            if (t is null || !t.Found) why = "ไม่พบใบขายที่จับคู่ไว้ในบริษัทนี้";
            else if (!ArApScope.IsReceivable(t.Type)) why = $"ใบ {t.DocumentNumber} ไม่ใช่เอกสารตั้งลูกหนี้ (ใบแจ้งหนี้/ใบกำกับ/ใบเพิ่มหนี้)";
            else if (t.Status is not (DocumentStatus.Approved or DocumentStatus.Sent or DocumentStatus.PartiallyPaid or DocumentStatus.Overdue))
                why = $"ใบ {t.DocumentNumber} อยู่สถานะ {t.Status} — รับชำระได้เฉพาะใบที่อนุมัติแล้วและยังค้างชำระ";
            else if (t.BalanceDue + 0.005m < r.Amount)
            {
                why = $"ใบ {t.DocumentNumber} ค้างชำระ {t.BalanceDue:N2} น้อยกว่ายอดที่แพลตฟอร์มโอน {r.Amount:N2} (เคยรับชำระทางอื่นแล้ว?)";
                // T-1: ยอดค้างของใบสุทธิหลัง WHT — เงินเข้าเต็มยอดก่อนหัก = ผู้ซื้อไม่ได้หัก ณ ที่จ่ายจริง
                if (t.DocumentWht > 0m && t.BalanceDue + t.DocumentWht + 0.005m >= r.Amount)
                    next = $"ใบนี้ตั้งภาษีหัก ณ ที่จ่ายของลูกค้าไว้ {t.DocumentWht:N2} แต่เงินเข้าเต็มยอดก่อนหัก — ถ้าผู้ซื้อไม่ได้หักจริง ให้แก้ใบถอดภาษีหัก ณ ที่จ่าย "
                        + "แล้วดูตัวอย่างใหม่ · " + next;
            }
            else if (t.PendingElsewhere > 0m && t.BalanceDue - t.PendingElsewhere + 0.005m < r.Amount)
            {
                // review198-B R-B13: รอบโอนอื่นที่ยังไม่ลงบัญชีจับคู่ใบเดียวกันไว้ — รวมกันเกินยอดค้าง (ด่านบรรทัดบนเห็นแค่การรับชำระที่ลงแล้ว)
                var refs = string.Join(", ", t.PendingPayoutRefs ?? Array.Empty<string>());
                why = $"ใบ {t.DocumentNumber} ค้างชำระ {t.BalanceDue:N2} แต่รอบโอนอื่นที่ยังไม่ลงบัญชี ({refs}) จับคู่ใบนี้ไว้แล้ว {t.PendingElsewhere:N2} "
                    + $"— รวมกับรอบนี้ {r.Amount:N2} เกินยอดค้าง (รับชำระเกิน)";
                next = $"ตรวจว่าออเดอร์เดียวกันอยู่ทั้งในรอบนี้และรอบ {refs} หรือไม่ (นำเข้าซ้ำ/จับคู่ผิดใบ) — ถอดการจับคู่ในรอบที่ผิด "
                    + "หรือยกเลิกรอบโอนที่ซ้ำ · ถ้าเป็นการผ่อนชำระจริงหลายรอบ ยอดรวมทุกรอบต้องไม่เกินยอดค้างของใบ";
            }
            else if (SettlementReceiptWht.Decide(t.DocumentWht, t.BalanceDue, r.Amount) == SettlementReceiptWhtKind.Undecidable)
                (why, next) = SettlementReceiptWht.UndecidableMessage(t.DocumentNumber, t.DocumentWht, t.BalanceDue, r.Amount);
            if (why != null)
                Add(SettlementPlanIssueCode.ReceiptDocumentNotPayable, true, why, next, r.LineIds, r.Amount);
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
                // คำตัดสินรอบ 200 ข้อ 16 (O-3): คงบล็อก — ระบบไม่ประกาศแทนผู้ใช้ว่าเป็นกิจการขายปลีก · ทางไปต่อชัด
                SummaryRetailNextStep,
                plan.SummarySales.SelectMany(s => s.LineIds).ToList(), plan.SummarySales.Sum(s => s.Gross));

        // ── การรับชำระที่ค้างจากครั้งก่อนไม่ตรงแผน (C-4) · ของกำพร้าจากรอบโอนที่ยกเลิกแล้ว (C-1(d)) ──
        foreach (var why in f.StaleReceipts ?? Array.Empty<string>())
            Add(SettlementPlanIssueCode.StaleDocument, true, why,
                "ยกเลิกการรับชำระนั้นที่หน้าเอกสาร (รอบโอนยังไม่ลงบัญชีครบ จึงยกเลิกทีละรายการได้) แล้วดูตัวอย่างใหม่ — ระบบจะรับชำระตามแผนปัจจุบันให้");
        foreach (var why in f.OrphanArtifacts ?? Array.Empty<string>())
            Add(SettlementPlanIssueCode.OrphanPostingArtifacts, true, why,
                "ยกเลิกเอกสาร/การรับชำระเหล่านั้นที่หน้าเอกสารก่อน (รอบโอนเจ้าของถูกยกเลิกแล้ว จึงยกเลิกทีละรายการได้) — ถ้ายังอยู่ ลงบัญชีรอบนี้ทับ "
                + "= ค่าธรรมเนียม/ภาษีซื้อ/รายได้ซ้ำ");
        // S4-1: ของกำพร้าที่ด่านยกเลิกการลงบัญชีปฏิเสธแต่ระบบยกเลิกทีละใบได้เมื่อคนทำขั้นก่อน — ยังบล็อก (ใบค่าธรรมเนียมไม่มีตัวกันซ้ำอื่น) ด้วยทางไปต่อที่ตรงเหตุ
        foreach (var o in f.OrphanNeedsAction ?? Array.Empty<SettlementOrphanBlock>())
            Add(SettlementPlanIssueCode.OrphanPostingArtifacts, true, o.Why, o.NextStep);
        // S3-6 → รอบ 200 (DECISIONS ข้อ 10): ของกำพร้าที่ระบบยกเลิกไม่ได้แล้ว — ไม่มีทางยกเลิก แต่ก็ห้ามผ่านเงียบ (ใบค่าธรรมเนียมไม่มีตัวกันซ้ำอื่น) ⇒
        // บล็อกจนกว่าผู้มีสิทธิ์ลงบัญชีจะ "รับรู้ของกำพร้า" พร้อมเหตุผล (ธงบนเอกสาร + audit) · รับรู้แล้ว ⇒ แสดงผู้รับรู้ ไม่บล็อกช่องทางนี้อีก
        foreach (var why in f.UnvoidableOrphans ?? Array.Empty<string>())
            Add(SettlementPlanIssueCode.OrphanPostingArtifacts, true, why, SettlementOrphanTriage.UnacknowledgedNextStep);
        foreach (var why in f.AcknowledgedOrphans ?? Array.Empty<string>())
            Add(SettlementPlanIssueCode.OrphanPostingArtifacts, false, why, SettlementOrphanTriage.AcknowledgedNextStep);

        // ── รายได้ซ้ำ (R-A7) ──
        foreach (var d in f.DuplicateSales)
            if (d.DistinctConfirmable)
                // R2M-12: เนื้อหาหน้าตาเหมือนรอบแรก — ทางไปต่อสองทาง (ยกเลิกรอบ · ยืนยันรายบรรทัด) · รหัสแยกให้หน้าจอแสดงปุ่มยืนยันเฉพาะกองนี้
                Add(SettlementPlanIssueCode.SummarySupplementDuplicate, true, d.Evidence, SettlementSummarySupplement.DistinctConfirmNextStep,
                    d.LineIds, null);
            else
                Add(SettlementPlanIssueCode.SummarySaleDuplicate, true, d.Evidence,
                    "ถ้าเป็นออเดอร์ชุดเดียวกัน (มีเอกสารขายแล้ว) ให้จับคู่บรรทัดกับเอกสารนั้นแทน — ระบบจะรับชำระเข้าผังพักให้ ไม่ออกใบสรุปซ้ำ · "
                    + "ถ้าเป็นเอกสารของรอบโอนที่ยกเลิกแล้ว ให้ยกเลิกเอกสารนั้นก่อน (หรือจับคู่บรรทัดกับเอกสารนั้นถ้าเป็นยอดขายชุดเดียวกัน)",
                    d.LineIds, null);

        // ── ใบสรุปเพิ่มเติมของวันเดียวกัน (คำตัดสินรอบ 200 ข้อ 15 · review198-C C-9) — แทนการบล็อก SummarySaleDuplicate ──
        foreach (var sup in f.Supplementary ?? Array.Empty<SettlementSupplementarySummary>())
        {
            var gross = plan.SummarySales.Where(x => x.Date == sup.Day).Sum(x => x.Gross);
            if (!sup.FirstIssued)
                Add(SettlementPlanIssueCode.SummarySaleFirstNotIssued, true,
                    $"วันที่ {ThaiDate.ToThaiDisplayString(sup.Day)} มีใบขายสรุปจากรอบโอน {sup.FirstPayoutRef} แล้วแต่ยังไม่ออกเลข (ลงบัญชีรอบนั้นค้างครึ่งทาง) — "
                    + "ใบสรุปเพิ่มเติมของรอบนี้ต้องอ้างเลขใบแรกของวัน",
                    $"กด \"ลงบัญชี\" ที่รอบโอน {sup.FirstPayoutRef} ให้เสร็จก่อน (ระบบทำต่อจากขั้นที่ค้าง) แล้วดูตัวอย่างรอบนี้ใหม่ · "
                    + $"ถ้ารอบ {sup.FirstPayoutRef} ลงบัญชีต่อไม่ได้ (ติดด่านอื่น) ให้ลบร่าง {sup.FirstNumber} ที่หน้าเอกสาร หรือยกเลิกรอบโอน {sup.FirstPayoutRef} "
                    + "— ใบสรุปของรอบนี้จะเป็นใบแรกของวันแทน (รอบนั้นลงทีหลังจะออกเป็นใบสรุปเพิ่มเติม)",
                    sup.LineIds, gross);
            else
                Add(SettlementPlanIssueCode.SummarySaleSupplementary, false,
                    $"ใบขายสรุปวันที่ {ThaiDate.ToThaiDisplayString(sup.Day)} ยอด {gross:N2} จะออกเป็นใบสรุปเพิ่มเติม (เลขใหม่ · อ้างใบแรกของวัน {sup.FirstNumber} "
                    + $"จากรอบโอน {sup.FirstPayoutRef} · จุดความรับผิดวันเดิม) — แพลตฟอร์มโอนออเดอร์ของวันเดียวกันแยกหลายรอบ",
                    "ลงบัญชีได้ · ตรวจว่าออเดอร์ในรอบนี้ไม่ใช่ชุดเดียวกับใบแรก (เมื่อไฟล์มีเลขออเดอร์ ระบบกันออเดอร์ที่ลงแล้ว/มีเอกสารของตัวเองให้แล้ว)",
                    sup.LineIds, gross);
        }

        // ── ใบสรุปไม่ตัดสต็อก/ต้นทุนขาย (review198-C C-15) — เดิมบอกแค่ใน InternalNotes ที่ผู้กดลงบัญชีไม่เห็น ──
        if (plan.SummarySales.Count > 0 && f.Stock is SettlementStockStance.KeepsInventory or SettlementStockStance.Unknown)
            Add(SettlementPlanIssueCode.SummarySaleNoStock, false,
                "ใบขายสรุปรายวันไม่ตัดสต็อกและไม่ลงต้นทุนขาย (ไฟล์รอบโอนไม่มีรายการสินค้า) — "
                + (f.Stock == SettlementStockStance.KeepsInventory
                    ? "กิจการนี้ถือสต็อก ⇒ สินค้าคงเหลือจะสูงเกินและต้นทุนขายต่ำเกินเท่ามูลค่าของที่ขายผ่านแพลตฟอร์ม"
                    : "ยังไม่ได้ระบุประเภทธุรกิจของบริษัท ระบบจึงไม่รู้ว่ากิจการถือสต็อกหรือไม่"),
                f.Stock == SettlementStockStance.KeepsInventory
                    ? "ลงบัญชีได้ · ตัดสต็อกของที่ขายผ่านแพลตฟอร์มด้วยการนำเข้าคำสั่งซื้อรายออเดอร์ (มีรายการสินค้า) หรือบันทึกเบิกสินค้า/ต้นทุนขายตามรายงานยอดขายของแพลตฟอร์ม"
                    : "ลงบัญชีได้ · ตั้งประเภทธุรกิจที่หน้าตั้งค่า → ข้อมูลบริษัท (กิจการบริการ ⇒ คำเตือนนี้หายไป · กิจการถือสต็อก ⇒ ตัดสต็อกตามรายงานยอดขาย)",
                plan.SummarySales.SelectMany(s => s.LineIds).ToList(), plan.SummarySales.Sum(s => s.Gross));

        // ── ยอด wallet ต่อเนื่องข้ามรอบโอน (review198-A R-A12) — สมการภายในรอบเป็นจริงได้ตามนิยามถ้ายอดยกมาถูกกรอกให้พอดี ──
        if (f.Wallet is { } w)
        {
            if (w.Kind == SettlementWalletContinuityKind.Gap && w.Previous is { } prev)
                Add(SettlementPlanIssueCode.WalletContinuityGap, true,
                    $"ยอด wallet ต้นรอบ {w.Opening:N2} ไม่เท่ายอดปลายรอบของรอบโอนก่อนหน้า {prev.PayoutRef} "
                    + $"({ThaiDate.ToThaiDisplayString(prev.PayoutDate)}) {prev.ClosingWalletBalance:N2} — ต่างกัน {w.Difference:N2} บาท "
                    + "· ลงบัญชีทั้งที่ยอดไม่ต่อเนื่อง = ผังพักคลาดจาก wallet จริงถาวร",
                    "ตรวจกับสเตทเมนต์ของผู้ให้บริการ: ถ้ามีรอบโอนที่ยังไม่ได้นำเข้าระหว่างสองรอบนี้ ให้นำเข้าก่อน · ถ้ายอดต้นรอบที่กรอกผิด ให้ยกเลิกรอบโอนนี้แล้วนำเข้าใหม่ "
                    + "(ยอดต้นรอบ = ยอดปลายรอบก่อน) · ถ้ามีรายการที่แพลตฟอร์มหัก/เติมนอกรอบโอนจริง ให้นำเข้าใหม่ด้วยยอดต้นรอบ = ยอดปลายรอบก่อน "
                    + "แล้วใส่รายการนั้นเป็นบรรทัด \"ปรับปรุงอื่น\" พร้อมเหตุผล (ผังพักจะตรง wallet จริง) · "
                    + "รอบโอนวันเดียวกันหลายรอบ: ระบบเทียบกับรอบของวันนั้นที่ยอดปลายรอบตรงกับยอดต้นรอบนี้ให้เอง (ไม่ขึ้นกับลำดับที่นำเข้า) — "
                    + "ยังขึ้นคำเตือนนี้ = ไม่มีรอบใดของวันนั้นที่ปลายรอบเท่ายอดต้นรอบนี้",
                    null, w.Difference);
            else if (w.Kind == SettlementWalletContinuityKind.PreviousHadNoBalances && w.Previous is { } blank)
                Add(SettlementPlanIssueCode.WalletContinuityUnknown, false,
                    $"รอบโอนก่อนหน้า {blank.PayoutRef} ({ThaiDate.ToThaiDisplayString(blank.PayoutDate)}) ไม่มียอด wallet (ต้น/ปลายรอบ 0/0 — ไฟล์รุ่นเก่าไม่มียอด) "
                    + $"จึงเทียบยอดต้นรอบ {w.Opening:N2} ของรอบนี้ไม่ได้",
                    "ตรวจยอดต้นรอบกับสเตทเมนต์ของผู้ให้บริการก่อนลงบัญชี — ถ้ายอด wallet ต้นรอบนี้มาจากรายการก่อนเริ่มนำเข้ายอด ให้ผู้ทำบัญชีตั้งยอดยกมาของผังพักให้ตรง "
                    + "(รอบถัดไประบบเทียบยอดต่อเนื่องให้เอง)",
                    null, w.Opening);
            else if (w.Kind == SettlementWalletContinuityKind.NoPreviousBatch)
                Add(SettlementPlanIssueCode.WalletContinuityUnknown, false,
                    $"รอบโอนแรกของช่องทางนี้ในระบบ — ยอด wallet ต้นรอบ {w.Opening:N2} เทียบกับรอบก่อนไม่ได้",
                    "ตรวจยอดต้นรอบกับสเตทเมนต์ของผู้ให้บริการก่อนลงบัญชี (รอบถัดไประบบจะเทียบยอดต้นรอบกับยอดปลายรอบนี้ให้เอง)",
                    null, w.Opening);
        }

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
    /// <para>รอบ 201 ทีม TX (A-TX3 · คำตัดสินข้อ 45): สูตรย้ายไป <see cref="ApprovalControlPolicy.SelfApproval"/> ตัวเดียว (เส้นอนุมัติเอกสารถามตัวเดียวกัน) ·
    /// ที่นี่เลือกนโยบายของรอบโอน = "ไม่รู้" บล็อก (<see cref="ApprovalControlPolicy.BlocksSettlementPosting"/>)</para>
    /// </summary>
    public static bool SodSelfApproval(bool sodBlockSelfApproval, string? batchCreatedBy, Guid postingUserId)
    {
        return ApprovalControlPolicy.BlocksSettlementPosting(
            ApprovalControlPolicy.SelfApproval(sodBlockSelfApproval, batchCreatedBy, null, postingUserId.ToString()));
    }

    /// <summary>
    /// **ผู้ทำของรอบโอน = ผู้สร้างรอบ + ผู้ที่เติมไฟล์เข้ารอบเดิม** (review198-S3 S3-11 · รอบ 200 ทีม V2) — เดิมเทียบแค่ <c>SettlementBatch.CreatedBy</c> ⇒
    /// คนที่นำเข้าไฟล์ที่สองเข้ารอบเดิม (บรรทัดของเขาอยู่ในใบค่าธรรมเนียม/ใบสรุปที่ระบบออก) กดลงบัญชีเองได้ทั้งที่บริษัทเปิดแยกหน้าที่ ·
    /// ผู้สร้างบรรทัดที่ว่าง (แถวเก่า) ไม่นับ — ตัวตัดสิน "ผู้สร้างรอบไม่รู้ = บล็อก" ของรูปสามอาร์กิวเมนต์คงเดิม ·
    /// รอบ 201 ทีม ST (A-ST7): ผู้ตัดสินการจับคู่/จัดประเภทรายบรรทัด (<c>SettlementLine.DecidedBy</c>) ถูกบันทึกแล้ว และผู้เรียกส่งรวมมาใน
    /// <paramref name="lineCreators"/> (<c>SettlementLineMakers.Of</c>) — นับเป็นผู้ทำด้วย (เดิมเขียนไว้ว่ายังไม่ถูกบันทึก — ไม่จริงแล้ว) ·
    /// สูตร SoD อยู่ที่ <see cref="ApprovalControlPolicy.SelfApproval"/> ตัวเดียว (รอบ 201 ทีม TX · A-TX3)
    /// </summary>
    public static bool SodSelfApproval(bool sodBlockSelfApproval, string? batchCreatedBy, IEnumerable<string?> lineCreators, Guid postingUserId)
    {
        return ApprovalControlPolicy.BlocksSettlementPosting(
            ApprovalControlPolicy.SelfApproval(sodBlockSelfApproval, batchCreatedBy, lineCreators, postingUserId.ToString()));
    }

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

    /// <param name="supplementOf">เลขใบขายสรุปใบแรกของวันเดียวกัน (จากรอบโอนอื่น) — มีค่า = ใบนี้เป็นใบสรุปเพิ่มเติม (คำตัดสินรอบ 200 ข้อ 15):
    /// บรรทัด/หมายเหตุบนใบอ้างใบแรก · วันที่และจุดความรับผิดยังเป็นวันขายเดิม</param>
    public static CreateDocumentRequest SummaryDocument(SettlementSummarySalePlan s, bool vatRegistered, Guid walkInContactId,
        Guid clearingAccountId, string payoutRef, string channelName, string? supplementOf = null)
    {
        var supplement = string.IsNullOrWhiteSpace(supplementOf) ? null : supplementOf.Trim();
        var desc = (supplement is null ? "ยอดขายผ่าน" : "ยอดขายเพิ่มเติมผ่าน")
            + $" {channelName} ประจำวันที่ {ThaiDate.ToThaiDisplayString(s.Date)} ({s.LineIds.Count} รายการ)"
            + (supplement is null ? "" : $" · ใบสรุปเพิ่มเติมของวันเดียวกัน ต่อจากใบ {supplement}")
            + (s.SellerVoucher != 0m ? $" · หักส่วนลดร้าน {-s.SellerVoucher:N2}" : "")
            + (s.PlatformVoucher != 0m ? $" · รวมโค้ดส่วนลดที่แพลตฟอร์มออกเงิน {s.PlatformVoucher:N2}" : "");
        var line = s.Vat > 0m
            ? new DocumentLineRequest(desc, 1m, null, s.Net, 0m, PartnerVatRate.StatutoryRate, 0m, null, VatAmountOverride: s.Vat)
            : new DocumentLineRequest(desc, 1m, null, s.Net, 0m, 0m, 0m, null);
        return new CreateDocumentRequest(
            vatRegistered ? DocumentType.TaxInvoice : DocumentType.Receipt,
            s.Date, null, walkInContactId, payoutRef,
            $"สรุปยอดขายผ่าน {channelName} ประจำวันที่ {ThaiDate.ToThaiDisplayString(s.Date)} · รอบโอน {payoutRef}"
                + (supplement is null ? "" : $" · ใบสรุปเพิ่มเติม (ใบแรกของวัน: {supplement})"),
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

    /// <summary>รับชำระใบขายที่จับคู่ได้ — เงินเข้า = ผังพัก · ใบเสร็จตามค่าเดิมของเส้นรับชำระ · ป้ายของรอบโอนใน Notes (บันทึกพร้อมการรับชำระ)
    /// <para>WHT (ฝ่ายค้านรอบ 200 T-1 → DECISIONS ข้อ 27 · <see cref="SettlementReceiptWht"/>): ใบไม่มี WHT ⇒ 0 · รับยอดสุทธิที่เหลือครบ ⇒ <c>null</c> =
    /// <c>CreatePaymentAsync</c> หยิบ WHT ที่เหลือของใบตามตรรกะงวดสุดท้ายเดิม (Dr 11910 · ลูกหนี้ใน GL ปิด) · รับบางส่วนของใบที่มี WHT ⇒ <b>ห้ามสร้าง</b>
    /// (ด่านบล็อกไว้แล้ว — ถึงนี่ได้ = ข้อมูลเปลี่ยนระหว่างพรีวิวกับลงบัญชี ⇒ ล้มดัง) · เดิม (C-12) ส่ง 0 ทุกครั้ง ⇒ ลูกหนี้ค้างเท่ายอด WHT เงียบ</para></summary>
    public static CreatePaymentRequest ReceiptPayment(SettlementReceiptPlan r, Guid batchId, Guid clearingAccountId,
        DateTime payoutDay, string payoutRef, string channelName, SettlementReceiptWhtKind wht)
    {
        if (wht == SettlementReceiptWhtKind.Undecidable)
            throw new BusinessRuleException(
                "ใบขายที่ตั้งภาษีหัก ณ ที่จ่ายของลูกค้าไว้ถูกรับชำระบางส่วนผ่านรอบโอน — ระบบไม่รู้ว่าผู้ซื้อหักส่วนไหนในงวดนี้ · "
                + "ดูตัวอย่างการลงบัญชีใหม่แล้วทำตามทางไปต่อ", "SETTLEMENT-RECEIPT-WHT");
        return new(r.DocumentId, payoutDay, r.Amount, PaymentMethod.EWallet, payoutRef, null,
            $"{SettlementPostingKeys.PaymentMarker(batchId)}{SettlementPostingKeys.PaymentNoteLead}{channelName} รอบโอน {payoutRef} (เงินเข้าผังพักของแพลตฟอร์ม — ยังไม่เข้าธนาคาร)",
            OverridePaymentAccountId: clearingAccountId,
            WithholdingTaxAmount: wht == SettlementReceiptWhtKind.FinalInstallment ? (decimal?)null : 0m);
    }

    /// <summary>50 ทวิ ของ WHT ที่ JE รอบโอนตั้ง 21917 (W2 หักเองแล้วแพลตฟอร์มคืน · W3 ออกภาษีแทน) — null = ไม่ต้องออก
    /// (None · W1 แพลตฟอร์มเป็นตัวแทนหัก/ยื่นแทน — ห้ามนับเข้ายอดที่เรายื่นเอง) · ยอด = ชุดบรรทัดเดียวกับขา 21917 (WhtAmount &gt; 0) ·
    /// แบบ ภ.ง.ด. = <c>fee.WhtForm</c> ของแผน (ผู้ให้บริการต่างประเทศ = ภ.ง.ด.54 · ขา 21918 · ทีม W)</summary>
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
        return new CreateWithholdingTaxCertRequest(counterpartyContactId, fee.WhtForm,
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
