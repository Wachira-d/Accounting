using Accounting.Models.Enums;

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
    /// <param name="RowFiscalYears">ปีภาษีของแถวที่จัดสรร + รอบของวันที่ใบแรกที่ลงแถวแล้ว (ฝ่ายค้าน WP-09 H2)</param>
    /// <param name="AmountNeedsReview">ยอดภาษีไม่ได้มาจาก เงินได้ × อัตรา (อ่านจากตัวอักษร/อ่านไม่ได้) ⇒ ไม่ auto (ฝ่ายค้าน WP-09 M4)</param>
    public sealed record GateInput(
        string? CertificateNumber, DateTime? CertificateDate,
        string? PayerTaxId, string? PayeeTaxId, bool? PayeeIsUs,
        decimal IncomeAmount, decimal WhtRate, decimal WhtAmount,
        WhtCertMatchLevel MatchLevel, decimal? ExtractionConfidence,
        int? CertFiscalYear, IReadOnlyCollection<int> RowFiscalYears,
        DateTime TodayUtc, bool AmountNeedsReview = false);

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
        else if (g.AmountNeedsReview) p.Add("ยอดภาษีไม่ได้มาจาก เงินได้ × อัตรา บนใบ (อ่านจากตัวอักษร) — ต้องให้คนตรวจกับใบจริง");
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
            p.Add("วันที่บนใบอยู่คนละรอบบัญชีกับรายการที่จับคู่ — ระบบนับงวดเครดิตตามวันที่ของแถว (ใบแรก) ไม่ใช่รายใบ · ต้องให้คนตรวจ (ลงเป็นรายการแยกถ้าคนละรอบ)");
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
    /// <param name="priorConfirmedFromPayer">จำนวนใบของผู้จ่ายรายนี้ที่ยืนยันแล้วก่อนหน้า — ป้ายของคำตอบนักเรียน (ฝ่ายค้าน WP-09 M6:
    /// นักเรียนตอบจากประวัติผู้จ่าย ไม่ได้อ่านกระดาษใบนี้ ⇒ ห้ามอ้างว่า "ตรวจแล้ว")</param>
    /// <param name="byProvenance">รอบ 264 P5: คำตอบมาจากการพิสูจน์ที่มา (<see cref="ProvenanceProblems"/> ว่าง) — ไม่ใช่ AI/นักเรียน</param>
    public static string AuthenticityLabel(string? answer, decimal? confidence, bool usedAi, int priorConfirmedFromPayer = 0, bool byProvenance = false)
    {
        if (answer == null) return "⚙️ ยังไม่ได้ตรวจของจริง — ผู้ใช้ตรวจใบแล้วกดยืนยัน";
        if (byProvenance && answer == Genuine && !usedAi) return ProvenanceLabel;
        var pct = confidence is decimal c ? $" ({c:P0})" : "";
        if (!usedAi)
            return $"⚙️ ผู้จ่ายรายนี้ยืนยันแล้ว {Math.Max(0, priorConfirmedFromPayer)} ใบ — ระบบไม่ได้ตรวจกระดาษใบนี้"
                + (answer == Genuine ? $" (ความมั่นใจจากประวัติ{pct})" : $" · ประวัติชี้ว่าน่าสงสัย{pct}");
        return $"🤖 AI ตรวจ: {(answer == Genuine ? "ดูเป็นใบจริง" : "น่าสงสัย")}{pct}";
    }

    /// <summary>ฝ่ายค้าน WP-09 M6: การยืนยันของผู้ใช้สอนนักเรียนว่า "ของจริง" ได้เฉพาะเมื่อผู้ใช้เปิดดูใบแล้ว และไม่ได้ทับคำตอบ "น่าสงสัย"</summary>
    public static bool ShouldTeachGenuine(bool viewedFile, string? authenticityAnswer)
        => viewedFile && authenticityAnswer != Suspect;

    /// <summary>ฝ่ายค้าน WP-09 รอบสอง H-B (ตัวเดียวของ H2): ใบใหม่ลงวันที่คนละรอบบัญชีกับใบแรกของแถว ⇒ ลงรวมแถว/แก้วันที่ข้ามรอบไม่ได้
    /// (<c>CitCalculation.CreditableInPeriod</c> นับงวดตามวันที่ใบแรกของแถว) · แถวยังไม่มีใบ (null) = ไม่ข้าม · ผู้เรียก: กล่องรับ (ยืนยัน/ยืนยันอัตโนมัติ/ทางรายการใหม่)
    /// และ <c>WhtCreditService.UpdateAsync</c></summary>
    public static bool CrossesCreditPeriod(DateTime? existingCertDate, DateTime newCertDate, int fiscalStartMonth)
        => existingCertDate is DateTime first && FiscalYearOf(first, fiscalStartMonth) != FiscalYearOf(newCertDate, fiscalStartMonth);

    /// <summary>ฝ่ายค้าน WP-09 รอบสอง M-D: ปีของใบสำหรับกันซ้ำ — รอบบัญชีของวันที่บนใบ · ไม่มีวันที่ ⇒ ปีภาษีของแถว (ถ้ามี) · ไม่รู้ทั้งคู่ = null</summary>
    public static int? CertYear(DateTime? certDate, int? taxYear, int fiscalStartMonth)
        => certDate is DateTime d ? FiscalYearOf(d, fiscalStartMonth) : taxYear is > 0 ? taxYear : null;

    /// <summary>ฝ่ายค้าน WP-09 รอบสอง M-D: เลขที่ใบเดียวกันของผู้จ่ายเดียวกัน "ซ้ำ" เฉพาะเมื่ออยู่ปีเดียวกัน (เลขที่ใบของผู้จ่ายเริ่มนับใหม่ทุกปีได้) ·
    /// ไม่รู้ปีฝั่งใดฝั่งหนึ่ง ⇒ ถือว่าซ้ำ (ทางปลอดภัย — ไม่ให้เครดิตสองครั้ง)</summary>
    public static bool SameCertYear(int? a, int? b) => a is null || b is null || a == b;

    public enum CertPayerMatch { Same, Different, Unknown }

    /// <summary>รอบ 257 ชุด 2 (B-M2): ใบเลขที่เดียวกันในกล่องรับเป็น "ผู้จ่ายเดียวกัน" ไหม — กุญแจ ไม่ใช่ความคล้ายชื่อ:
    /// เลขผู้เสียภาษีผู้จ่ายของใบใหม่อ่านได้ (13 หลัก) ⇒ ตัดสินด้วยเลขอย่างเดียว · อ่านไม่ได้ ⇒ ผู้ติดต่อ (ผู้ติดต่อที่จับคู่ได้ / ลูกค้าของลิงก์) ·
    /// ไม่รู้ทั้งสองอย่าง ⇒ <see cref="CertPayerMatch.Unknown"/> (เดิมถือว่าซ้ำ ⇒ ใบเลข "001" ของลูกค้าอื่นถูกกลืนเป็น "ได้รับแล้ว") —
    /// ผู้เรียกสร้างใบใหม่พร้อมด่าน "อาจซ้ำ" (ส่ง) หรือขอเลขผู้จ่ายก่อนยืนยัน (ยืนยัน)</summary>
    public static CertPayerMatch SameCertPayer(string? payerTaxId, Guid? payerContactId, string? otherPayerTaxId, Guid? otherPayerContactId)
    {
        var mine = ThaiTaxId.Normalize(payerTaxId);
        var other = ThaiTaxId.Normalize(otherPayerTaxId);
        if (mine.Length == 13 && other.Length == 13)
            return other == mine ? CertPayerMatch.Same : CertPayerMatch.Different;
        // ฝ่ายค้าน L5: ฝั่งหนึ่งมีเลข อีกฝั่งไม่มี ⇒ ไม่รู้ (ด่าน "อาจซ้ำ") — เดิมนับเป็น "ต่างผู้จ่าย" ⇒ ใบเดิมที่อ่านเลขไม่ได้กับใบใหม่ที่อ่านได้ลงซ้ำ
        if (mine.Length == 13 || other.Length == 13) return CertPayerMatch.Unknown;
        if (payerContactId is Guid c)
            return otherPayerContactId == c ? CertPayerMatch.Same
                : otherPayerContactId != null ? CertPayerMatch.Different : CertPayerMatch.Unknown;   // อีกใบไม่รู้ผู้ติดต่อ ⇒ ไม่รู้ (ไม่เดา)
        return CertPayerMatch.Unknown;
    }

    // ═══════════ รอบ 264 P5 (C-02 · B8 · คำตัดสิน 264-5-1): ใบจากคู่ค้า NextAcc ยืนยันด้วย "ที่มา" ไม่ใช่ AI ═══════════

    /// <summary>ความมั่นใจของคำตอบที่มาจากการพิสูจน์ที่มาด้วยฐานข้อมูล (ไม่ใช่การคาดเดา — แถวจริงในบริษัทผู้จ่าย)</summary>
    public const decimal ProvenanceConfidence = 1m;

    /// <summary>ส่วนต่างที่ยอมได้ระหว่างยอดบนใบที่ส่งมากับแถว 50 ทวิ ในบริษัทผู้จ่าย — สตางค์เดียวกัน (ตัวเลขล้วนไม่มีการสะกดผิด · F2 ข้อ 3)</summary>
    public const decimal ProvenanceAmountTolerance = 0.005m;

    /// <summary>ข้อเท็จจริงที่ <c>WhtCertIntakeService</c> อ่านจากฐาน (ไม่ใช่จากผู้ส่ง) เพื่อพิสูจน์ว่าใบช่อง <c>NextAccLink</c> มีอยู่จริงในบริษัทผู้จ่าย</summary>
    /// <param name="PartnershipAccepted">ผู้จ่าย (บริษัทที่เลขผู้เสียภาษีตรงใบ) เป็นคู่ค้าที่เชื่อมอยู่ (Accepted) กับเรา</param>
    /// <param name="LiveSourceCount">จำนวนใบเลขที่นี้ในบริษัทผู้จ่ายที่ออกแล้ว (ออก/พิมพ์ · ไม่ถูกลบ) และผู้ถูกหักคือผู้ติดต่อที่ผูกกับบริษัทเรา</param>
    /// <param name="SourceVoided">มีใบเลขที่นี้ (ผูกกับเรา) แต่ถูกยกเลิก/ลบแล้ว</param>
    /// <param name="Source">ใบที่ออกแล้ว (เมื่อ <paramref name="LiveSourceCount"/> = 1) · null = ไม่มี/กำกวม</param>
    public sealed record ProvenanceFacts(bool PartnershipAccepted, int LiveSourceCount, bool SourceVoided, SourceCertFacts? Source,
        string? OurTaxId, SubmittedCertFacts Submitted);

    /// <param name="Rate">อัตราจาก <c>PartnerExchangeRules.CertRate</c> (สูตรเดียวกับฝั่งส่ง) · <paramref name="CertificateDate"/> จาก <c>PartnerExchangeRules.WhtCertificateDate</c></param>
    public sealed record SourceCertFacts(decimal Income, decimal Tax, decimal Rate, DateTime? CertificateDate, WhtPayerFormType? Form, string? PayeeContactTaxId);

    public sealed record SubmittedCertFacts(string? PayeeTaxId, decimal Income, decimal Tax, decimal Rate, DateTime? CertificateDate, WhtPayerFormType Form);

    /// <summary>ปัญหาของการพิสูจน์ที่มา (ไทย) — ว่าง = ใบนี้คือแถว 50 ทวิ ที่ผู้จ่ายออกจริง เลขภาษีทั้งสองฝั่ง/ยอด/อัตรา/วันที่/แบบ ตรงทุกช่อง ⇒
    /// ผู้เรียกตั้งคำตอบ <see cref="Genuine"/> (<see cref="ProvenanceConfidence"/>) <b>โดยไม่เรียก orchestrator</b> · ไม่ว่าง ⇒ เป็นด่าน (รอคน · ไม่ถาม AI —
    /// ใบช่องคู่ค้าที่ยอดไม่ตรงแถวต้นทาง = ข้อมูลถูกแก้ระหว่างทาง ห้ามให้ AI "รับรอง" แทน)</summary>
    public static IReadOnlyList<string> ProvenanceProblems(ProvenanceFacts f)
    {
        var p = new List<string>();
        if (!f.PartnershipAccepted) { p.Add("ผู้จ่ายไม่ได้เป็นคู่ค้า NextAcc ที่เชื่อมอยู่กับบริษัทเรา — ยืนยันจากระบบของผู้จ่ายไม่ได้"); return p; }
        if (f.LiveSourceCount == 0)
        {
            p.Add(f.SourceVoided
                ? "ผู้จ่ายยกเลิกหนังสือรับรองเลขนี้ในระบบของผู้จ่ายแล้ว — ห้ามใช้เป็นเครดิตภาษี (ขอใบใหม่จากผู้จ่าย)"
                : "ไม่พบหนังสือรับรองเลขนี้ที่ออกให้บริษัทเราในระบบของผู้จ่าย");
            return p;
        }
        if (f.LiveSourceCount > 1 || f.Source is not { } s)
        {
            p.Add("พบหนังสือรับรองเลขนี้ในระบบของผู้จ่ายมากกว่าหนึ่งใบ — ระบบไม่เดาว่าใบไหน");
            return p;
        }
        var sub = f.Submitted;
        if (!ThaiTaxId.IsValid(f.OurTaxId) || !ThaiTaxId.Same(s.PayeeContactTaxId, f.OurTaxId))
            p.Add("เลขผู้เสียภาษีผู้ถูกหักบนใบในระบบของผู้จ่ายไม่ตรงเลขของบริษัทเรา");
        else if (!ThaiTaxId.Same(sub.PayeeTaxId, f.OurTaxId))
            p.Add("เลขผู้เสียภาษีผู้ถูกหักที่ส่งมาไม่ตรงใบในระบบของผู้จ่าย");
        if (Math.Abs(s.Tax - sub.Tax) > ProvenanceAmountTolerance) p.Add($"ยอดภาษีที่ส่งมา ({sub.Tax:N2}) ไม่ตรงใบในระบบของผู้จ่าย ({s.Tax:N2})");
        if (Math.Abs(s.Income - sub.Income) > ProvenanceAmountTolerance) p.Add($"ยอดเงินได้ที่ส่งมา ({sub.Income:N2}) ไม่ตรงใบในระบบของผู้จ่าย ({s.Income:N2})");
        if (Math.Abs(s.Rate - sub.Rate) > ProvenanceAmountTolerance) p.Add("อัตราที่ส่งมาไม่ตรงใบในระบบของผู้จ่าย");
        if (s.CertificateDate?.Date != sub.CertificateDate?.Date) p.Add("วันที่บนใบที่ส่งมาไม่ตรงใบในระบบของผู้จ่าย");
        if (s.Form != sub.Form) p.Add("แบบ (ภ.ง.ด.3/53) ที่ส่งมาไม่ตรงใบในระบบของผู้จ่าย");
        return p;
    }

    /// <summary>คำนำของด่านเมื่อพิสูจน์ที่มาไม่ได้ (ตัวเดียว — เทสต์อ้างได้)</summary>
    public const string ProvenanceGatePrefix = "ยืนยันจากระบบของผู้จ่ายไม่ได้: ";

    /// <summary>ป้ายของใบที่ยืนยันด้วยที่มา (ไม่ใช่ AI · ไม่ใช่นักเรียน) — กฎเหล็ก #1 ป้ายซื่อสัตย์</summary>
    public const string ProvenanceLabel = "⚙️ ยืนยันจากระบบของผู้จ่าย (คู่ค้า NextAcc) — แถวหนังสือรับรองในบริษัทผู้จ่ายตรงทุกช่อง · ไม่ได้ใช้ AI";

    /// <summary>ปีภาษี (รอบบัญชีของบริษัท) ของวันที่ — กติกาเดียวกับ <c>SyncWhtCreditReceivedAsync</c> (เดือนเริ่มรอบ 1–12)</summary>
    public static int FiscalYearOf(DateTime date, int fiscalStartMonth)
    {
        if (fiscalStartMonth is < 1 or > 12) fiscalStartMonth = 1;
        return date.Month >= fiscalStartMonth ? date.Year : date.Year - 1;
    }
}
