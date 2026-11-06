namespace Accounting.Helpers;

/// <summary>
/// รอบ 252 (WP-09 · AUTO_SEND_WHT_DESIGN §3.7 "Auto-confirm" + คำตัดสินเจ้าของ §11 ข้อ 1–2 · AUDIT_249 §2 #31) —
/// <b>ใบ 50 ทวิ ที่เข้ามา ยืนยันอัตโนมัติได้ไหม</b> (pure · ตัวตัดสินเดียว)
///
/// <para>ลำดับตายตัว: <b>ด่านเชิงกำหนดก่อนเสมอ</b> (<see cref="Gates"/> — checksum เลขผู้เสียภาษี mod-11 · ผู้ถูกหัก = เรา ·
/// ผู้จ่ายตรงคู่สัญญา · ยอด/อัตรา/ปีภาษีตรงแถวรอใบ · มีเลขที่+วันที่ · OCR มั่นใจ) — ไม่ผ่านข้อใด ⇒ <b>ไม่ถาม AI</b> และไม่ auto ·
/// ผ่านครบ ⇒ ถามการตรวจของจริง (<c>AiFeatureKey.WhtCertAuthenticity</c> — นักเรียนก่อน · AI เป็นครู) ⇒ ตอบ <see cref="Genuine"/> ด้วยความมั่นใจ
/// ≥ <see cref="AuthenticityThreshold"/> เท่านั้นจึงยืนยันอัตโนมัติ · ไม่มีคำตอบ/ไม่มั่นใจ (เช่นปิด provider ทุกตัว) ⇒ ผู้ใช้กดยืนยัน (ฟอร์มเติมครบ — กฎเหล็ก #3)</para>
/// <para>AI ไม่ได้ตัดสินแทนด่าน — คำตอบ AI ทำได้แค่ "ไม่ให้ auto" หรือ "ให้ auto หลังด่านผ่านแล้ว"</para>
/// </summary>
public static class WhtCertIntakeVerdict
{
    public const string Genuine = "Genuine";
    public const string Suspect = "Suspect";
    /// <summary>ความมั่นใจขั้นต่ำของช่องเงิน/เลขผู้เสียภาษีจาก OCR (กฎเหล็ก #3 ข้อ 3) และของคำตอบตรวจของจริง (กฎเหล็ก #1 ข้อ 6)</summary>
    public const decimal ExtractionThreshold = 0.85m;
    public const decimal AuthenticityThreshold = 0.85m;
    /// <summary>ส่วนต่างที่ยอมได้ระหว่าง ภาษี กับ เงินได้ × อัตรา (บาท)</summary>
    public const decimal RateTolerance = 1m;

    /// <param name="PayeeIsUs">ผู้ถูกหักบนใบ = บริษัทเรา: true · ไม่ใช่: false · อ่านไม่ได้: null</param>
    /// <param name="ExtractionConfidence">null = ข้อมูลไม่ได้มาจาก OCR (กรอกมือ/ข้อมูลโครงสร้าง) — ไม่ใช่เหตุให้ไม่ auto</param>
    /// <param name="CertFiscalYear">ปีภาษี (รอบบัญชีของเรา) ของวันที่บนใบ</param>
    /// <param name="RowFiscalYears">ปีภาษีของแถวที่จัดสรร</param>
    public sealed record GateInput(
        string? CertificateNumber, DateTime? CertificateDate,
        string? PayerTaxId, string? PayeeTaxId, bool? PayeeIsUs,
        decimal IncomeAmount, decimal WhtRate, decimal WhtAmount,
        WhtCertMatchLevel MatchLevel, decimal? ExtractionConfidence,
        int? CertFiscalYear, IReadOnlyCollection<int> RowFiscalYears,
        DateTime TodayUtc);

    /// <summary>ด่านที่ไม่ผ่าน (ข้อความไทย) — ว่าง = ผ่านทุกด่าน ⇒ ถามการตรวจของจริงได้</summary>
    public static IReadOnlyList<string> Gates(GateInput g)
    {
        var p = new List<string>();
        if (string.IsNullOrWhiteSpace(g.CertificateNumber)) p.Add("ไม่มีเลขที่หนังสือรับรอง");
        if (g.CertificateDate is null) p.Add("ไม่มีวันที่บนหนังสือรับรอง");
        else if (g.CertificateDate.Value.Date > g.TodayUtc.Date.AddDays(1)) p.Add("วันที่บนหนังสือรับรองเป็นวันในอนาคต");
        if (!ThaiTaxId.IsValid(g.PayerTaxId)) p.Add("เลขผู้เสียภาษีของผู้จ่ายไม่ครบ 13 หลักหรือเลขตรวจสอบ (checksum) ไม่ถูกต้อง");
        if (g.PayeeIsUs == false) p.Add("ผู้ถูกหักบนใบไม่ใช่บริษัทเรา");
        else if (g.PayeeIsUs is null && !ThaiTaxId.IsValid(g.PayeeTaxId)) p.Add("อ่านเลขผู้เสียภาษีของผู้ถูกหัก (บริษัทเรา) บนใบไม่ได้");
        if (g.WhtAmount <= 0m) p.Add("อ่านยอดภาษีที่หักไม่ได้");
        if (g.IncomeAmount <= 0m) p.Add("อ่านยอดเงินได้ไม่ได้");
        if (g.WhtAmount > 0m && g.IncomeAmount > 0m && g.WhtRate > 0m
            && Math.Abs(Math.Round(g.IncomeAmount * g.WhtRate / 100m, 2, MidpointRounding.AwayFromZero) - g.WhtAmount) > RateTolerance)
            p.Add("ภาษีบนใบไม่เท่ากับเงินได้ × อัตรา");
        if (g.WhtAmount > 0m && g.IncomeAmount > 0m && g.WhtAmount >= g.IncomeAmount) p.Add("ภาษีบนใบไม่น้อยกว่าเงินได้ (อ่านช่องสลับกัน?)");
        if (g.MatchLevel != WhtCertMatchLevel.Exact) p.Add(g.MatchLevel switch
        {
            WhtCertMatchLevel.Rejected => "ใบนี้ไม่ได้ออกให้บริษัทเรา",
            WhtCertMatchLevel.NoCandidate => "ไม่พบรายการรอ 50 ทวิ ที่ตรงกับใบ",
            WhtCertMatchLevel.Ambiguous => "จับคู่กับรายการรอใบได้มากกว่าหนึ่งแบบ",
            WhtCertMatchLevel.Partial => "ยอดบนใบเป็นบางส่วนของรายการ (ผ่อนงวด)",
            WhtCertMatchLevel.Over => "ยอดบนใบมากกว่ายอดที่บันทึกว่าถูกหัก",
            _ => "จับคู่ไม่ตรงพอดี",
        });
        if (g.ExtractionConfidence is decimal c && c < ExtractionThreshold)
            p.Add($"ความมั่นใจในการอ่านช่องเงิน/เลขผู้เสียภาษีต่ำ ({c:P0})");
        if (g.CertFiscalYear is int y && g.RowFiscalYears.Count > 0 && g.RowFiscalYears.Any(r => r != y))
            p.Add("วันที่บนใบอยู่คนละรอบบัญชีกับรายการที่จับคู่ (เครดิตใช้ในรอบของวันที่บนใบ)");
        return p;
    }

    public enum Outcome
    {
        /// <summary>ยืนยันอัตโนมัติ (ด่านผ่าน + ตรวจของจริงมั่นใจ)</summary>
        AutoConfirm,
        /// <summary>รอผู้ใช้กดยืนยัน</summary>
        AwaitUser,
        /// <summary>ตีกลับทันที (K0 — ไม่ใช่ใบของเรา)</summary>
        Reject,
    }

    /// <param name="authenticityAnswer">คำตอบที่ผ่าน <see cref="ParseAuthenticity"/> แล้ว · null = ไม่ได้ถาม/ไม่มีคำตอบ</param>
    /// <param name="answerUsable">คำตอบมาจาก AI จริงหรือนักเรียน (ไม่ใช่ค่าว่างของ orchestrator)</param>
    public static Outcome Decide(WhtCertMatchLevel level, IReadOnlyList<string> gateProblems,
        string? authenticityAnswer, decimal? authenticityConfidence, bool answerUsable)
    {
        if (level == WhtCertMatchLevel.Rejected) return Outcome.Reject;
        if (gateProblems.Count > 0) return Outcome.AwaitUser;
        if (!answerUsable || authenticityAnswer != Genuine) return Outcome.AwaitUser;
        if (authenticityConfidence is not decimal c || c < AuthenticityThreshold || c > 1m) return Outcome.AwaitUser;
        return Outcome.AutoConfirm;
    }

    /// <summary>ด่านรับคำตอบ (anti-hallucination) — รับเฉพาะ Genuine/Suspect (ไม่สนตัวพิมพ์) · อื่น ๆ = null (ไม่มีคำตอบ)</summary>
    public static string? ParseAuthenticity(string? answer)
    {
        var a = (answer ?? "").Trim().Trim('"').ToLowerInvariant();
        return a switch
        {
            "genuine" => Genuine,
            "suspect" => Suspect,
            _ => null,
        };
    }

    /// <summary>ป้ายซื่อสัตย์ของผลตรวจของจริง (กฎเหล็ก #1): "🤖 AI ตรวจ" เฉพาะเมื่อเรียก provider จริง · นักเรียน/ระบบ = "⚙️ ระบบตรวจ" ·
    /// ไม่ได้ตรวจ (ด่านเชิงกำหนดไม่ผ่าน/ไม่มีคำตอบ) = บอกตรง ๆ — หน้าเว็บแสดงข้อความนี้อย่างเดียว (ไม่มีสำเนาป้ายใน JS)</summary>
    public static string AuthenticityLabel(string? answer, decimal? confidence, bool usedAi)
    {
        if (answer == null) return "⚙️ ยังไม่ได้ตรวจของจริง — ผู้ใช้ตรวจใบแล้วกดยืนยัน";
        var who = usedAi ? "🤖 AI ตรวจ" : "⚙️ ระบบตรวจ";
        var what = answer == Genuine ? "ดูเป็นใบจริง" : "น่าสงสัย";
        return confidence is decimal c ? $"{who}: {what} ({c:P0})" : $"{who}: {what}";
    }

    /// <summary>ปีภาษี (รอบบัญชีของบริษัท) ของวันที่ — กติกาเดียวกับ <c>SyncWhtCreditReceivedAsync</c> (เดือนเริ่มรอบ 1–12)</summary>
    public static int FiscalYearOf(DateTime date, int fiscalStartMonth)
    {
        if (fiscalStartMonth is < 1 or > 12) fiscalStartMonth = 1;
        return date.Month >= fiscalStartMonth ? date.Year : date.Year - 1;
    }
}
