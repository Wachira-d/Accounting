using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// **สลิปที่ลูกค้าส่งในแชต LINE OA ของบริษัท ⇒ ผูกกับเอกสารใบไหน — ตัวตัดสินเดียว** (pure · รอบ 260 · คำตัดสินเจ้าของ 260-1-1:
/// "ตรวจว่ายอดสลิปตรงกับเอกสารใบไหนบ้าง · ถ้ามีหลายใบให้ลูกค้ากดเลือก · ถ้ามีใบเดียวระบบจับ วิเคราะห์ และดำเนินการต่ออัตโนมัติ")
/// <list type="bullet">
/// <item><b>ผู้สมัคร</b> = เอกสารขายที่ค้างชำระของผู้ติดต่อที่ผูก LINE นั้นเท่านั้น (บริษัทเดียวกัน) — ผู้ติดต่ออื่นไม่เคยเป็นผู้สมัคร (ด่านซ้ำในตัวตัดสิน) ·
/// รับสลิปได้ไหมใช้ตัวตัดสินเดิม <see cref="DocumentSlipIntents.UploadProblem"/> (ร่าง/รออนุมัติ/ยกเลิก/ปฏิเสธ/ชำระครบ = ตัดออก)</item>
/// <item><b>ตรงยอด</b> = ยอดสลิปเท่ายอดค้างของใบ หรือยอดสุทธิหลังหัก ณ ที่จ่าย (ตัวคิดเดียวกับหน้าชำระ/พอร์ทัล — ผู้เรียกส่งมา) ตรงถึงสตางค์ ·
/// ปัด 2 ตำแหน่ง AwayFromZero · <b>ไม่จับคู่ผลรวมหลายใบ</b> (subset-sum กำกวม — คำตัดสิน 260-1-3)</item>
/// <item><see cref="Outcome.One"/> เฉพาะเมื่อยอดบนสลิป "มั่นใจ" (<see cref="OcrSlipText.Reading.Amount"/>) และตรงใบเดียว ⇒ ผู้เรียกแจ้งโอนผ่านประตูเดิม
/// (<c>SlipEvidenceService.SubmitAsync</c>) · ยอดไม่มั่นใจแต่ตรงบางใบ ⇒ <see cref="Outcome.Many"/> (ให้ลูกค้ากดยืนยันแม้มีใบเดียว) · ไม่ตรง/อ่านไม่ได้ ⇒
/// <see cref="Outcome.None"/> (คิวพนักงาน)</item>
/// <item>ระบบ<b>ไม่ประทับ "ชำระแล้ว"</b> เอง (F2 ข้อ 3) — สลิปเป็นรายการรอยืนยัน · ปิดหนี้ได้ทางเดิมเท่านั้น (คนยืนยัน · รายการเดินบัญชีจริงที่ <c>BankReceiptAutoSettle</c> จับเลขอ้างอิงได้)</item>
/// <item>ปุ่มเลือกใบ (postback) ลงลายเซ็น HMAC ผูก บริษัท + แหล่งแชต + สลิป + ใบ (<see cref="Sign"/>/<see cref="Verify"/> ตัวเดียวทั้งฝั่งออกและฝั่งตรวจ) ·
/// เซิร์ฟเวอร์ตรวจซ้ำกับแถวในฐาน (สถานะ · หมดอายุ · ใบอยู่ในชุดที่เสนอ · แหล่งแชตเดียวกัน)</item>
/// </list>
/// ผู้เรียก: <c>LineChatSlipService</c> · ผู้อ่านค่าตั้ง <c>CompanySettings.LineChatSlipIntakeEnabled</c>: <c>LineChatSlipService</c> + แถวความพร้อม <see cref="ReadinessRow"/>
/// </summary>
public static class LineChatSlipRule
{
    public const string RuleCode = "LINE-CHAT-SLIP";
    public const string AuditEntityType = "LineChatSlip";

    /// <summary>ตัวเลือกในแชตมีอายุเท่านี้ — เลยแล้วกดไม่ได้ (สลิปเข้าคิวพนักงานด้วยเหตุ "ลูกค้าไม่ได้เลือก")</summary>
    public static readonly TimeSpan ChoiceTtl = TimeSpan.FromHours(24);

    /// <summary>ใบที่เสนอให้เลือกสูงสุด (quick reply ของ LINE รับ 13 ปุ่ม — เหลือปุ่ม "ไม่ใช่ใบเหล่านี้")</summary>
    public const int MaxChoices = 10;

    /// <summary>รอบ 260 ฝ่ายค้าน M4 (คำตัดสิน 260-1-9): แถวที่ไม่ได้ผูกเอกสารเก็บรูปไว้ไม่เกินเท่านี้ — เลย ⇒ งานเบื้องหลังลบรูป + ปิดแถว (ไม่มีเหตุทางกฎหมายให้เก็บรูปที่ไม่ใช่หลักฐานบัญชี) ·
    /// แถวที่ผูกแล้ว/ปิดแล้วลบรูปทันที (สำเนาหลักฐานอยู่ที่ไฟล์แนบของเอกสาร)</summary>
    public const int UnmatchedRetentionDays = 90;

    /// <summary>ฝ่ายค้าน M2: แถว "กำลังแจ้งโอน" ที่ค้างนานเกินนี้ (ระบบล่มกลางทาง) ⇒ นับเป็นคิวพนักงาน</summary>
    public static readonly TimeSpan StuckSubmitting = TimeSpan.FromMinutes(10);

    /// <summary>ฝ่ายค้าน L2: แถว "LINE ยังไม่ผูก" ต่อบริษัทต่อวัน (เวลาไทย) สูงสุด — เกิน ⇒ ไม่เก็บ (คำตอบเดิมของรอบ 259) · แถวนี้ไม่แจ้งทีมรายแถว (รวมใน Autopilot)</summary>
    public const int UnboundDailyCap = 30;

    /// <summary>ฝ่ายค้าน L3: เพดานปุ่มเลือกใบต่อแหล่งแชต (ตารางกลาง)</summary>
    public const int PostbackPerMinute = 6, PostbackPerDay = 60;

    public static string PostbackRateKey(string sourceKey) => "co-line-slip-pb:" + sourceKey[..Math.Min(32, sourceKey.Length)];

    // ── ฝ่ายค้าน M4 (PDPA ม.39): แถว RoPA ของ "รับรูปสลิปจากแชต LINE" — ระบบสร้างครั้งแรกที่รับรูป (ไม่ทับที่บริษัทแก้ไว้) ──
    public const string RopaPurpose = "รับรูปสลิปโอนเงินที่ลูกค้าส่งในแชต LINE OA ของบริษัท เพื่อผูกกับเอกสารค้างชำระ";
    public const string RopaLegalBasis = "Contract";
    public const string RopaDataCategories = "รูปสลิป (ชื่อผู้โอน · เลขบัญชีบางส่วน · ยอด · วันที่ · เลขอ้างอิง) · hash ของ LINE userId/groupId · รูปอื่นที่ผู้ใช้ส่งมา";
    public const string RopaRetention = "ผูกเอกสารแล้ว/ปิดรายการ ⇒ ลบสำเนาในแชตทันที (หลักฐานอยู่ที่ไฟล์แนบเอกสารตามอายุเอกสารบัญชี) · ไม่ได้ผูก ⇒ ลบรูปเมื่อครบ 90 วัน";
    public const string RopaRecipients = "พนักงานผู้มีสิทธิ์ดูรายการรับชำระของบริษัท · LY Corporation (LINE — ผู้ส่งรูปผ่านแพลตฟอร์ม)";
    public const string RopaNote = "ระบบสร้างอัตโนมัติเมื่อรับรูปจากแชต LINE ครั้งแรก (รอบ 260) — ผู้ดูแลข้อมูลทบทวนได้ที่หน้า PDPA";

    /// <summary>ไฟล์รูปจากแชตใหญ่สุดที่ดึงมาอ่าน</summary>
    public const int MaxBytes = 10 * 1024 * 1024;

    // ── สถานะของแถว LineChatSlip (ชื่อ ไม่ใช่เลข) ──
    public static class Statuses
    {
        /// <summary>เสนอหลายใบ รอลูกค้ากดเลือก</summary>
        public const string AwaitingChoice = "AwaitingChoice";
        /// <summary>กำลังแจ้งโอน (กันกดซ้ำ/พนักงานกดพร้อมกัน — compare-and-set)</summary>
        public const string Submitting = "Submitting";
        /// <summary>แจ้งโอนแล้ว (รายการรอยืนยันในคิวเดียว) — ระบบจับเองหรือลูกค้าเลือก</summary>
        public const string Submitted = "Submitted";
        /// <summary>ยังไม่ผูกเอกสาร — คิวพนักงาน (มีเหตุ)</summary>
        public const string Unmatched = "Unmatched";
        /// <summary>พนักงานผูกกับเอกสารแล้ว (รายการรอยืนยันในคิวเดียว)</summary>
        public const string Assigned = "Assigned";
        /// <summary>พนักงานปิด (ไม่ใช่สลิป · จัดการทางอื่นแล้ว)</summary>
        public const string Dismissed = "Dismissed";

        /// <summary>แถวที่นับเป็น "ส่งเข้ามาแล้ว" ของด่านกันซ้ำฝั่งแชต — แถวที่ผูกใบแล้วกันซ้ำด้วยรายการรอยืนยันเอง
        /// (<c>SlipEvidenceRule.DuplicateStatuses</c> — ผู้ขายปฏิเสธสลิปแล้วส่งใหม่ได้) · ปิดโดยพนักงานแล้ว = ส่งใหม่ได้</summary>
        public static readonly string[] Live = { AwaitingChoice, Submitting, Unmatched };

    }

    // ── เหตุที่สลิปเข้าคิวพนักงาน ──
    public static class Reasons
    {
        public const string Unbound = "Unbound";
        public const string Unreadable = "Unreadable";
        public const string NoMatch = "NoMatch";
        public const string CustomerDeclined = "CustomerDeclined";
        public const string ChoiceExpired = "ChoiceExpired";
        public const string SubmitFailed = "SubmitFailed";
        public const string GrossBeforeWht = "GrossBeforeWht";
        public const string DuplicateRef = "DuplicateRef";
        public const string SystemError = "SystemError";
        public const string IntakeOff = "IntakeOff";

        public static string Label(string? code) => code switch
        {
            Unbound => "LINE นี้ยังไม่ได้ผูกกับผู้ติดต่อ — ไม่รู้ว่าเป็นลูกค้ารายไหน",
            Unreadable => "อ่านยอดจากรูปไม่ได้ (ไม่ใช่สลิป หรือรูปไม่ชัด)",
            NoMatch => "ยอดสลิปไม่ตรงกับเอกสารค้างชำระใบใดของลูกค้า",
            CustomerDeclined => "ลูกค้าบอกว่าไม่ใช่ใบที่ระบบเสนอ",
            ChoiceExpired => "ลูกค้าไม่ได้เลือกใบภายในเวลา",
            SubmitFailed => "ผูกกับเอกสารที่ตรงยอดไม่สำเร็จ",
            DuplicateRef => "เลขอ้างอิงที่อ่านได้ซ้ำกับสลิปที่รับไว้แล้ว (รูปต่างกัน) — เทียบรูปกับรายการเดิมก่อนผูก (OCR อาจอ่านเลขผู้รับเงิน/Ref1 ที่เหมือนกันทุกใบ)",
            SystemError => "ระบบขัดข้องระหว่างบันทึกการแจ้งโอน — ผูกเองได้",
            IntakeOff => "ร้านปิดรับสลิปทางแชตก่อนลูกค้ากดเลือกใบ",
            GrossBeforeWht => "ยอดสลิปเท่ายอดเต็มก่อนหัก ณ ที่จ่าย (ลูกค้าอาจไม่ได้หัก) — เกินยอดค้าง: ตรวจว่าจะรับส่วนเกินเป็นเงินรอจัดสรร หรือรอหนังสือ 50 ทวิ",
            _ => "ยังไม่ผูกเอกสาร",
        };
    }

    /// <summary>เอกสารผู้สมัครหนึ่งใบ — ยอดจากตัวคิดของหน้าชำระ (<c>DocumentPayLinkPolicy.AmountsFor</c>): <paramref name="NetDue"/> = ยอดที่ลูกค้าต้องโอน
    /// (= ยอดค้าง ซึ่งเป็นยอดหลังหัก ณ ที่จ่ายแล้ว) · <paramref name="GrossDue"/> = ยอดก่อนหัก (ยอดค้าง + WHT ที่คาดว่าจะหัก · ผู้ซื้อไม่มีหน้าที่หัก = เท่ายอดค้าง)</summary>
    public sealed record Candidate(Guid DocumentId, Guid ContactId, string Number, DateTime Date, DocumentStatus Status, decimal BalanceDue, decimal NetDue, decimal GrossDue);

    /// <summary>ใบที่ตรงยอด · <paramref name="Amount"/> = ยอดที่แจ้งโอน (ยอดที่ลูกค้าต้องโอนหลังหัก ณ ที่จ่าย)</summary>
    public sealed record SlipMatch(Candidate Doc, decimal Amount);

    public enum Outcome { None, One, Many }

    /// <param name="Reason">เหตุเมื่อ <see cref="Outcome.None"/> (<see cref="Reasons"/>)</param>
    public sealed record Decision(Outcome Outcome, IReadOnlyList<SlipMatch> Matches, string? Reason);

    private static decimal R(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    /// <summary>ตัดสินว่าสลิปตรงกับใบไหน — <paramref name="ownContactIds"/> = ผู้ติดต่อที่ผูก LINE นี้ (ว่าง = ยังไม่ผูก)</summary>
    public static Decision Decide(OcrSlipText.Reading? reading, IEnumerable<Candidate> candidates, IReadOnlyCollection<Guid> ownContactIds)
    {
        if (ownContactIds.Count == 0) return new Decision(Outcome.None, Array.Empty<SlipMatch>(), Reasons.Unbound);
        if (reading == null || !reading.HasAnyAmount) return new Decision(Outcome.None, Array.Empty<SlipMatch>(), Reasons.Unreadable);
        var amounts = reading.Amount is decimal sure ? new[] { R(sure) } : reading.Amounts.Select(R).Where(a => a > 0m).Distinct().ToArray();
        var matches = new List<SlipMatch>();
        var grossHit = false;
        static decimal? gross(Candidate c) => R(c.GrossDue) > R(c.NetDue > 0m ? c.NetDue : c.BalanceDue) + 0.005m ? R(c.GrossDue) : null;
        foreach (var c in candidates)
        {
            if (!ownContactIds.Contains(c.ContactId)) continue;                       // ใบของผู้ติดต่ออื่น — ไม่เคยเป็นผู้สมัคร
            if (DocumentSlipIntents.UploadProblem(c.Status, c.BalanceDue, c.BalanceDue) != null) continue;   // ร่าง/ยกเลิก/ชำระครบ ฯลฯ
            if (matches.Any(m => m.Doc.DocumentId == c.DocumentId)) continue;
            var net = R(c.NetDue > 0m ? c.NetDue : c.BalanceDue);
            if (amounts.Contains(net)) matches.Add(new SlipMatch(c, net));
            else if (gross(c) is decimal g && amounts.Contains(g)) grossHit = true;
        }
        // ยอดเต็มก่อนหัก ณ ที่จ่าย (ลูกค้าไม่หัก?) — เกินยอดค้าง ⇒ แจ้งโอนไม่ได้ · ไม่เดาว่าเงินส่วนเกินคืออะไร (คนตัดสิน: เงินรอจัดสรร/50 ทวิ) · แบบเดียวกับ BankReceiptAutoSettle "เสนอเท่านั้น"
        if (matches.Count == 0) return new Decision(Outcome.None, matches, grossHit ? Reasons.GrossBeforeWht : Reasons.NoMatch);
        var ordered = matches.OrderBy(m => m.Doc.Date).ThenBy(m => m.Doc.Number, StringComparer.Ordinal).Take(MaxChoices).ToList();
        // รอบ 260 ฝ่ายค้าน M3 (คำตัดสิน 260-1-7): ผูกเองเฉพาะภาพที่ "ดูเป็นสลิป" (เลขอ้างอิง/คำว่าสำเร็จ) — ภาพหน้าชำระ/QR ที่มียอดเดียว ⇒ ให้ลูกค้ากดยืนยัน 1 ปุ่ม
        return reading.Amount != null && reading.LooksLikeSlip && matches.Count == 1
            ? new Decision(Outcome.One, ordered, null)
            : new Decision(Outcome.Many, ordered, null);
    }

    // ═══ ปุ่มเลือกใบ (postback) — ลายเซ็นตัวเดียว ═══

    public const string PostbackPrefix = "ls1";
    /// <summary>ค่าใบใน postback ของปุ่ม "ไม่ใช่ใบเหล่านี้"</summary>
    public const string DeclineToken = "none";

    /// <summary>กุญแจลงนามของบริษัท — ได้จาก Channel Secret ของ OA บริษัท (ไม่ใช้ secret ตรง) · secret เปลี่ยน ⇒ ปุ่มเก่ากดไม่ได้ (หมดอายุอยู่แล้ว)</summary>
    public static byte[] KeyFrom(string channelSecret)
        => HMACSHA256.HashData(Encoding.UTF8.GetBytes(channelSecret), Encoding.UTF8.GetBytes("nextacc-line-chat-slip-v1"));

    private static string Payload(Guid companyId, string sourceKey, Guid slipId, Guid? documentId)
        => string.Join('|', companyId.ToString("N"), sourceKey, slipId.ToString("N"), documentId?.ToString("N") ?? DeclineToken);

    public static string Sign(byte[] key, Guid companyId, string sourceKey, Guid slipId, Guid? documentId)
    {
        var mac = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(Payload(companyId, sourceKey, slipId, documentId)));
        return Convert.ToBase64String(mac, 0, 16).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static bool Verify(byte[] key, Guid companyId, string sourceKey, Guid slipId, Guid? documentId, string? signature)
    {
        if (string.IsNullOrEmpty(signature)) return false;
        var expected = Encoding.ASCII.GetBytes(Sign(key, companyId, sourceKey, slipId, documentId));
        var given = Encoding.ASCII.GetBytes(signature);
        return expected.Length == given.Length && CryptographicOperations.FixedTimeEquals(expected, given);
    }

    /// <summary>ข้อมูล postback ของปุ่ม (≤ 300 ตัวอักษรตามข้อจำกัด LINE) — <paramref name="documentId"/> null = "ไม่ใช่ใบเหล่านี้"</summary>
    public static string PostbackData(byte[] key, Guid companyId, string sourceKey, Guid slipId, Guid? documentId)
        => string.Join('|', PostbackPrefix, slipId.ToString("N"), documentId?.ToString("N") ?? DeclineToken, Sign(key, companyId, sourceKey, slipId, documentId));

    public sealed record Postback(Guid SlipId, Guid? DocumentId, string Signature);

    /// <summary>postback นี้เป็นของสลิปจากแชตไหม (ขึ้นต้นด้วยคำนำหน้า) — ใช่แต่รูปผิด ⇒ <see cref="TryParse"/> คืน null</summary>
    public static bool IsOurs(string? data) => data != null && data.StartsWith(PostbackPrefix + "|", StringComparison.Ordinal);

    public static Postback? TryParse(string? data)
    {
        if (!IsOurs(data)) return null;
        var parts = data!.Split('|');
        if (parts.Length != 4) return null;
        if (!Guid.TryParseExact(parts[1], "N", out var slip)) return null;
        Guid? doc = null;
        if (parts[2] != DeclineToken)
        {
            if (!Guid.TryParseExact(parts[2], "N", out var d)) return null;
            doc = d;
        }
        return string.IsNullOrEmpty(parts[3]) ? null : new Postback(slip, doc, parts[3]);
    }

    /// <summary>ตัวเลือกยังกดได้ไหม — null = ได้</summary>
    public static string? ChoiceProblem(string status, DateTime? expiresAtUtc, DateTime nowUtc)
    {
        if (status != Statuses.AwaitingChoice) return "ตัวเลือกนี้ใช้ไปแล้ว";
        if (expiresAtUtc == null || expiresAtUtc <= nowUtc) return "ตัวเลือกนี้หมดอายุแล้ว";
        return null;
    }

    /// <summary>แจ้งทีม (E-06 เดียวกับสลิปพอร์ทัล) เมื่อสลิปจากแชตเข้าคิวพนักงาน</summary>
    public static (string Title, string Message) StaffNotice(string? reason, decimal? amount)
        => ("สลิปจากแชต LINE ยังไม่ผูกเอกสาร" + (amount is decimal a ? $" ยอด {Money(a)} บาท" : ""),
            Reasons.Label(reason) + " — ตรวจรูปสลิปแล้วผูกกับเอกสาร หรือปิดรายการ ที่หน้ารายการรับชำระออนไลน์ (ระบบไม่ปิดหนี้จากสลิปเอง)");

    /// <summary>ยอดทั้งหมดที่อ่านได้ เป็นข้อความให้พนักงานเห็น</summary>
    public static string? AmountsText(IReadOnlyList<decimal> amounts)
        => amounts.Count == 0 ? null : LineMessageLimits.Truncate(string.Join(" · ", amounts.Select(Money)), 500);

    // ═══ ข้อความตอบในแชต (ข้อความถึงลูกค้า · ยอดเงินแสดงเฉพาะเมื่อปลายทางเปิดแสดงยอด) ═══

    private static string Money(decimal v) => v.ToString("#,##0.00", CultureInfo.InvariantCulture);
    private static string AmountPart(decimal amount, bool showAmounts) => showAmounts ? $" ยอด {Money(amount)} บาท" : "";

    public static class Replies
    {
        public static string Submitted(string documentNumber, decimal amount, bool showAmounts)
            => $"ได้รับสลิปแล้วค่ะ — บันทึกการแจ้งโอนของ {documentNumber}{AmountPart(amount, showAmounts)} "
               + "รอทางร้านตรวจรายการเงินเข้า แล้วจะส่งใบเสร็จให้";

        public static string Choose(decimal? amount, bool showAmounts, int count)
            => "ได้รับสลิป" + (amount is decimal a ? AmountPart(a, showAmounts) : "") + $"แล้วค่ะ — ยอดนี้ตรงกับเอกสาร {count} ใบ "
               + "กรุณากดเลือกใบที่ชำระด้านล่าง (เลือกได้ภายใน 24 ชั่วโมง)";

        public const string ChooseOne = "ได้รับสลิปแล้วค่ะ — กรุณากดยืนยันเอกสารที่ชำระด้านล่าง (เลือกได้ภายใน 24 ชั่วโมง)";

        public const string Duplicate = "สลิปนี้ส่งเข้ามาแล้วค่ะ — ไม่ต้องส่งซ้ำ ทางร้านกำลังตรวจ";

        public const string StaffWillCheck = "ได้รับรูปแล้วค่ะ — ถ้าเป็นสลิปโอนเงิน ทางร้านจะตรวจและผูกกับเอกสารให้";

        public const string DownloadFailed = "ได้รับรูปแล้ว แต่ระบบดึงรูปไม่สำเร็จค่ะ — กรุณาส่งสลิปอีกครั้ง";

        public const string IntakeOff = "ตอนนี้ร้านปิดรับสลิปทางแชตแล้วค่ะ — ทางร้านจะตรวจสลิปที่ส่งมาและผูกกับเอกสารให้";

        public const string ChoiceInvalid = "ตัวเลือกนี้ใช้ไม่ได้แล้วค่ะ (หมดอายุหรือเลือกไปแล้ว) — ทางร้านจะตรวจสลิปและผูกกับเอกสารให้";

        public const string Declined = "รับทราบค่ะ — ทางร้านจะตรวจสลิปและผูกกับเอกสารที่ถูกต้องให้";

        public const string DeclineLabel = "ไม่ใช่ใบเหล่านี้";

        /// <summary>ป้ายปุ่ม (≤ 20 ตัวอักษรตาม LINE)</summary>
        public static string ChoiceLabel(string documentNumber) => documentNumber.Length <= 20 ? documentNumber : documentNumber[..19] + "…";

        /// <summary>ข้อความที่ขึ้นในแชตเมื่อกดปุ่ม — เลขที่ + วันที่ (พ.ศ.) · ยอดเฉพาะเมื่อแสดงยอด</summary>
        public static string ChoiceDisplay(string documentNumber, DateTime date, decimal amount, bool showAmounts)
            => LineMessageLimits.Truncate($"เลือก {documentNumber} ลงวันที่ {ThaiDate.ToThaiDisplayString(date)}{AmountPart(amount, showAmounts)}", 300);
    }

    // ═══ ความพร้อม (แท็บ LINE ของหน้าตั้งค่า) ═══

    /// <summary>แถวความพร้อม "รับสลิปจากแชต" — server computes, page displays</summary>
    public static LineOaHealth.Row ReadinessRow(bool intakeEnabled, bool oaReady, bool webhookSeen, bool ocrAvailable)
    {
        if (!intakeEnabled)
            return new LineOaHealth.Row("chat-slip", LineOaHealth.LevelWarn, "ปิดรับสลิปจากแชต LINE อยู่",
                "ลูกค้าที่ส่งสลิปในแชตจะได้คำตอบให้แนบสลิปที่หน้าชำระของเอกสารแทน");
        if (!oaReady || !webhookSeen)
            return new LineOaHealth.Row("chat-slip", LineOaHealth.LevelWarn, "รับสลิปจากแชต LINE: เปิดไว้ แต่ยังไม่ทำงาน",
                "ต้องให้ LINE OA ของบริษัทพร้อมและ webhook เข้าจริงก่อน (ดูแถวด้านบน)");
        if (!ocrAvailable)
            return new LineOaHealth.Row("chat-slip", LineOaHealth.LevelWarn, "รับสลิปจากแชต LINE: เครื่องนี้อ่านยอดจากรูปไม่ได้",
                "สลิปทุกใบจะเข้าคิว \"สลิปจากแชตที่ยังไม่ผูกเอกสาร\" ให้พนักงานผูกเอง (หน้ารายการรับชำระออนไลน์)");
        return new LineOaHealth.Row("chat-slip", LineOaHealth.LevelOk, "รับสลิปจากแชต LINE อัตโนมัติ",
            "ยอดตรงใบเดียว = แจ้งโอนให้เอง · หลายใบ = ลูกค้ากดเลือก · ไม่ตรง = เข้าคิวพนักงาน — ระบบไม่ปิดหนี้จากสลิปเอง");
    }
}
