using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>จุดที่ขอออกหนังสือรับรองหัก ณ ที่จ่าย (50 ทวิ) จากเอกสาร</summary>
public enum WhtCertIssueTrigger
{
    /// <summary>อนุมัติเอกสารที่ "จ่ายจบตอนอนุมัติ" (PV เงินสด · ใบที่ settle มาแล้ว)</summary>
    Approve,
    /// <summary>บันทึกจ่าย (ใบเดียว · งวด · โอนก้อนเดียวหลายใบ)</summary>
    Payment,
    /// <summary>ซิงค์จากระบบภายนอก (integration) — ยังไม่จ่าย = ออกเป็นฉบับร่าง</summary>
    IntegrationSync,
    /// <summary>"สร้างใบทั้งเดือน" จากหน้า 50 ทวิ (ทำทีละหลายใบ)</summary>
    Bulk,
    /// <summary>ผู้ใช้กด "สร้างใบ" ทีละใบ (ตั้งใจออกใบนี้)</summary>
    Manual,
}

/// <summary>ผลการตัดสิน</summary>
public enum WhtCertIssueAction
{
    /// <summary>ใบนี้ไม่ต้องมี 50 ทวิ จากเอกสาร (ไม่มี WHT · ไม่ใช่ฝั่งผู้จ่าย · ยกเลิก/ตีกลับ)</summary>
    NotApplicable,
    /// <summary>ออกใบเดี๋ยวนี้</summary>
    IssueNow,
    /// <summary>ยังไม่จ่ายเงิน — 50 ทวิ ออกตอนบันทึกจ่าย (ท.ป.4/2528: ออกในวันจ่าย)</summary>
    WaitForPayment,
    /// <summary>ไม่ออกอัตโนมัติ — ใบรอในลิสต์ "รอออกใบหัก ณ ที่จ่าย" ให้ผู้ใช้ออกเอง</summary>
    ManualPending,
    /// <summary>มีใบที่ครอบการจ่ายนี้อยู่แล้ว (idempotent — ไม่ใช่ความล้มเหลว)</summary>
    AlreadyIssued,
}

/// <summary>ข้อเท็จจริงของเอกสาร ณ จุดที่ขอออกใบ (ผู้เรียกโหลดจากฐาน · ตัวตัดสินไม่แตะฐาน)</summary>
/// <param name="PaidAtTrigger">จุด Approve: ใบ "จ่ายจบตอนอนุมัติ" ไหม · จุดอื่นไม่ใช้</param>
/// <param name="HasSourcePayment">ออกใบรายงวด (ผูก payment id) ไหม</param>
/// <param name="ActiveCertsForThisPayment">ใบที่ยังไม่ยกเลิกของ "งวดจ่ายนี้ + เอกสารนี้"</param>
/// <param name="ActiveFullDocCertsNotDraft">ใบระดับทั้งเอกสาร (ไม่ผูกงวด) ที่ออกจริงแล้ว (ไม่ใช่ร่าง ไม่ยกเลิก)</param>
/// <param name="ActiveCertsAny">ใบที่ยังไม่ยกเลิกของเอกสารนี้ทุกชนิด</param>
public sealed record WhtCertIssueFacts(
    WhtCertIssueTrigger Trigger,
    DocumentType DocumentType,
    DocumentStatus Status,
    decimal WithholdingTaxAmount,
    bool AutoIssueEnabled,
    bool DismissedFromPending,
    Guid ContactId,
    Guid? PayeeContactId,
    bool PaidAtTrigger,
    bool HasSourcePayment,
    int ActiveCertsForThisPayment,
    int ActiveFullDocCertsNotDraft,
    int ActiveCertsAny);

/// <summary>คำตอบ: ทำอะไร · ออกในนามใคร · เหตุผลภาษาไทย (แสดงผู้ใช้ได้)</summary>
public sealed record WhtCertIssueDecision(WhtCertIssueAction Action, Guid PayeeContactId, string Reason);

/// <summary>
/// **ตัวตัดสินเดียว "ออก 50 ทวิ จากเอกสารใบนี้ตอนนี้ไหม · ในนามใคร"** (คำขอเจ้าของ 2026-10-12)
///
/// <para>ที่มา: เดิมแต่ละจุด (อนุมัติ · บันทึกจ่าย · จ่ายหลายใบ · ซิงค์ · สร้างทั้งเดือน) ตัดสินเองแล้วเรียก
/// <c>AutoGenerateFromDocumentAsync</c> ตรง ๆ ทุกครั้ง · ผู้ถูกหัก = ผู้ติดต่อของเอกสารเสมอ ⇒ ซื้อประกันผ่านโบรกเกอร์
/// (ใบสำคัญจ่ายชื่อโบรกเกอร์ แต่ผู้มีเงินได้คือบริษัทประกัน) ได้ 50 ทวิ ผิดชื่อ/ผิดเลขผู้เสียภาษีทุกใบ และผู้ใช้ปิดการออกอัตโนมัติไม่ได้ ·
/// ออกไม่สำเร็จก็ถูกกลืนด้วย LogWarning (ไม่มีใครเห็น)</para>
///
/// <para>กติกา: ไม่มี WHT/ไม่ใช่ฝั่งผู้จ่าย/ยกเลิก-ตีกลับ ⇒ ไม่เกี่ยว · มีใบครอบแล้ว ⇒ ไม่ออกซ้ำ ·
/// ทางอัตโนมัติ (อนุมัติ/จ่าย/ซิงค์/ทั้งเดือน) + ผู้ใช้ปิดติ๊ก หรือกด "ข้าม" ไว้ ⇒ รอในลิสต์ (ไม่ออกเอง) ·
/// อนุมัติแต่ยังไม่จ่าย ⇒ รอวันจ่าย · ที่เหลือ ⇒ ออกเดี๋ยวนี้ · กดทีละใบ (Manual) = ผู้ใช้ตั้งใจ ⇒ ออกแม้ปิดติ๊ก</para>
/// </summary>
public static class WhtCertIssuePlan
{
    /// <summary>รหัสกฎสำหรับ audit/ข้อความ</summary>
    public const string RuleCode = "RD-50BIS-AUTO";
    public const string PayeeRuleCode = "RD-50BIS-PAYEE";

    /// <summary>ป้ายคำเตือนก่อนอนุมัติเรื่องผู้ถูกหัก (ไม่มีเลขผู้เสียภาษี · ไม่พบผู้ถูกหัก · ผู้ถูกหักคือบริษัทเราเอง) — ทางเข้าที่ไม่มีคนเห็น
    /// (ใบประจำ · API · LINE · อนุมัติอัตโนมัติ) <b>ผ่าน</b> (<c>ApprovalAcknowledgement</c>) เพราะความล้มเหลวของ 50 ทวิ ล้มดังอยู่แล้ว
    /// (ป้ายบนเอกสาร + ลิสต์รอออกใบ) — ห้ามทำให้การจ่ายเงินที่เคยผ่านถูกหยุด · เว็บ/มือถือยังหยุดให้คนเห็น</summary>
    public const string ApprovalWarningPrefix = "[WHT-PAYEE]";

    public static bool IsApprovalWarning(string warning)
        => warning.StartsWith(ApprovalWarningPrefix, StringComparison.Ordinal);

    /// <summary>ชนิดเอกสารที่ "เราเป็นผู้หัก" — ชุดเดียวกับ <see cref="WhtRemitScope.PayerSideTypes"/></summary>
    public static bool AppliesTo(DocumentType type) => WhtRemitScope.PayerSideTypes.Contains(type);

    /// <summary>ผู้ถูกหักภาษีของเอกสาร — override ถ้ามี ไม่งั้นผู้ติดต่อของเอกสาร (ทุกจุดที่ตัดสินแบบ ภ.ง.ด./ผัง 2191x/50 ทวิ อ่านที่นี่)</summary>
    public static Guid PayeeOf(Guid contactId, Guid? payeeOverride)
        => payeeOverride is Guid p && p != Guid.Empty ? p : contactId;

    /// <summary>ค่าที่ควรเก็บลง <c>Document.WhtPayeeContactId</c> — ว่าง/Guid.Empty/ซ้ำกับผู้ติดต่อของเอกสาร ⇒ null (ไม่เก็บสำเนาที่ไม่มีความหมาย)</summary>
    public static Guid? NormalizeOverride(Guid contactId, Guid? requested)
        => requested is Guid p && p != Guid.Empty && p != contactId ? p : null;

    /// <summary>ตัดสินว่าจะออกใบไหม</summary>
    public static WhtCertIssueDecision Decide(WhtCertIssueFacts f)
    {
        var payee = PayeeOf(f.ContactId, f.PayeeContactId);
        WhtCertIssueDecision D(WhtCertIssueAction a, string reason) => new(a, payee, reason);

        if (f.WithholdingTaxAmount <= 0m)
            return D(WhtCertIssueAction.NotApplicable, "เอกสารนี้ไม่มีภาษีหัก ณ ที่จ่าย");
        if (!AppliesTo(f.DocumentType))
            return D(WhtCertIssueAction.NotApplicable, "เอกสารฝั่งรับเงิน — ผู้จ่ายเป็นผู้ออก 50 ทวิ ให้เรา");
        if (f.Status is DocumentStatus.Voided or DocumentStatus.Rejected)
            return D(WhtCertIssueAction.NotApplicable, "เอกสารถูกยกเลิก/ตีกลับ — ไม่มีการจ่ายให้ออกใบ");

        var covered = f.HasSourcePayment
            ? f.ActiveCertsForThisPayment > 0 || f.ActiveFullDocCertsNotDraft > 0
            : f.ActiveCertsAny > 0;
        if (covered)
            return D(WhtCertIssueAction.AlreadyIssued, "มีหนังสือรับรองหัก ณ ที่จ่ายของการจ่ายนี้แล้ว");

        if (f.Trigger != WhtCertIssueTrigger.Manual)
        {
            if (!f.AutoIssueEnabled)
                return D(WhtCertIssueAction.ManualPending,
                    "ตั้งไว้ไม่ให้สร้าง 50 ทวิ อัตโนมัติ — ออกใบเองที่หน้า \"หนังสือรับรองหัก ณ ที่จ่าย\" → \"เอกสารรอออกใบ\"");
            if (f.DismissedFromPending)
                return D(WhtCertIssueAction.ManualPending,
                    "เอกสารนี้ถูกกด \"ข้าม\" ออกจากลิสต์รอออกใบ — ระบบไม่ออกใบเอง");
        }

        if (f.Trigger == WhtCertIssueTrigger.Approve && !f.PaidAtTrigger)
            return D(WhtCertIssueAction.WaitForPayment, "ยังไม่จ่ายเงิน — 50 ทวิ ออกตอนบันทึกจ่าย (ท.ป.4/2528 ออกในวันจ่าย)");

        return D(WhtCertIssueAction.IssueNow, "ออกหนังสือรับรองหัก ณ ที่จ่าย");
    }

    /// <summary>เลขผู้เสียภาษีของผู้ถูกหักใช้บน 50 ทวิ ได้ไหม — คืนข้อความปัญหา (null = ใช้ได้) ·
    /// ผู้รับต่างประเทศ (ภ.ง.ด.54) ไม่มีเลขไทย ⇒ ไม่ตรวจ · ตัวตรวจ checksum ตัวเดียวของระบบ (<see cref="ThaiTaxId"/>)</summary>
    public static string? PayeeTaxIdProblem(string? payeeName, string? taxId, bool foreignPayee)
    {
        if (foreignPayee) return null;
        var who = string.IsNullOrWhiteSpace(payeeName) ? "ผู้ถูกหักภาษี" : $"ผู้ถูกหักภาษี \"{payeeName!.Trim()}\"";
        if (!ThaiTaxId.IsWellFormed(taxId))
            return $"{who} ยังไม่มีเลขประจำตัวผู้เสียภาษี 13 หลัก — 50 ทวิ และแบบ ภ.ง.ด. ต้องมีเลขนี้ " +
                   "(เติมที่ข้อมูลผู้ติดต่อ แล้วกด \"สร้างใบ\" ที่ลิสต์รอออกใบ)";
        if (!ThaiTaxId.HasValidChecksum(taxId))
            return $"เลขประจำตัวผู้เสียภาษีของ{who} ({ThaiTaxId.Normalize(taxId)}) ไม่ผ่านการตรวจหลักท้าย — " +
                   "ตรวจเลขที่ข้อมูลผู้ติดต่อ แล้วกด \"สร้างใบ\" ที่ลิสต์รอออกใบ";
        return null;
    }

    /// <summary>คำอธิบายสถานะในลิสต์ "รอออกใบ" (server computes · page displays)</summary>
    public static string PendingLabel(bool autoIssueEnabled, string? autoIssueFailedReason, bool paid)
    {
        if (!string.IsNullOrWhiteSpace(autoIssueFailedReason))
            return "ออกอัตโนมัติไม่สำเร็จ: " + autoIssueFailedReason;
        if (!autoIssueEnabled) return "ตั้งไว้ให้ออกใบเอง (ไม่สร้างอัตโนมัติ)";
        return paid ? "จ่ายแล้วแต่ยังไม่มีใบ" : "รอจ่ายเงิน — ระบบจะออกใบอัตโนมัติตอนบันทึกจ่าย";
    }
}

/// <summary>
/// **ป้าย "ออก 50 ทวิ อัตโนมัติไม่สำเร็จ" บนหมายเหตุภายในของเอกสาร — เจ้าของรูปแบบตัวเดียว** (แบบเดียวกับ <see cref="EtaxAutoFailedNote"/>)
///
/// <para>ที่มา: ทุกจุดที่ออก 50 ทวิ อัตโนมัติกลืนความล้มเหลวด้วย <c>LogWarning</c> ⇒ ผู้ถูกหักไม่ได้ใบ ผู้ใช้ไม่รู้ (F2 ข้อ 7 "ล้มดัง 3 ที่").
/// ตอนนี้: เขียนป้ายลงเอกสาร (ตัวข้อมูลที่ผู้ใช้เปิดดู) · ส่งเหตุผลกลับในคำตอบอนุมัติ/บันทึกจ่าย · ใบไปโผล่ในลิสต์รอออกใบ ·
/// ออกใบสำเร็จ (ทางใดก็ตาม) ⇒ ล้างป้าย</para>
/// </summary>
public static class WhtCertAutoFailedNote
{
    public const string Marker = "[WHT-CERT-AUTO-FAILED]";
    private const string Lead = " ออก 50 ทวิ อัตโนมัติไม่สำเร็จ: ";

    public static string Build(string reason) => $"{Marker}{Lead}{reason}";

    /// <summary>ต่อท้ายหมายเหตุเดิม (ป้ายเดิมถูกแทนด้วยอันล่าสุด — ล้มซ้ำไม่สะสม)</summary>
    public static string Append(string? existing, string reason)
    {
        var kept = Clear(existing);
        var note = Build(reason);
        return string.IsNullOrWhiteSpace(kept) ? note : kept!.TrimEnd() + "\n\n" + note;
    }

    public static bool Has(string? notes)
        => !string.IsNullOrEmpty(notes) && notes.Contains(Marker, StringComparison.Ordinal);

    /// <summary>เหตุผลในป้าย (null = ไม่มีป้าย) — ส่งเป็นช่องของคำตอบให้หน้าเว็บแสดง</summary>
    public static string? ReasonOf(string? notes)
    {
        if (!Has(notes)) return null;
        var para = notes!.Replace("\r\n", "\n").Split("\n\n")
            .LastOrDefault(p => p.TrimStart().StartsWith(Marker, StringComparison.Ordinal));
        if (para == null) return null;
        var text = para.TrimStart().Substring(Marker.Length);
        return text.StartsWith(Lead, StringComparison.Ordinal) ? text.Substring(Lead.Length).Trim() : text.Trim();
    }

    /// <summary>ถอดทุกย่อหน้าของป้าย · ไม่มีป้าย = คืนค่าเดิมทุกตัวอักษร · เหลือแต่ช่องว่าง = null</summary>
    public static string? Clear(string? notes)
    {
        if (!Has(notes)) return notes;
        var paras = notes!.Replace("\r\n", "\n").Split("\n\n")
            .Where(p => !p.TrimStart().StartsWith(Marker, StringComparison.Ordinal));
        var joined = string.Join("\n\n", paras).Trim();
        return joined.Length == 0 ? null : joined;
    }
}
