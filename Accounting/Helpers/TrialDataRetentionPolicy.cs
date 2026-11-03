using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>รอบ 231 (คำตัดสินข้อ 231-10 · LICENSE_USAGE_OVERAGE_AUDIT §A.8 "เก็บ/ลบข้อมูลหลังหมดอายุ" · ฝ่ายค้าน B2) — <b>ตัวตัดสินเดียว</b>ว่าข้อมูลของบริษัท<b>ทดลองใช้</b>ที่หมดอายุ
/// ถึงเวลา "แจ้งเจ้าของล่วงหน้า" หรือ "ปิด" (soft-delete + anonymize ผ่านเส้น PDPA) แล้วหรือยัง · ผู้อ่านของค่าตั้ง <c>TrialConfig.DeleteDataAfterGracePeriod / DataRetentionDays / GracePeriodDays / PurgeNoticeDays</c>
/// (เดิมเก็บ+echo แต่ไม่มีผู้อ่าน — F2 ข้อ 2) · ผู้เรียก: <c>Services/Background/TrialDataRetentionJob</c>
///
/// <para><b>กติกา (อนุรักษ์นิยมที่สุด):</b> ① เฉพาะ <c>Kind = Trial</c> + <c>Status = Expired</c> + ไม่เคยมีเงินเข้า (จ่ายแล้วแม้ครั้งเดียว = ลูกค้า ไม่ใช่ทดลอง) ·
/// ② เปิดสวิตช์ <c>DeleteDataAfterGracePeriod</c> ไว้ (ค่าตั้งต้นปิด) · ③ ไม่มีรายการชำระ/ใบแจ้งหนี้แพลตฟอร์มค้าง (งานยังไม่จบ) ·
/// ④ <b>ไม่อยู่ใต้ legal hold</b> — เอกสารที่ไม่ใช่ร่าง · รายการบัญชีที่ลงแล้ว · แบบภาษีที่ยื่นแล้ว · e-Tax ที่ส่งกรมแล้ว · หนังสือรับรอง 50 ทวิ ที่ออกแล้ว · งวดเงินเดือนที่จ่ายแล้ว
/// (พ.ร.บ.บัญชี ม.10 · §87/3 · ท.ป.4/2528 เก็บ 5 ปี) ⇒ <b>ไม่ลบ</b> (คง read-only · ลบได้เฉพาะคำขอ DSR ที่ไม่ติด hold) ·
/// ⑤ ครบ <c>EndDate + GracePeriodDays + DataRetentionDays</c> ⇒ <b>แจ้งเจ้าของก่อน</b> (<see cref="Outcome.NoticeDue"/>) แล้วรออีก <c>PurgeNoticeDays</c> วันนับจากวันแจ้ง จึง <see cref="Outcome.Purge"/> ·
/// <c>PurgeNoticeDays = 0</c> = ไม่แจ้ง ปิดเมื่อครบกำหนด · ⑥ ปิด = soft-delete บริษัท + anonymize ผู้ติดต่อผ่าน <c>IPdpaService.ApplyErasureAsync</c> (เส้นเดียวกับ DSR) ไม่ใช่ DELETE แถว</para></summary>
public static class TrialDataRetentionPolicy
{
    public enum Outcome { NotEligible = 0, NotYetDue = 1, LegalHold = 2, Purge = 3, NoticeDue = 4, NoticeSent = 5 }

    /// <summary>legal hold ทีละเหตุ — ข้อความอ้างมาตรา</summary>
    public sealed record LegalHoldFacts(bool HasIssuedDocuments, bool HasPostedJournals, bool HasFiledTaxReturns = false, bool HasSubmittedEtax = false,
        bool HasIssuedWhtCerts = false, bool HasPaidPayrollRuns = false)
    {
        public bool Any => HasIssuedDocuments || HasPostedJournals || HasFiledTaxReturns || HasSubmittedEtax || HasIssuedWhtCerts || HasPaidPayrollRuns;
        public string Describe()
        {
            var parts = new List<string>();
            if (HasIssuedDocuments) parts.Add("เอกสารที่ออกแล้ว (พ.ร.บ.บัญชี ม.10 · §87/3)");
            if (HasPostedJournals) parts.Add("รายการบัญชีที่ลงแล้ว (พ.ร.บ.บัญชี ม.10)");
            if (HasFiledTaxReturns) parts.Add("แบบภาษีที่ยื่นแล้ว (§87/3)");
            if (HasSubmittedEtax) parts.Add("e-Tax Invoice ที่ส่งกรมสรรพากรแล้ว (ขมธอ.3-2560)");
            if (HasIssuedWhtCerts) parts.Add("หนังสือรับรอง 50 ทวิ ที่ออกแล้ว (ท.ป.4/2528)");
            if (HasPaidPayrollRuns) parts.Add("งวดเงินเดือนที่จ่ายแล้ว (ภ.ง.ด.1 · ประกันสังคม)");
            return string.Join(" · ", parts);
        }
    }

    public sealed record Facts(SubscriptionKind Kind, SubscriptionStatus Status, bool EverPaid, bool DeleteDataAfterGracePeriod,
        int GracePeriodDays, int DataRetentionDays, DateTime EndDateUtc, LegalHoldFacts LegalHold, bool CompanyAlreadyDeleted,
        int PurgeNoticeDays = 14, DateTime? PurgeNoticeSentAtUtc = null, bool HasOpenPlatformBilling = false);

    public sealed record Decision(Outcome Outcome, DateTime? DueAtUtc, string Reason);

    public static DateTime DueAt(DateTime endDateUtc, int graceDays, int retentionDays)
        => endDateUtc.AddDays(Math.Max(0, graceDays)).AddDays(Math.Max(0, retentionDays));

    /// <summary>วันที่ปิดได้จริงหลังแจ้ง (= max(due, แจ้ง + PurgeNoticeDays))</summary>
    public static DateTime PurgeAt(DateTime dueAtUtc, DateTime? noticeSentAtUtc, int noticeDays)
        => noticeSentAtUtc is DateTime n && noticeDays > 0 && n.AddDays(noticeDays) > dueAtUtc ? n.AddDays(noticeDays) : dueAtUtc;

    public static Decision Decide(Facts f, DateTime nowUtc)
    {
        if (f.CompanyAlreadyDeleted) return new(Outcome.NotEligible, null, "บริษัทถูกปิดแล้ว");
        if (f.Kind != SubscriptionKind.Trial) return new(Outcome.NotEligible, null, "ไม่ใช่ทดลองใช้ (Free Edition/จ่ายเงิน ไม่มีนโยบายลบ)");
        if (f.Status != SubscriptionStatus.Expired) return new(Outcome.NotEligible, null, "ทดลองใช้ยังไม่หมดอายุ");
        if (f.EverPaid) return new(Outcome.NotEligible, null, "เคยมีการชำระ — เป็นลูกค้า ไม่ใช่ทดลอง");
        if (!f.DeleteDataAfterGracePeriod) return new(Outcome.NotEligible, null, "ไม่ได้เปิด DeleteDataAfterGracePeriod");
        if (f.HasOpenPlatformBilling) return new(Outcome.NotEligible, null, "มีรายการชำระ/ใบแจ้งหนี้ค่าบริการค้างอยู่ — ปิดรายการก่อน");
        var due = DueAt(f.EndDateUtc, f.GracePeriodDays, f.DataRetentionDays);
        var noticeDays = Math.Max(0, f.PurgeNoticeDays);
        var noticeFrom = due.AddDays(-noticeDays);
        if (nowUtc < noticeFrom) return new(Outcome.NotYetDue, due, $"ครบกำหนดเก็บ {due:yyyy-MM-dd}");
        if (f.LegalHold.Any)
            return new(Outcome.LegalHold, due, $"ติด legal hold: {f.LegalHold.Describe()} — ไม่ลบ (ลบได้เฉพาะคำขอ DSR ที่ไม่ติด hold)");
        if (noticeDays > 0 && f.PurgeNoticeSentAtUtc == null)
            return new(Outcome.NoticeDue, due, $"แจ้งเจ้าของล่วงหน้า {noticeDays} วัน — จะปิดข้อมูลไม่ก่อน {PurgeAt(due, nowUtc, noticeDays):yyyy-MM-dd}");
        var purgeAt = PurgeAt(due, f.PurgeNoticeSentAtUtc, noticeDays);
        if (nowUtc < purgeAt) return new(Outcome.NoticeSent, purgeAt, $"แจ้งแล้ว {f.PurgeNoticeSentAtUtc:yyyy-MM-dd} — ปิดข้อมูลได้ตั้งแต่ {purgeAt:yyyy-MM-dd}");
        return new(Outcome.Purge, purgeAt, "พ้นระยะเก็บ + แจ้งเจ้าของแล้ว · ไม่มีเอกสารที่กฎหมายบังคับเก็บ");
    }

    public const string RuleCode = "TRIAL-RETENTION-PURGE";
    public const string NoticeRuleCode = "TRIAL-RETENTION-NOTICE";
    public const string LegalReference = "PDPA ม.37 retention-by-purpose · พ.ร.บ.การบัญชี ม.10 · ป.รัษฎากร §87/3 · ท.ป.4/2528 (legal hold)";
}
