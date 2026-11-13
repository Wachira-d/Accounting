using System.Globalization;

namespace Accounting.Helpers;

/// <summary>
/// **ตัวตัดสิน "เงินโอนเข้าในรายการเดินบัญชี ⇒ ปิดหนี้ใบค้างเองได้ไหม" — ตัวเดียว** (รอบ 252 WP-12A · AUTOPILOT_BILLING_DESIGN §5.4 · รอบ 250 ของแผน) · pure · <b>ไม่เรียก AI</b>
/// (ตัวเลขล้วนไม่มีการสะกดผิด — F2 ข้อ 3 · kill-switch AI ไม่มีผล)
/// <para><b>AutoSettle</b> เมื่อครบทุกข้อ: (1) บรรทัดมาจากธนาคารจริง (statement ที่นำเข้า/feed — ไม่ใช่พิมพ์มือ/ไม่รู้แหล่ง) ·
/// (2) ยอดเงินเข้า = ยอดสุทธิที่ลูกค้าต้องโอนของใบ <b>ตรงเป๊ะ</b> (สูตรเดียวกับลิงก์ชำระ/QR — <c>DocumentPayLinkPolicy.AmountsFor(...).Net</c>) ·
/// (3) มีตัวระบุตัว ≥ 1: เลขเอกสารอยู่ในคำอธิบาย/อ้างอิง · ชื่อผู้โอนตรงผู้ติดต่อแบบมั่นใจ (<c>BankMatchScorer</c>) · เลขอ้างอิงโอนตรงสลิปที่ลูกค้าแจ้ง ·
/// (4) ยอดไม่ซ้ำ: ไม่มีใบค้างอื่น (ทุกผู้ติดต่อ) ที่ยอดสุทธิเท่ากันในกรอบวัน — ถ้ามี ต้องอ้าง<b>เลขเอกสาร</b>ใบเดียวเท่านั้น ·
/// (5) ไม่มีรายการชำระออนไลน์ค้างของใบ (กันนับซ้ำกับ gateway) · ผู้ติดต่อไม่ปิดการปิดหนี้อัตโนมัติ · <b>บริษัทเปิดค่าตั้ง</b> (ค่าตั้งต้นปิด — Q4)</para>
/// <para><b>Suggest</b> (คนยืนยัน 1 คลิก): ผ่านยอดแต่ขาดข้อใด · ยอดเต็มก่อนหัก ณ ที่จ่าย (ลูกค้าไม่หัก?) · ขาด/เกินไม่เกิน ฿50 หรือ 2% พร้อมตัวระบุตัว ·
/// ยอดเท่ากันหลายใบ (ให้คนเลือก) · <b>None</b>: อื่น ๆ · ไม่ปิดหลายใบรวม (subset-sum = backlog)</para>
/// </summary>
public static class BankReceiptAutoSettle
{
    public const string RuleAuto = "BANK-AUTO-SETTLE";
    public const string RuleSuggest = "BANK-AUTO-SUGGEST";
    /// <summary>รอบ 262 (คำตัดสิน 262-1-18): เงินเข้าที่ตรงได้แต่ใบค่าบริการ NextAcc ของผู้เช่า — ไม่ตัดชำระอัตโนมัติ (ข้ามการต่ออายุแพ็กเกจ) · ทางไปต่อของผู้ดูแล</summary>
    public const string PlatformTenantReason =
        "ยอดนี้ตรงได้เฉพาะใบค่าบริการ NextAcc ของผู้เช่า — ระบบไม่ตัดชำระอัตโนมัติ (ต้องผ่านรายการชำระค่าบริการเพื่อต่ออายุแพ็กเกจ) · "
        + "บันทึกที่ หน้าแอดมิน → เรียกเก็บค่าบริการ (ยืนยันการโอน/จับคู่รอบโอน) หรือให้ผู้เช่าแจ้งชำระที่ศูนย์บิล";
    public const string RuleNone = "BANK-AUTO-NONE";
    /// <summary>ฝ่ายค้านรอบสอง LOW: คนกดยืนยันข้อเสนอ (ไม่ใช่ระบบปิดเอง) — รหัสกติกาบนบรรทัดแยกจาก <see cref="RuleAuto"/></summary>
    public const string RuleConfirm = "BANK-CONFIRM-SUGGEST";

    // ── แหล่งของบรรทัด (BankTransaction.EntrySource) ──
    // ฝ่ายค้าน WP-12A H1 (คำตัดสินผู้ประสานงาน): statement ที่ผู้ใช้อัปโหลด/พิมพ์เอง ไม่ใช่หลักฐานให้ระบบปิดหนี้โดยไม่มีคน — เสนอได้อย่างเดียว
    // ยกเว้นผู้นำเข้าเองมีสิทธิ์อนุมัติใบชนิดนั้น ณ ตอนลงมือ (เท่ากับคนอนุมัติ) — ต่อไฟล์หน้าธนาคาร/feed ที่ปลายทางผู้ใช้ตั้งเอง
    /// <summary>ไฟล์ statement ที่อัปโหลดที่หน้าบัญชีธนาคาร (มีผู้อัปโหลด <c>CreatedByUserId</c>) — ปิดเองได้เมื่อผู้อัปโหลดมีสิทธิ์อนุมัติใบชนิดนั้น</summary>
    public const string SourceStatement = "Statement";
    /// <summary>ดึงจากธนาคารผ่านการเชื่อมต่อ feed — <b>เสนอเท่านั้น</b> (ฝ่ายค้านรอบสอง H-A · DECISIONS 252-WP12A-6: ยังไม่มีการเชื่อมธนาคารจริงที่ตรวจแล้ว ·
    /// ปลายทาง/บัญชีผูกกับเลขบัญชีไม่ได้ ⇒ ทบทวนเมื่อทำสัญญา API ธนาคารจริง)</summary>
    public const string SourceFeed = "Feed";
    /// <summary>ค่าเดิมของรอบก่อน (feed ที่ผู้ใช้ตั้ง URL) — ไม่มีผู้เขียนแล้ว · แถวที่มีค่านี้ = เสนอเท่านั้นเหมือน Feed</summary>
    public const string SourceFeedCustom = "FeedCustom";
    /// <summary>ไฟล์จากศูนย์นำเข้า/นำเข้า-ส่งออก/Open Banking (ไฟล์) — เสนอเท่านั้น</summary>
    public const string SourceUserFile = "UserFile";
    /// <summary>ระบบภายนอกผ่าน API v1 — เสนอเท่านั้น</summary>
    public const string SourceApi = "Api";
    public const string SourceManual = "Manual";

    /// <summary>บรรทัดนี้เป็นหลักฐานให้ระบบปิดหนี้เองได้ไหม — null = ได้ · ข้อความ = เหตุที่เสนอเท่านั้น ·
    /// <paramref name="creatorMayApprove"/> = ผู้นำเข้า/ผู้สั่งดึงบรรทัดนี้มีสิทธิ์อนุมัติใบชนิดนั้น ณ ตอนนี้ (ไม่รู้ผู้นำเข้า = false)</summary>
    private static string? SourceProblem(string? entrySource, bool creatorMayApprove) => entrySource switch
    {
        // DECISIONS 252-WP12A-6: ปิดเองได้ทางเดียว = ไฟล์ statement หน้าบัญชีธนาคารที่ผู้อัปโหลดมีสิทธิ์อนุมัติ
        SourceStatement => creatorMayApprove ? null
            : "รายการนี้นำเข้าโดยผู้ใช้ที่ไม่มีสิทธิ์อนุมัติใบชนิดนี้ (หรือไม่ทราบผู้นำเข้า) — ระบบไม่ปิดหนี้เอง ให้ผู้มีสิทธิ์ยืนยัน",
        SourceFeed or SourceFeedCustom =>
            "รายการนี้มาจากการเชื่อมต่อธนาคาร (feed) ที่ยังไม่ได้ตรวจยืนยันกับธนาคารจริง — ระบบไม่ปิดหนี้เอง ให้ผู้มีสิทธิ์ยืนยัน",
        _ => "รายการนี้ไม่ได้มาจากธนาคาร (พิมพ์มือ · ไฟล์/API ที่ผู้ใช้ส่งเอง · หรือไม่ทราบแหล่ง) — ระบบไม่ปิดหนี้เอง",
    };

    /// <summary>รอบ 264 P4 (คำตัดสิน 264-5-10): กติกาแหล่งของบรรทัด (H1) ตัวเดียวกันสำหรับฝั่งเงินออก (<c>BankPaymentLineMatch</c>) — null = บรรทัดเป็นหลักฐานได้ ·
    /// <paramref name="creatorMayApprove"/> = ผู้นำเข้ามีสิทธิ์อนุมัติเอกสารชนิดนั้น (ฝั่งเงินออก = ใบซื้อ) ณ ตอนนี้</summary>
    public static string? EvidenceProblem(string? entrySource, bool creatorMayApprove) => SourceProblem(entrySource, creatorMayApprove);

    /// <summary>ทางจับคู่ด้วยมือของหน้าธนาคารต้องไม่แตะบรรทัดที่ระบบกำลังบันทึกรับชำระ (ฝ่ายค้าน M5) — null = ทำได้</summary>
    public static string? ManualMatchBlock(string? verdict)
        => verdict == Stamps.Applying ? "รายการนี้กำลังถูกบันทึกรับชำระอัตโนมัติ — รอสักครู่แล้วโหลดหน้าใหม่" : null;

    public enum Verdict { None, Suggest, AutoSettle }

    /// <summary>ค่าที่ประทับใน <c>BankTransaction.AutoSettleVerdict</c> (คำตัดสิน + ผลหลังลงมือ)</summary>
    /// <summary>รอบ 257 ชุด 4 (A L12): ข้อความใต้สวิตช์ "ปิดหนี้อัตโนมัติ" — เปิดแล้วรอบถัดไปไล่บรรทัดเงินเข้าที่ยังไม่จับคู่<b>ย้อนหลัง</b> <paramref name="lookbackDays"/> วัน
    /// (รวมที่เคยเป็น "เสนอ" ไว้) ⇒ ใบที่เข้าเกณฑ์ถูกบันทึกรับชำระ — เดิมเปิดแล้วระบบปิดย้อนหลังโดยไม่บอก</summary>
    public static string EnableNote(int lookbackDays)
        => $"เปิดแล้ว รอบถัดไประบบจะไล่บรรทัดเงินเข้าที่ยังไม่จับคู่ย้อนหลัง {lookbackDays} วัน (รวมรายการที่เคยเสนอให้ยืนยันไว้) "
           + "และบันทึกรับชำระใบที่เข้าเกณฑ์ทันที — ดูผลจำลองก่อนเปิด";

    public static class Stamps
    {
        public const string None = "None";
        public const string Suggest = "Suggest";
        /// <summary>เข้าเกณฑ์ปิดอัตโนมัติ — กำลังบันทึกรับชำระ (ล้มกลางทาง ⇒ รอบหน้ากู้ด้วยเลขอ้างอิงการชำระ)</summary>
        public const string Applying = "Applying";
        public const string Settled = "Settled";
        public const string Failed = "Failed";
        public const string Rejected = "Rejected";
        /// <summary>ฝ่ายค้านรอบสอง M7: ประเมินล้มซ้ำครบ <see cref="PoisonFailures"/> ครั้ง — พักไว้ให้คนดู (อยู่ในคิว · ไม่ประเมินซ้ำอัตโนมัติ)</summary>
        public const string Held = "Held";
    }

    /// <summary>ประเมินล้มกี่ครั้งแล้วพักบรรทัด (<see cref="Stamps.Held"/>)</summary>
    public const int PoisonFailures = 3;
    /// <summary>ฝ่ายค้านรอบสอง M-E: ใบค้างยอดตรงเกินจำนวนนี้ และรายการไม่อ้างเลขเอกสาร ⇒ เสนอโดยไม่โหลดข้อเท็จจริงทุกใบ (คงขอบเขตงาน)</summary>
    public const int MaxExactForFacts = 8;
    public const string TooManyEqualReason = "มีใบค้างยอดเท่ากันจำนวนมากและรายการไม่อ้างเลขเอกสาร — เลือกใบเองในคิว";

    /// <summary>ทนยอด "ขาด/เกิน" ที่ยังเสนอให้คนดู (ค่าธรรมเนียมโอน/ส่วนต่าง WHT) — ไม่ใช่เกณฑ์ปิดหนี้</summary>
    public const decimal NearAbsolute = 50m;
    public const decimal NearPercent = 0.02m;
    /// <summary>ใบที่ลงวันที่หลังเงินเข้าเกินนี้ไม่ใช่ผู้สมัคร</summary>
    public const int DocumentAfterLineDays = 7;

    public sealed record Line(decimal Amount, DateTime Date, bool IsCredit, string? EntrySource, bool AlreadyMatched);

    /// <param name="ExpectedNet">ยอดที่ลูกค้าต้องโอน (= ยอดค้างสุทธิหลัง WHT — สูตรลิงก์ชำระ)</param>
    /// <param name="ExpectedGross">ยอดก่อนหัก ณ ที่จ่าย (= สุทธิ + WHT ที่ยังไม่ถูกหัก)</param>
    /// <param name="CitesDocumentNumber">เลขเอกสารอยู่ในคำอธิบาย/อ้างอิง/ผู้โอน (<see cref="CitesDocumentNumber(string?, string?)"/>)</param>
    /// <param name="NameConfident">ชื่อผู้โอนตรงผู้ติดต่อแบบมั่นใจ (<c>BankMatchScorer.Result.NameConfident</c>)</param>
    /// <param name="SlipRefMatched">เลขอ้างอิงโอนบนบรรทัดตรงสลิปที่ลูกค้าแจ้งของใบนี้</param>
    /// <param name="CreatorMayApprove">ผู้นำเข้าบรรทัดมีสิทธิ์อนุมัติใบชนิดของผู้สมัครนี้ (ตรวจ ณ ตอนตัดสิน) — ใช้กับแหล่ง Statement/FeedCustom</param>
    /// <param name="ContactIsSupplier">ผู้จ่ายเป็นผู้ขายของเราด้วย (เงินเข้าอาจเป็นเงินคืน/หักกลบ) ⇒ เสนอเท่านั้น</param>
    public sealed record Candidate(Guid DocumentId, string DocumentNumber, DateTime DocumentDate, decimal ExpectedNet, decimal ExpectedGross,
        bool CitesDocumentNumber, bool NameConfident, bool SlipRefMatched, bool HasPendingOnlinePayment, bool ContactAutoSettleDisabled,
        bool CreatorMayApprove = false, bool ContactIsSupplier = false)
    {
        public bool HasIdentity => CitesDocumentNumber || NameConfident || SlipRefMatched;
    }

    /// <param name="WouldAutoSettleIfEnabled">เข้าเกณฑ์ปิดเองทุกข้อ ยกเว้นบริษัทยังไม่เปิด — ใช้แสดง "ผลจำลอง 30 วัน"</param>
    public sealed record Result(Verdict Kind, Guid? DocumentId, string RuleCode, string Reason, bool WouldAutoSettleIfEnabled);

    private static string Money(decimal v) => v.ToString("N2", CultureInfo.InvariantCulture);

    /// <summary>เลขเอกสารอยู่ในข้อความของบรรทัดไหม — <b>ตามขอบคำ</b> (ฝ่ายค้าน LOW): แยกข้อความเป็นคำตามอักขระที่ไม่ใช่ตัวอักษร/ตัวเลข แล้วต้องมี
    /// "คำติดกัน" ที่ต่อกันได้เท่ากับเลขเอกสาร (ตัดสัญลักษณ์ · ไม่สนตัวพิมพ์) พอดี — <c>INV25691100123</c> ไม่นับเป็น <c>INV2569110012</c> ·
    /// เลขเอกสารสั้นกว่า 6 ตัวอักษรหลังตัด = ไม่นับ (กันชนตัวเลขทั่วไป)</summary>
    public static bool CitesDocumentNumber(string? lineText, string? documentNumber)
    {
        var doc = string.Concat(Tokens(documentNumber));
        if (doc.Length < 6) return false;
        var toks = Tokens(lineText);
        for (var i = 0; i < toks.Count; i++)
        {
            var acc = "";
            for (var j = i; j < toks.Count && acc.Length < doc.Length; j++)
            {
                acc += toks[j];
                if (acc.Length == doc.Length && acc == doc) return true;
            }
        }
        return false;
    }

    private static List<string> Tokens(string? s)
    {
        var list = new List<string>();
        var cur = new System.Text.StringBuilder();
        foreach (var ch in s ?? "")
        {
            if (char.IsLetterOrDigit(ch)) cur.Append(char.ToUpperInvariant(ch));
            else if (cur.Length > 0) { list.Add(cur.ToString()); cur.Clear(); }
        }
        if (cur.Length > 0) list.Add(cur.ToString());
        return list;
    }

    private static bool Exact(decimal a, decimal b) => Math.Round(a, 2, MidpointRounding.AwayFromZero) == Math.Round(b, 2, MidpointRounding.AwayFromZero);

    public static Result Decide(Line line, IReadOnlyList<Candidate> candidates, bool companyEnabled)
    {
        if (!line.IsCredit || line.Amount <= 0m)
            return new Result(Verdict.None, null, RuleNone, "ไม่ใช่เงินเข้า", false);
        if (line.AlreadyMatched)
            return new Result(Verdict.None, null, RuleNone, "รายการนี้จับคู่แล้ว", false);

        var pool = (candidates ?? Array.Empty<Candidate>())
            .Where(c => c.ExpectedNet > 0m && c.DocumentDate.Date <= line.Date.Date.AddDays(DocumentAfterLineDays))
            .ToList();
        var exact = pool.Where(c => Exact(c.ExpectedNet, line.Amount)).ToList();

        if (exact.Count == 0)
        {
            // ยอดเต็มก่อนหัก ณ ที่จ่าย — ลูกค้าไม่หัก? (ผู้ซื้อนิติบุคคลมีหน้าที่หัก — ไม่เดาแทน)
            var gross = pool.Where(c => c.ExpectedGross > c.ExpectedNet && Exact(c.ExpectedGross, line.Amount)).ToList();
            if (gross.Count == 1)
                return new Result(Verdict.Suggest, gross[0].DocumentId, RuleSuggest,
                    $"ยอดเท่ายอดก่อนหัก ณ ที่จ่ายของ {gross[0].DocumentNumber} ({Money(gross[0].ExpectedGross)}) — ลูกค้าไม่หัก ณ ที่จ่าย? ตรวจก่อนยืนยัน", false);
            // ขาด/เกินเล็กน้อย + มีตัวระบุตัว (ค่าธรรมเนียมโอน/หักต่างอัตรา) — ห้ามประทับว่าครบ
            var near = pool.Where(c => c.HasIdentity
                    && Math.Abs(c.ExpectedNet - line.Amount) <= Math.Max(NearAbsolute, Math.Round(c.ExpectedNet * NearPercent, 2, MidpointRounding.AwayFromZero)))
                .ToList();
            if (near.Count == 1)
            {
                var d = line.Amount - near[0].ExpectedNet;
                return new Result(Verdict.Suggest, near[0].DocumentId, RuleSuggest,
                    (d < 0 ? $"ขาด {Money(-d)}" : $"เกิน {Money(d)}") + $" จากยอดที่ต้องโอนของ {near[0].DocumentNumber} ({Money(near[0].ExpectedNet)}) — ไม่ปิดเอง ตรวจส่วนต่างก่อน", false);
            }
            return new Result(Verdict.None, null, RuleNone, "ไม่มีใบค้างที่ยอดตรง", false);
        }

        // (4) ยอดซ้ำหลายใบ ⇒ ต้องอ้างเลขเอกสารใบเดียวเท่านั้น
        Candidate pick;
        if (exact.Count > 1)
        {
            var cited = exact.Where(c => c.CitesDocumentNumber).ToList();
            if (cited.Count != 1)
                return new Result(Verdict.Suggest, null, RuleSuggest,
                    $"มีใบค้าง {exact.Count} ใบที่ยอดเท่ากัน ({Money(line.Amount)}) และรายการไม่อ้างเลขเอกสารใบเดียว — เลือกใบเองในคิว", false);
            pick = cited[0];
        }
        else pick = exact[0];

        string? hold = null;
        if (!pick.HasIdentity)
            hold = $"ยอดตรง {pick.DocumentNumber} แต่ไม่มีตัวระบุตัว (เลขเอกสาร/ชื่อผู้โอน/เลขอ้างอิงสลิป) — ยืนยันก่อนปิดหนี้";
        else if (SourceProblem(line.EntrySource, pick.CreatorMayApprove) is string srcWhy)
            hold = $"ยอดตรง {pick.DocumentNumber} แต่{srcWhy}";
        else if (pick.DocumentDate.Date > line.Date.Date)
            hold = $"ยอดตรง {pick.DocumentNumber} แต่ใบลงวันที่หลังวันเงินเข้า — จุดความรับผิดภาษี (tax point §78/1) อาจเป็นวันรับเงิน ตรวจวันที่ก่อนยืนยัน";
        else if (pick.ContactIsSupplier)
            hold = $"ยอดตรง {pick.DocumentNumber} แต่ผู้ติดต่อเป็นผู้ขายของเราด้วย (เงินเข้าอาจเป็นเงินคืน/หักกลบ) — ยืนยันเอง";
        else if (pick.HasPendingOnlinePayment)
            hold = $"ยอดตรง {pick.DocumentNumber} แต่ใบนี้มีรายการชำระออนไลน์ค้างอยู่ — ตรวจว่าไม่ใช่เงินก้อนเดียวกันก่อนยืนยัน";
        else if (pick.ContactAutoSettleDisabled)
            hold = $"ยอดตรง {pick.DocumentNumber} แต่ผู้ติดต่อนี้ตั้งไม่ให้ปิดหนี้อัตโนมัติ — ยืนยันเอง";
        if (hold != null) return new Result(Verdict.Suggest, pick.DocumentId, RuleSuggest, hold, false);

        var why = $"ยอดตรงยอดที่ต้องโอนของ {pick.DocumentNumber} ({Money(pick.ExpectedNet)}) · "
            + string.Join(" · ", new[]
            {
                pick.CitesDocumentNumber ? "อ้างเลขเอกสาร" : null,
                pick.NameConfident ? "ชื่อผู้โอนตรงผู้ติดต่อ" : null,
                pick.SlipRefMatched ? "เลขอ้างอิงตรงสลิปที่ลูกค้าแจ้ง" : null,
            }.Where(x => x != null));
        if (!companyEnabled)
            return new Result(Verdict.Suggest, pick.DocumentId, RuleSuggest, why + " — เข้าเกณฑ์ปิดอัตโนมัติ แต่บริษัทยังไม่เปิด (ยืนยันเอง)", true);
        return new Result(Verdict.AutoSettle, pick.DocumentId, RuleAuto, why, true);
    }

    /// <summary>ผลจำลองย้อนหลัง (อ่านอย่างเดียว) — ก่อนเปิดค่าตั้ง (Q4: ดู 30 วันก่อน)</summary>
    public sealed record Simulation(int Lines, int WouldAutoSettle, int WouldSuggest, int NoMatch);

    public static Simulation Summarize(IEnumerable<Result> results)
    {
        var list = (results ?? Array.Empty<Result>()).ToList();
        var auto = list.Count(r => r.WouldAutoSettleIfEnabled);
        var suggest = list.Count(r => r.Kind == Verdict.Suggest && !r.WouldAutoSettleIfEnabled);
        return new Simulation(list.Count, auto, suggest, list.Count - auto - suggest);
    }
}
