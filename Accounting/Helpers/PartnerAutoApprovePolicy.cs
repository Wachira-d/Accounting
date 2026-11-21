using System.Globalization;

namespace Accounting.Helpers;

/// <summary>
/// **ตัวตัดสินเดียวของ "อนุมัติใบซื้อจากคู่ค้าเองภายใต้เพดาน"** (ขอบเขต <see cref="PartnerConsent.Scope.AutoApprovePurchase"/> · รอบ 288 คำสั่งเจ้าของ
/// "ทำให้ใช้งานได้ทั้งหมด" · ดีไซน์ team-264-a2b-design §5.2) · pure · ผู้เรียกเดียว <c>PartnerPayablePipeline.AutoApproveDraftsAsync</c>
/// (ขั้น PartnerPayable ของ <c>AutopilotJob</c> — ไม่ทำงานในคำขอของผู้ขาย)
/// <para>อนุมัติเองเมื่อ<b>ครบทุกข้อ</b> (ไม่ครบข้อใด ⇒ ใบยังเป็นร่าง + เหตุบนใบ + แจ้งเตือน · ระบบไม่ประทับอะไรแทนคน):</para>
/// <list type="number">
/// <item>ความยินยอมใช้ได้ ณ ตอนนี้ (ตรวจเพื่อเขียนในธุรกรรมล็อก · ยอดรวมบาทก่อนหัก ณ ที่จ่าย ≤ เพดาน · ผู้ให้ยังมีสิทธิ์อนุมัติเอกสารซื้อ)</item>
/// <item>ผู้ให้ความยินยอมไม่ได้ถูกตั้งเป็น "ทำได้เฉพาะร่าง" (ด่านเดียวกับเส้นอนุมัติ <c>DocumentRestrictionGuard.MayFinalizeAsync</c>)</item>
/// <item>ใบยังเป็นร่าง · มาจากเอกสารของคู่ค้า · ต้นทางยังไม่ถูกยกเลิก · สกุลบาท · มีบรรทัด</item>
/// <item>ใบกำกับของผู้ขาย (ถ้าเป็นใบกำกับ) ครบรายการ ม.86/4 — ยกเว้นข้อ "ยังไม่ได้ต้นฉบับ" (อนุมัติ ≠ เคลมภาษีซื้อ · หลักฐานภาษีซื้อยังรอต้นฉบับตามเดิม)</item>
/// <item>ทุกบรรทัดมีผังบัญชีที่มั่นใจ: โมเดลในบ้านตอบผังนี้ด้วยความมั่นใจ ≥ <see cref="MinLocalConfidence"/> หรือผู้ใช้เคยยืนยันผังนี้กับผู้ขายรายนี้
/// (คำตอบจาก AI ภายนอกอย่างเดียวไม่พอ — กฎเหล็ก #1 kill-switch: AI ปิด ⇒ ใบรอคนกดเหมือนเดิม ไม่พัง)</item>
/// <item>ไม่มีบรรทัดที่ระบบตั้งว่าภาษีซื้อต้องห้าม (ม.82/5)</item>
/// <item>ภาษีหัก ณ ที่จ่ายตัดสินได้ชัด (<see cref="WhtProblem"/>)</item>
/// <item>ไม่มีคำเตือนก่อนอนุมัติเลย (ตัวรวบรวมเดิมของเส้นอนุมัติ — รวม ม.65 ตรี · ม.82/5(4)(6) · หัก ณ ที่จ่าย · วันที่ · งวดยื่นแล้ว)</item>
/// </list>
/// ไม่มีจุดเรียก AI ในตัวตัดสินนี้ (ป้ายบนใบเป็น ⚙️ เสมอ)
/// </summary>
public static class PartnerAutoApprovePolicy
{
    public const string RuleCode = "XT-AUTO-APPROVE";
    public const string LegalReference = "ป.รัษฎากร ม.86/4 (รายการในใบกำกับ) · ม.82/5 (ภาษีซื้อต้องห้าม) · ม.65 ตรี (รายจ่ายต้องห้าม) · ท.ป.4/2528 (หัก ณ ที่จ่าย) "
        + "· พ.ร.บ.การบัญชี 2543 ม.8 — อนุมัติตามความยินยอมของบริษัทผู้ซื้อ (ผู้ให้ความยินยอมเป็นผู้อนุมัติ)";

    /// <summary>ความมั่นใจขั้นต่ำของโมเดลในบ้านที่ยอมให้ผังบัญชีเป็นหลักฐานอนุมัติเอง (เกณฑ์ short-circuit เดิมของ orchestrator)</summary>
    public const decimal MinLocalConfidence = 0.85m;

    /// <summary>ป้ายบนหมายเหตุภายในของใบที่ระบบอนุมัติเอง — ใบที่มีป้ายนี้ไม่นับเป็น "ผู้ใช้เคยยืนยันผัง" (ไม่เรียนจากตัวเอง)</summary>
    public const string ApprovedMarker = "[PARTNER-AUTO-APPROVED]";
    /// <summary>ป้ายบนหมายเหตุภายในของใบที่ระบบยังไม่อนุมัติเอง (ลงเมื่อเหตุเปลี่ยนเท่านั้น — ไม่สแปม)</summary>
    public const string HoldMarker = "[PARTNER-AUTO-APPROVE-HOLD]";

    /// <summary>ประเมินใบเดิมซ้ำเมื่อพ้นช่วงนี้ (หรือเมื่อใบถูกแก้หลังประเมินครั้งก่อน)</summary>
    public static readonly TimeSpan ReevaluateEvery = TimeSpan.FromMinutes(15);

    /// <summary>ดูใบร่างย้อนหลังกี่วัน (ใบเก่ากว่านี้รอคนกดตามเดิม)</summary>
    public const int LookbackDays = 30;

    public static class Codes
    {
        public const string Approved = "Approved";
        public const string Consent = "Consent";
        public const string GrantorDraftOnly = "GrantorDraftOnly";
        public const string NotDraft = "NotDraft";
        public const string SourceVoided = "SourceVoided";
        public const string Currency = "Currency";
        public const string NoLines = "NoLines";
        public const string TaxInvoice = "TaxInvoice";
        public const string GlMissing = "GlMissing";
        public const string GlUnsure = "GlUnsure";
        public const string InputVatBlocked = "InputVatBlocked";
        public const string Wht = "Wht";
        public const string Warnings = "Warnings";
        public const string ApproveFailed = "ApproveFailed";
    }

    /// <param name="LocalConfident">โมเดลในบ้านตอบผังเดียวกับบรรทัดด้วยความมั่นใจ ≥ เกณฑ์ (<see cref="LocalConfidentOf"/>)</param>
    /// <param name="VendorConfirmed">ผู้ใช้เคยยืนยันผังนี้กับผู้ขายรายนี้ (ใบซื้อที่คนอนุมัติ หรือคนเลือกผังนี้เองในคำแนะนำ)</param>
    /// <param name="InputVatBlocked">บรรทัดมี VAT แต่ตั้งว่าเคลมไม่ได้ (ม.82/5)</param>
    public sealed record Line(int Order, string? Description, bool HasAccount, bool LocalConfident, bool VendorConfirmed, bool InputVatBlocked = false);

    public sealed record Facts(
        bool ConsentOk, string ConsentReason,
        bool GrantorMayFinalize,
        bool IsDraft, bool SourceVoided, string? Currency,
        bool IsTaxInvoice, IReadOnlyList<string> TaxInvoiceMissing,
        IReadOnlyList<Line> Lines,
        string? WhtProblem,
        IReadOnlyList<string> ApprovalWarnings);

    public sealed record Verdict(bool Auto, string Code, string Reason)
    {
        public bool UsedAi => false;
    }

    private static Verdict Hold(string code, string reason) => new(false, code, reason);

    public static Verdict Decide(Facts f)
    {
        if (!f.ConsentOk) return Hold(Codes.Consent, "ความยินยอมใช้ไม่ได้ตอนนี้: " + f.ConsentReason);
        if (!f.GrantorMayFinalize) return Hold(Codes.GrantorDraftOnly, "ผู้ให้ความยินยอมถูกตั้งให้ทำได้เฉพาะร่าง — อนุมัติแทนไม่ได้ (ผู้มีสิทธิ์อนุมัติต้องยืนยันความยินยอมใหม่)");
        if (!f.IsDraft) return Hold(Codes.NotDraft, "ใบไม่ใช่ร่างแล้ว");
        if (f.SourceVoided) return Hold(Codes.SourceVoided, "ผู้ขายยกเลิกเอกสารต้นทางแล้ว — ไม่อนุมัติ");
        if (!string.Equals((f.Currency ?? "THB").Trim(), "THB", StringComparison.OrdinalIgnoreCase))
            return Hold(Codes.Currency, "ใบไม่ใช่สกุลบาท — ระบบไม่อนุมัติเอง");
        if (f.Lines.Count == 0) return Hold(Codes.NoLines, "ใบไม่มีรายการ");
        if (f.IsTaxInvoice && f.TaxInvoiceMissing.Count > 0)
            return Hold(Codes.TaxInvoice, "ใบกำกับภาษีของผู้ขายยังขาดรายการตาม ม.86/4: " + string.Join(", ", f.TaxInvoiceMissing));
        var noAccount = f.Lines.Where(l => !l.HasAccount).ToList();
        if (noAccount.Count > 0)
            return Hold(Codes.GlMissing, "ยังไม่มีผังบัญชี " + noAccount.Count + " บรรทัด (" + Names(noAccount) + ") — เลือกผังแล้วอนุมัติเอง");
        var unsure = f.Lines.Where(l => !l.LocalConfident && !l.VendorConfirmed).ToList();
        if (unsure.Count > 0)
            return Hold(Codes.GlUnsure, "ผังบัญชียังไม่มั่นใจ " + unsure.Count + " บรรทัด (" + Names(unsure) + ") — ยังไม่เคยยืนยันผังนี้กับผู้ขายรายนี้ "
                + "และระบบในบ้านมั่นใจไม่ถึง " + (MinLocalConfidence * 100m).ToString("0", CultureInfo.InvariantCulture) + "% · ตรวจผังแล้วอนุมัติเอง (ครั้งถัดไประบบจำได้)");
        var blocked = f.Lines.Where(l => l.InputVatBlocked).ToList();
        if (blocked.Count > 0)
            return Hold(Codes.InputVatBlocked, "มีรายการที่ภาษีซื้อต้องห้าม (ม.82/5) " + blocked.Count + " บรรทัด (" + Names(blocked) + ") — ให้คนตรวจก่อนอนุมัติ");
        if (!string.IsNullOrWhiteSpace(f.WhtProblem)) return Hold(Codes.Wht, f.WhtProblem!);
        if (f.ApprovalWarnings.Count > 0)
            return Hold(Codes.Warnings, "มีข้อสังเกตก่อนอนุมัติที่ต้องให้คนดู " + f.ApprovalWarnings.Count + " ข้อ: " + Trunc(f.ApprovalWarnings[0], 300));
        return new Verdict(true, Codes.Approved, "ครบทุกเงื่อนไข — ระบบอนุมัติแทนผู้ให้ความยินยอม");
    }

    private static string Names(IEnumerable<Line> lines)
        => string.Join(", ", lines.Take(3).Select(l => string.IsNullOrWhiteSpace(l.Description) ? "บรรทัด " + l.Order : "“" + Trunc(l.Description!.Trim(), 40) + "”"))
           + (lines.Count() > 3 ? " …" : "");

    private static string Trunc(string s, int n) => s.Length > n ? s[..n] + "…" : s;

    /// <summary>โมเดลในบ้านตอบผังเดียวกับที่อยู่บนบรรทัด ด้วยความมั่นใจ ≥ <see cref="MinLocalConfidence"/> · คำตอบของ AI ภายนอกไม่นับ</summary>
    public static bool LocalConfidentOf(string? lineAccountCode, string? localAnswer, decimal? localConfidence)
        => !string.IsNullOrWhiteSpace(lineAccountCode)
           && string.Equals(lineAccountCode.Trim(), (localAnswer ?? "").Trim(), StringComparison.Ordinal)
           && (localConfidence ?? 0m) >= MinLocalConfidence;

    /// <summary>รายการ ม.86/4 ที่ยังขาด <b>โดยไม่นับ</b> "ยังไม่ได้ต้นฉบับใบกำกับ" (หลักฐานภาษีซื้อ — อนุมัติไม่ใช่การเคลม) · ผลของ
    /// <c>TaxInvoiceCompletenessChecker.Evaluate</c> ตัวเดียว</summary>
    public static IReadOnlyList<string> TaxInvoiceMissingForApproval(IReadOnlyList<string> missing, string? paperEvidenceLabel)
        => missing.Where(m => paperEvidenceLabel == null || !string.Equals(m, paperEvidenceLabel, StringComparison.Ordinal)).ToList();

    /// <summary>ข้อเท็จจริงหัก ณ ที่จ่ายต่อบรรทัด (อัตรา % · ยอดภาษีหัก)</summary>
    public sealed record WhtLine(decimal Rate, decimal Amount);

    /// <summary>ภาษีหัก ณ ที่จ่ายตัดสินได้ชัดไหม — null = ชัด · ไม่ชัดเมื่อ: ผู้จ่ายออกภาษีแทน (ต้องคิดฐานรวม) · อัตราที่ไม่ใช่อัตราตามกฎหมาย ณ วันที่ใบ ·
    /// ยอดรวมหัวใบไม่ตรงผลรวมบรรทัด (เกิน 0.01) · มีภาษีหักที่หัวใบแต่ไม่มีอัตราบนบรรทัด</summary>
    public static string? WhtProblem(IReadOnlyList<WhtLine> lines, decimal headerWht, decimal payerBorne, DateTime documentDate)
    {
        if (payerBorne > 0m) return "ใบนี้ผู้จ่ายออกภาษีหัก ณ ที่จ่ายแทน — ให้คนตรวจฐานภาษีก่อนอนุมัติ";
        foreach (var l in lines.Where(l => l.Rate > 0m))
        {
            var v = ThaiWhtRateTable.ClassifyRate(l.Rate, documentDate);
            if (v.Standing is not (ThaiWhtRateTable.RateStanding.Statutory or ThaiWhtRateTable.RateStanding.TemporaryInForce))
                return "อัตราภาษีหัก ณ ที่จ่าย " + l.Rate.ToString("0.##", CultureInfo.InvariantCulture) + "% ไม่ใช่อัตราตามกฎหมาย — ให้คนตรวจประเภทเงินได้";
        }
        var lineSum = lines.Sum(l => l.Amount);
        if (headerWht > 0m && !lines.Any(l => l.Rate > 0m))
            return "มีภาษีหัก ณ ที่จ่ายที่หัวใบแต่ไม่ระบุอัตราที่รายการ — ให้คนตรวจ";
        if (Math.Abs(Math.Round(headerWht, 2, MidpointRounding.AwayFromZero) - Math.Round(lineSum, 2, MidpointRounding.AwayFromZero)) > 0.01m)
            return $"ภาษีหัก ณ ที่จ่ายหัวใบ {headerWht:N2} ไม่ตรงผลรวมรายการ {lineSum:N2} — ให้คนตรวจ";
        return null;
    }

    /// <summary>หมายเหตุภายในบนใบที่ยังไม่อนุมัติเอง (เหตุจาก <see cref="Decide"/>)</summary>
    public static string HoldNote(string reason, DateTime nowUtc)
        => $"{HoldMarker} {nowUtc.AddHours(7):dd/MM/yyyy HH:mm}: ⚙️ ระบบยังไม่อนุมัติใบนี้เอง — {reason} · ตรวจแล้วกดอนุมัติเองได้ตามปกติ";

    /// <summary>หมายเหตุภายในบนใบที่ระบบอนุมัติเอง</summary>
    public static string ApprovedNote(string grantorName, decimal? cap, DateTime nowUtc)
        => $"{ApprovedMarker} {nowUtc.AddHours(7):dd/MM/yyyy HH:mm}: ⚙️ ระบบอนุมัติใบนี้เองตามความยินยอมของ {grantorName}"
           + (cap is decimal c ? $" (เพดาน {c:N2} บาท/ใบ)" : "")
           + " — ผังบัญชีทุกบรรทัดมีหลักฐาน · ไม่มีข้อสังเกตก่อนอนุมัติ · ภาษีซื้อยังรอต้นฉบับใบกำกับตามเดิม · ไม่ถูกต้องให้ยกเลิกใบนี้";

    /// <summary>แจ้งเตือน "ยังไม่อนุมัติเอง" เมื่อเหตุเปลี่ยนเท่านั้น (ครั้งแรกต่อเหตุ · ไม่สแปมทุกห้านาที)</summary>
    public static bool ShouldAnnounceHold(string? previousCode, string code) => !string.Equals(previousCode, code, StringComparison.Ordinal);
}
