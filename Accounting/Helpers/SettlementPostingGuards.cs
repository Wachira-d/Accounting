using System.Globalization;
using Accounting.Data;
using Accounting.Models.DTOs.Tax;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

// ═══════════════════════════════════════════════════════════════════════
// Settlement — ด่านกันลงบัญชี/ยกเลิกครึ่งทาง (รอบ 198 ทีม S3 · ฝ่ายค้าน review198-B R-B3 + review198-C C-1..C-5)
// ตัวตัดสินบริสุทธิ์ทั้งหมด (+ ตัวโหลดข้อเท็จจริงที่แตะฐานเท่าที่จำเป็น) — service แค่หาข้อเท็จจริงแล้วทำตาม ·
// จุดเรียกใน service ล็อกด้วย tools/required_call_site_check.py
// ═══════════════════════════════════════════════════════════════════════

/// <summary>
/// **ล็อกต่อช่องทาง settlement ตัวเดียว** ของทั้งการนำเข้า/แก้บรรทัด/ยกเลิกรอบโอน (ทีม B) และการลงบัญชี/ยกเลิกการลงบัญชี/
/// จับคู่ธนาคาร/ปิด chargeback (ทีม C) — ฝ่ายค้าน C-1: เดิมสองฝั่งใช้คนละคีย์ (<c>settlement-import</c> กับ <c>settle-post</c>) ⇒ ยกเลิก
/// รอบโอนแทรกกลางการลงบัญชีได้ แล้วเอกสารที่ออกแล้วกลายเป็นของกำพร้า
/// <para>ฝั่งนำเข้าถือแบบธุรกรรม (<c>pg_advisory_xact_lock(Key)</c>) · ฝั่งลงบัญชีถือแบบ session ผ่าน <see cref="JobLock.RunExclusiveAsync"/>
/// ด้วย <see cref="Scope"/> + <see cref="Part"/> ⇒ คีย์ตัวเลขเดียวกันเป๊ะ (<see cref="AdvisoryLockKey.For(Guid, string, string)"/>) — PostgreSQL
/// ให้ session lock กับ xact lock ของคีย์เดียวกันกันกันเอง · ทุกเส้นที่แก้ข้อมูลของรอบโอน<b>ล็อกก่อนแล้วค่อยโหลด/ตรวจ</b> (R-B3)</para>
/// </summary>
public static class SettlementChannelLock
{
    /// <summary>scope เดียวของทั้งสองฝั่ง</summary>
    public const string Scope = AdvisoryLockKey.SettlementImport;

    /// <summary>ส่วนของคีย์ = ช่องทาง</summary>
    public static string Part(Guid channelId) => channelId.ToString("N");

    /// <summary>คีย์ตัวเลข — ตัวเดียวกับที่ <see cref="JobLock.RunExclusiveAsync"/> คำนวณจาก <see cref="Scope"/> + <see cref="Part"/></summary>
    public static long Key(Guid companyId, Guid channelId) => AdvisoryLockKey.For(companyId, Scope, Part(channelId));
}

/// <summary>การรับชำระ 1 รายการที่มีป้ายของรอบโอน (<see cref="SettlementPostingKeys.PaymentMarker"/>) — ข้อเท็จจริงจากฐาน (tenant แล้ว)</summary>
public sealed record SettlementMarkerPayment(Guid Id, Guid DocumentId, decimal Amount, string? Number);

/// <summary>เอกสาร 1 ใบที่มีป้ายของรอบโอน (<see cref="SettlementPostingKeys.CreatorPrefix"/>) — ยังไม่ถูกยกเลิก</summary>
public sealed record SettlementPostedDocumentFact(string Component, string Number, DocumentStatus Status, decimal Total);

/// <summary>
/// **การรับชำระที่ค้างจากการลงบัญชีครั้งก่อนตรงกับแผนปัจจุบันไหม** (ฝ่ายค้าน C-4) — เดิมตรวจแค่ "มีการรับชำระบนใบนี้แล้ว" ⇒
/// ผู้ใช้จับคู่ใหม่ไปใบอื่น/เพิ่มบรรทัดของออเดอร์เดียวกันหลังล้มครึ่งทาง ⇒ การรับชำระเดิมค้างอยู่ (ผังพักคลาดเท่ายอดนั้น) แต่ Posted ถูกประทับ
/// <para>ทุกการรับชำระที่มีป้ายต้องอยู่ในแผน (ใบเดียวกัน · ยอดรวมต่อใบเท่ากัน ±0.005) — ไม่ตรง ⇒ ข้อความ 1 ข้อต่อใบ (ผู้เรียกบล็อกเป็น StaleDocument)</para>
/// </summary>
public static class SettlementReceiptReconcile
{
    public static IReadOnlyList<string> Stale(IReadOnlyList<SettlementReceiptPlan> planned, IReadOnlyList<SettlementMarkerPayment> payments)
    {
        var want = planned.GroupBy(r => r.DocumentId).ToDictionary(g => g.Key, g => g.Sum(r => r.Amount));
        var result = new List<string>();
        foreach (var g in payments.GroupBy(p => p.DocumentId))
        {
            var numbers = string.Join(", ", g.Select(p => p.Number ?? p.Id.ToString("N")[..8]));
            var have = g.Sum(p => p.Amount);
            if (!want.TryGetValue(g.Key, out var plannedAmount))
                result.Add($"การรับชำระ {numbers} ({have:N2}) จากการลงบัญชีครั้งก่อนไม่อยู่ในแผนปัจจุบัน (บรรทัดถูกจับคู่ใหม่หลังลงบัญชีค้างครึ่งทาง)");
            else if (Math.Abs(have - plannedAmount) > 0.005m)
                result.Add($"การรับชำระ {numbers} จากการลงบัญชีครั้งก่อนยอด {have:N2} ไม่เท่าแผนปัจจุบัน {plannedAmount:N2} ของใบเดียวกัน");
        }
        return result;
    }
}

/// <summary>
/// **ทุกชิ้นของแผนมีอยู่จริงและออกแล้วก่อนประทับ Posted** (ฝ่ายค้าน C-5 · ราก R1 "สถานะปลายทางที่ระบบประทับเอง") — เดิมขั้นสุดท้ายแค่
/// "เก็บเอกสาร/การรับชำระที่มีอยู่" ⇒ ใบที่ถูกยกเลิกผ่านหน้าเอกสารปกติระหว่างนั้นหายไปจากรอบโอนที่ขึ้นว่าลงบัญชีครบแล้ว
/// <para>คืนรายการสิ่งที่ขาด (ว่าง = ครบ): ชิ้นของแผนแต่ละชิ้นต้องมีเอกสาร<b>ใบเดียว</b>ที่ออกแล้ว ยอดเท่ากับยอดที่สร้าง/รับมาในรอบนี้ ·
/// ใบรับชำระตามแผนต้องมีการรับชำระที่มีป้ายยอดรวมเท่ากัน · มีชิ้นเกินแผน = ไม่ครบเช่นกัน</para>
/// </summary>
public static class SettlementPostingCompleteness
{
    public static IReadOnlyList<string> Missing(
        SettlementPostingPlan plan,
        IReadOnlyDictionary<string, decimal> expectedDocumentTotals,
        IReadOnlyList<SettlementPostedDocumentFact> documents,
        IReadOnlyList<SettlementMarkerPayment> payments)
    {
        var missing = new List<string>();
        var planned = plan.FeeDocuments.Select(f => SettlementPostingKeys.FeeComponent(f.VatTreatment))
            .Concat(plan.SummarySales.Select(s => SettlementPostingKeys.SummaryComponent(s.Date))).ToList();
        foreach (var component in planned)
        {
            var docs = documents.Where(d => d.Component == component).ToList();
            if (docs.Count == 0) { missing.Add($"ไม่พบเอกสารของชิ้น {component} (ถูกยกเลิก/ลบระหว่างลงบัญชี?)"); continue; }
            if (docs.Count > 1) { missing.Add($"ชิ้น {component} มีเอกสารมากกว่า 1 ใบ ({string.Join(", ", docs.Select(d => d.Number))})"); continue; }
            var d = docs[0];
            if (!DocumentStatusRules.IsEffective(d.Status))
                missing.Add($"เอกสาร {d.Number} ของชิ้น {component} ยังไม่ออก (สถานะ {d.Status})");
            if (!expectedDocumentTotals.TryGetValue(component, out var expected))
                missing.Add($"เอกสาร {d.Number} ของชิ้น {component} ไม่ได้ถูกตรวจยอดกับแผนในการลงบัญชีครั้งนี้");
            else if (Math.Abs(d.Total - expected) > 0.005m)
                missing.Add($"เอกสาร {d.Number} ยอด {d.Total:N2} ไม่เท่าแผน {expected:N2}");
        }
        foreach (var extra in documents.Where(d => !planned.Contains(d.Component)))
            missing.Add($"เอกสาร {extra.Number} ({extra.Component}) ไม่อยู่ในแผน");
        missing.AddRange(SettlementReceiptReconcile.Stale(plan.Receipts, payments));
        var paid = payments.Select(p => p.DocumentId).ToHashSet();
        foreach (var r in plan.Receipts.Where(r => !paid.Contains(r.DocumentId)))
            missing.Add($"ยังไม่มีการรับชำระของใบขายที่จับคู่ ยอด {r.Amount:N2}");
        return missing;
    }
}

/// <summary>
/// **ลายนิ้วมือของแผนลงบัญชี** (ฝ่ายค้าน C-1(c)) — ขั้นสุดท้ายคิดแผนใหม่ใต้ล็อกแถว (<c>FOR UPDATE</c>) แล้วเทียบกับแผนที่ด่านใช้ตอนเริ่ม ·
/// ต่าง = บรรทัดถูกแก้ระหว่างลงบัญชี ⇒ ห้ามประทับ Posted (ตาข่ายชั้นที่สองหลังล็อกต่อช่องทางตัวเดียว) · ไม่รวมปัญหา (ด่านเติมทีหลัง)
/// </summary>
public static class SettlementPlanFingerprint
{
    public static string Of(SettlementPostingPlan p)
    {
        static string M(decimal v) => v.ToString("0.00", CultureInfo.InvariantCulture);
        var parts = new List<string>
        {
            "net=" + M(p.NetPayout), "lines=" + M(p.LinesTotal), "clr=" + p.ClearingAccountId, "bank=" + p.BankAccountId,
        };
        parts.AddRange(p.PayoutJournal.Select(j => $"je:{j.AccountRole}:{j.AccountId}:{j.DefaultAccountCode}:{M(j.Debit)}:{M(j.Credit)}"));
        foreach (var f in p.FeeDocuments)
        {
            parts.Add($"fee:{f.VatTreatment}:{f.WhtMode}:{M(f.Deducted)}:{M(f.Expense)}:{M(f.InputVat)}:{M(f.Pp36Payable)}:{M(f.WhtAmount)}");
            parts.AddRange(f.Lines.Select(l => $"fl:{l.LineType}:{l.AccountId}:{l.DefaultAccountCode}:{M(l.Deducted)}:{M(l.Expense)}:{M(l.WhtAmount)}"));
        }
        parts.AddRange(p.Receipts.Select(r => $"rc:{r.DocumentId}:{M(r.Amount)}"));
        parts.AddRange(p.SummarySales.Select(s => $"sum:{s.Date:yyyyMMdd}:{M(s.Gross)}:{M(s.Net)}:{M(s.Vat)}:{s.LineIds.Count}"));
        parts.AddRange(p.Refunds.Select(r => $"rf:{r.DocumentId}:{M(r.Amount)}"));
        return string.Join("|", parts);
    }
}

/// <summary>ขั้นถัดไปของ 50 ทวิ ของใบค่าธรรมเนียมเมื่อลงบัญชีต่อจากที่ค้าง</summary>
public enum SettlementWhtCertStep
{
    /// <summary>ยังไม่มี ⇒ สร้าง + ออก</summary>
    Create = 1,
    /// <summary>มีร่างค้าง (ล้มระหว่างสร้างกับออก) ⇒ ออกใบร่างนั้น</summary>
    IssueDraft = 2,
    /// <summary>ออกแล้วยอดตรง ⇒ ไม่ต้องทำอะไร</summary>
    Done = 3,
    /// <summary>ยอดไม่ตรงแผน ⇒ ล้มดัง (ยกเลิกหนังสือรับรองนั้นก่อน)</summary>
    Stale = 4,
}

/// <summary>
/// **50 ทวิ ที่ค้างเป็นร่างต้องถูกออกตอนลงบัญชีต่อ** (ฝ่ายค้าน C-3) — เดิมด่านกันสร้างซ้ำถาม "มีใบที่ไม่ถูกยกเลิกไหม" ⇒ ร่างที่ค้าง
/// (ล้มระหว่าง Create กับ Issue) นับว่ามีแล้ว ⇒ ไม่เคยถูกออก ⇒ ไม่เข้า ภ.ง.ด.53/ยอดนำส่ง (<see cref="WhtCertFilingScope.Filed"/>) ทั้งที่ JE ตั้ง 21917 แล้ว
/// </summary>
public static class SettlementWhtCertResume
{
    public static SettlementWhtCertStep Decide(WithholdingTaxCertStatus? existingStatus, decimal? existingTotal, decimal expectedTotal)
    {
        if (existingStatus is null) return SettlementWhtCertStep.Create;
        if (existingTotal is decimal t && Math.Abs(t - expectedTotal) > 0.005m) return SettlementWhtCertStep.Stale;
        return existingStatus == WithholdingTaxCertStatus.Draft ? SettlementWhtCertStep.IssueDraft : SettlementWhtCertStep.Done;
    }
}

/// <summary>เอกสาร 1 ใบของรอบโอนที่จะถูกยกเลิกตอนยกเลิกการลงบัญชี — ข้อเท็จจริงจากฐาน (tenant แล้ว)</summary>
/// <param name="Component">ชิ้นของแผน (<c>fee-…</c> · <c>sum-yyyyMMdd</c>)</param>
/// <param name="EtaxAccepted">e-Tax ของใบนี้ได้รับตอบรับจากกรมสรรพากรแล้ว (Accepted)</param>
/// <param name="InLockedReport">อยู่ในรายงานภาษีที่ล็อกการยื่นแล้ว (<c>FilingLockedAt</c>)</param>
public sealed record SettlementUnpostDocument(
    Guid Id, string Number, DocumentType Type, string Component, DateTime DocumentDate, decimal VatAmount,
    bool IsForeignService, bool EtaxAccepted, bool InLockedReport);

/// <summary>50 ทวิ ที่ผูกกับเอกสารของรอบโอน (ยังไม่ถูกยกเลิก)</summary>
public sealed record SettlementUnpostCertificate(
    Guid Id, Guid DocumentId, string? Number, TaxType FormType, int Year, int Month, WithholdingTaxCertStatus Status);

/// <summary>เหตุผลที่ยกเลิกการลงบัญชีไม่ได้ 1 ข้อ — พร้อมทางไปต่อ</summary>
public sealed record SettlementUnpostRefusal(string Subject, string Reason, string NextStep);

/// <summary>
/// **ด่านก่อนยกเลิกการลงบัญชีรอบโอน — ตรวจทุกชิ้นก่อนแตะชิ้นแรก** (ฝ่ายค้าน C-2)
///
/// <para>ที่มา: เดิมตรวจแค่งวดบัญชีที่ปิด แล้วยกเลิกการรับชำระ → 50 ทวิ → เอกสาร (ลำดับไม่แน่นอน) ⇒ ใบขายสรุปที่ e-Tax ตอบรับแล้วถูกปฏิเสธ
/// <b>กลางทาง</b> หลังการรับชำระ/ใบค่าธรรมเนียมถูกกลับรายการไปแล้ว ⇒ รอบโอนค้าง "ลงบัญชีแล้ว" · JE รอบโอนยังอยู่ · ผังพักคลาด ·
/// กดซ้ำล้มที่ใบเดิมทุกครั้ง · และ ภ.พ.30 ที่<b>ประกาศว่ายื่นแล้ว</b> (Submitted ไม่มีเลขรับ) ไม่ถูกกันเลย ⇒ ภาษีขายที่ยื่นแล้วถูกกลับในสมุดเงียบ ๆ</para>
/// <para>กติกา (ชุดเดียวกับด่านลงบัญชี — <see cref="TaxFilingLockPolicy.DeclaredOrFiledStatuses"/>): e-Tax ตอบรับแล้ว · อยู่ในรายงานที่ล็อก ·
/// ใบขายที่มี VAT ในเดือน ภ.พ.30 ที่ประกาศ/ยื่นแล้ว · ใบบริการต่างประเทศในเดือน ภ.พ.36 ที่ยื่นแล้ว · 50 ทวิ ที่ออกแล้วในเดือน ภ.ง.ด. ที่ยื่นแล้ว
/// (<see cref="WhtCertVoidGuard.Reason"/> ตัวเดียวกับหน้ายกเลิก 50 ทวิ) ⇒ ปฏิเสธทั้งรอบ ไม่แตะอะไร · ทางไปต่อ = ใบลดหนี้/รายการปรับปรุงงวดปัจจุบัน</para>
/// </summary>
public static class SettlementUnpostGate
{
    public static IReadOnlyList<SettlementUnpostRefusal> Evaluate(
        IReadOnlyList<SettlementUnpostDocument> documents,
        IReadOnlyList<SettlementUnpostCertificate> certificates,
        IReadOnlyCollection<(TaxType Type, int Year, int Month)> declaredOrFiled)
    {
        const string CreditNotePath = "ยกเลิกการลงบัญชีทั้งรอบไม่ได้แล้ว — ถ้ายอดผิด ให้ออกใบลดหนี้/ใบเพิ่มหนี้อ้างเอกสารนั้น (หรือบันทึกรายการปรับปรุง"
            + "ในงวดปัจจุบัน) แล้วนำเข้าส่วนต่างเป็นบรรทัดปรับปรุงของรอบโอนถัดไป · ระบบไม่แตะอะไรในรอบนี้";
        var result = new List<SettlementUnpostRefusal>();
        foreach (var d in documents)
        {
            if (d.EtaxAccepted)
                result.Add(new(d.Number, "e-Tax ของเอกสารนี้ได้รับตอบรับจากกรมสรรพากรแล้ว (Accepted)", CreditNotePath));
            if (d.InLockedReport)
                result.Add(new(d.Number, "เอกสารนี้อยู่ในรายงานภาษีที่ล็อกการยื่นแล้ว", CreditNotePath));
            if (IsSaleSide(d.Component) && d.VatAmount != 0m
                && declaredOrFiled.Contains((TaxType.VAT, d.DocumentDate.Year, d.DocumentDate.Month)))
                result.Add(new(d.Number, $"ภาษีขายของเอกสารนี้อยู่ในเดือน {Period(d.DocumentDate)} ที่ประกาศว่ายื่น ภ.พ.30 แล้ว", CreditNotePath));
            if (d.IsForeignService && declaredOrFiled.Contains((TaxType.VatPp36, d.DocumentDate.Year, d.DocumentDate.Month)))
                result.Add(new(d.Number, $"VAT แทนผู้ประกอบการต่างประเทศของเอกสารนี้อยู่ในเดือน {Period(d.DocumentDate)} ที่ยื่น ภ.พ.36 แล้ว",
                    CreditNotePath));
        }
        foreach (var c in certificates)
            if (WhtCertVoidGuard.Reason(c.Status, c.Number, c.FormType, c.Year, c.Month,
                    declaredOrFiled.Contains((c.FormType, c.Year, c.Month))) is string why)
                result.Add(new(c.Number ?? "50 ทวิ", why, CreditNotePath));
        return result;
    }

    /// <summary>ชิ้นฝั่งขาย (ใบขายสรุปรายวัน)</summary>
    internal static bool IsSaleSide(string component) => component.StartsWith("sum-", StringComparison.Ordinal);

    /// <summary>ลำดับยกเลิกคงที่: ฝั่งขายก่อน (เสี่ยงถูกปฏิเสธสุด — e-Tax/ภ.พ.30) → ใบค่าธรรมเนียม · ในกลุ่มเรียงตามเลขที่ ·
    /// การรับชำระ/ถอนการจับคู่ธนาคาร/กลับ JE รอบโอน ทำหลังเอกสารทั้งหมด (ผู้เรียก)</summary>
    public static IReadOnlyList<SettlementUnpostDocument> VoidOrder(IReadOnlyList<SettlementUnpostDocument> documents)
        => documents.OrderBy(d => IsSaleSide(d.Component) ? 0 : 1).ThenBy(d => d.Number, StringComparer.Ordinal).ToList();

    private static string Period(DateTime d) => $"{d:MM}/{d.Year + 543}";
}

/// <summary>
/// **กำหนดขอบเขต "กำลังยกเลิกการลงบัญชีรอบโอนนี้"** ให้ด่านใน <c>DocumentService.VoidDocumentAsync/VoidPaymentAsync</c> รู้ว่าเส้นที่เรียกคือ
/// Unpost (ฝ่ายค้าน C-5) — ค่าอยู่ใน <see cref="AsyncLocal{T}"/> = ต่อสายการเรียก (async flow) ของคำขอเดียว <b>ไม่ใช่สถานะข้ามคำขอ</b>
/// (CLAUDE.md #4 D) · ออกจากขอบเขตด้วย <c>Dispose</c>
/// </summary>
public static class SettlementUnpostScope
{
    private static readonly AsyncLocal<Guid?> Current = new();

    public static IDisposable Enter(Guid batchId)
    {
        var previous = Current.Value;
        Current.Value = batchId;
        return new Restore(previous);
    }

    internal static bool IsUnposting(Guid batchId) => Current.Value == batchId;

    private sealed class Restore : IDisposable
    {
        private readonly Guid? _previous;
        public Restore(Guid? previous) => _previous = previous;
        public void Dispose() => Current.Value = _previous;
    }
}

/// <summary>
/// **ห้ามยกเลิกเอกสาร/การรับชำระที่การลงบัญชีรอบโอนสร้าง ทีละชิ้นผ่านหน้าปกติ ขณะที่รอบโอนยังขึ้นว่าลงบัญชีแล้ว** (ฝ่ายค้าน C-5)
/// <para>ยกเลิกทีละชิ้น ⇒ รอบโอน Posted/BankMatched แต่ชิ้นหาย · JE รอบโอนยังอยู่ · ผังพักคลาด (ราก R1) — ทางที่ถูกคือ "ยกเลิกการลงบัญชี" ของรอบโอน
/// (ด่าน C-2 + ลำดับคงที่ + กลับ JE) · รอบที่ลงค้างครึ่งทาง (ยังไม่ Posted) หรือถูกยกเลิก/ลบแล้ว (ของกำพร้าจากก่อนรอบนี้) ⇒ <b>ยกเลิกทีละใบได้</b>
/// (เป็นทางไปต่อของ StaleDocument/OrphanPostingArtifacts)</para>
/// </summary>
public static class SettlementArtifactGuard
{
    private const string MarkerHead = "[SETTLEMENT:";

    /// <summary>รอบโอนเจ้าของเอกสาร จาก <c>Document.CreatedBy</c> (<see cref="SettlementPostingKeys.CreatorPrefix"/>) — ไม่ใช่ของรอบโอน = null</summary>
    public static Guid? BatchIdFromCreator(string? createdBy)
    {
        const string head = "system:settlement:";
        if (createdBy == null || !createdBy.StartsWith(head, StringComparison.Ordinal) || createdBy.Length < head.Length + 32)
            return null;
        return Guid.TryParseExact(createdBy.Substring(head.Length, 32), "N", out var id) ? id : null;
    }

    /// <summary>รอบโอนเจ้าของการรับชำระ จากป้ายใน <c>Payment.Notes</c> (<see cref="SettlementPostingKeys.PaymentMarker"/>)</summary>
    public static Guid? BatchIdFromPaymentNotes(string? notes)
    {
        if (notes == null) return null;
        var at = notes.IndexOf(MarkerHead, StringComparison.Ordinal);
        if (at < 0 || notes.Length < at + MarkerHead.Length + 33 || notes[at + MarkerHead.Length + 32] != ']') return null;
        return Guid.TryParseExact(notes.Substring(at + MarkerHead.Length, 32), "N", out var id) ? id : null;
    }

    /// <summary>เหตุผลที่ห้ามยกเลิกชิ้นนี้ทีละชิ้น — null = ยกเลิกได้</summary>
    internal static string? VoidBlockedReason(SettlementBatchStatus? batchStatus, bool batchDeleted, bool unpostingThisBatch, string? payoutRef)
    {
        if (batchStatus is not (SettlementBatchStatus.Posted or SettlementBatchStatus.BankMatched) || batchDeleted || unpostingThisBatch)
            return null;
        return $"รายการนี้ระบบสร้างจากการลงบัญชีรอบโอน {payoutRef} ที่ลงบัญชีแล้ว — ยกเลิกทีละรายการไม่ได้ (JE รอบโอนและผังพักจะไม่ตรงกัน) · "
            + "เปิดรอบโอนนั้นแล้วกด \"ยกเลิกการลงบัญชี\" (ระบบตรวจภาษี/e-Tax ก่อน แล้วยกเลิกทุกชิ้นพร้อมกลับรายการ JE รอบโอน)";
    }

    /// <summary>ตัวโหลดข้อเท็จจริง + ตัดสิน — เรียกจากเส้นยกเลิกเอกสาร/การรับชำระของ <c>DocumentService</c></summary>
    public static async Task<string?> CheckAsync(AccountingDbContext db, Guid companyId, Guid? batchId, CancellationToken ct = default)
    {
        if (batchId is not Guid id) return null;
        var batch = await db.SettlementBatches.IgnoreQueryFilters().AsNoTracking()
            .Where(b => b.Id == id && b.CompanyId == companyId)
            .Select(b => new { b.Status, b.IsDeleted, b.PayoutRef }).FirstOrDefaultAsync(ct);
        if (batch == null) return null;
        return VoidBlockedReason(batch.Status, batch.IsDeleted, SettlementUnpostScope.IsUnposting(id), batch.PayoutRef);
    }

    /// <summary>ยกเลิกเอกสารที่มีการรับชำระจากรอบโอนผูกอยู่ (เส้นยกเลิกเอกสารกลับรายการการรับชำระของใบเองภายใน — ไม่ผ่าน VoidPaymentAsync) —
    /// ใบขายที่รอบโอนรับชำระเข้าผังพักแล้ว ยกเลิกทีละใบไม่ได้ขณะรอบโอนยังลงบัญชีแล้ว</summary>
    public static async Task<string?> CheckDocumentPaymentsAsync(AccountingDbContext db, Guid companyId, Guid documentId,
        CancellationToken ct = default)
    {
        var notes = await db.Payments.AsNoTracking()
            .Where(p => p.CompanyId == companyId && p.DocumentId == documentId && !p.IsDeleted && p.Notes != null
                && p.Notes.Contains(MarkerHead))
            .Select(p => p.Notes).ToListAsync(ct);
        foreach (var n in notes)
            if (await CheckAsync(db, companyId, BatchIdFromPaymentNotes(n), ct) is string why)
                return why;
        return null;
    }
}
