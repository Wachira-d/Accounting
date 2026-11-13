namespace Accounting.Helpers;

/// <summary>
/// รอบ 264 P3 (team-264-a2b-design §3.1 · คำตัดสิน 264-3-1 = ร่าง 264-2-4 · C-01) — <b>ตัวตัดสินเดียว "เงินออกจากบัญชีไหนของผู้ซื้อ"</b> เมื่อจ่ายใบซื้อตามการแจ้งของผู้ขายคู่ค้า ·
/// pure (ไม่รับ DbContext — ผู้เรียกโหลดข้อเท็จจริงมาให้ · ลายเซ็นตาม DECISION_DOCTRINE G6)
/// <list type="bullet">
/// <item><b>ไม่รู้ = บอกว่าไม่รู้</b> (F2 ข้อ 3): ไม่มีหลักฐาน ⇒ <see cref="Origin.Unknown"/> · ไม่มีค่าตั้งต้น · ไม่ preselect —
/// ไม่ว่าเงินสด 111 · เจ้าหนี้กรรมการ 21230 · "บัญชีแรกที่ใช้งาน" · บัญชีพัก (เดิม <c>PartnerPaymentNoticeService</c> เลือก "บัญชีแรก" ให้ — ค่าที่แต่งขึ้น)</item>
/// <item>ลำดับชั้นเป็น<b>ข้อมูล</b> (<see cref="Precedence"/> · G1) ไม่ใช่ลำดับบรรทัด · ชั้นบนชนะ + เก็บผู้แพ้ (<see cref="Decision.Losers"/>) ลง audit/หน้า (G2)</item>
/// <item>ชั้นที่ 3 (สลิปตรงบัญชีเดียว) ขัดชั้นที่ 4 (บัญชีที่ตั้งไว้ในความยินยอม) ⇒ <b>ไม่อัตโนมัติ</b> (สองหลักฐานระดับกลางขัดกัน — G4) ·
/// สลิปเป็นบัญชีที่ไม่ใช่ของบริษัท ⇒ ไม่อัตโนมัติ + เปิดตัวเลือก "กรรมการจ่ายแทน" ให้<b>คนเลือก</b> (G5) — ยกเว้นชั้นที่ 1 (เงินออกจริงตามรายการเดินบัญชี)</item>
/// <item>ประวัติ (บัญชีที่จ่ายผู้ขายรายนี้บ่อยสุด) + บัญชีที่ตั้งบนใบซื้อ = <b>คำแนะนำมีป้าย</b> เท่านั้น ไม่อัตโนมัติ ไม่ preselect</item>
/// <item>บัญชีทุกตัวต้องอยู่ในชุด <see cref="Facts.Usable"/> ของบริษัทผู้เรียก (ผู้เรียกกรองด้วยตัวตรวจบัญชีเดิม) — id ที่ไม่อยู่ในชุด = ไม่ใช่หลักฐาน</item>
/// <item><b>ไม่มีจุดเรียก AI</b> (ตัวเลขล้วน ห้าม fuzzy) ⇒ ไม่มี <c>AiFeatureKey</c> ใหม่ · ไม่ต้อง distillation · <see cref="Decision.UsedAi"/> = false เสมอ (แบบ 260-1-2) ·
/// ชั้น BankLine ป้อนแล้ว (รอบ 264 P4 · <c>BankPaymentLineEvidence</c> ⇒ <see cref="BankPaymentLineMatch"/>) · ชั้น PayerDeclared ป้อนแล้ว (P4 "จ่ายจากระบบของเรา" — <c>PartnerPaymentPlans</c>) · ชั้น Slip ป้อนแล้ว (รอบ 264 P2 ·
/// <c>PartnerPayablePipeline.SlipMatchAsync</c> ⇒ <c>SlipAccountMatch</c> จากผู้โอนบนสลิปที่ผู้ขายได้รับ)</item>
/// </list>
/// </summary>
public static class PartnerPaySourceAccount
{
    public const string RuleCode = "XT-PARTNER-PAY-SOURCE";

    /// <summary>เกณฑ์ความมั่นใจขั้นต่ำของการบันทึกเอง (กฎ #1 ข้อ 6 · DECISION_DOCTRINE G4)</summary>
    public const decimal AutoThreshold = 0.85m;

    /// <summary>ที่มาของบัญชี (ชื่อคงที่ — หน้า/audit ใช้ชื่อ · ไม่ใช่ตัวเลข)</summary>
    public enum Origin { BankLine, PayerDeclared, SlipUnique, ConsentDefault, HistoryHint, Unknown }

    /// <summary>ผลจับคู่สลิปของผู้ซื้อ (ผล <c>SlipAccountMatch</c> ของแพ็ก P2 · ตัวแปลงเดียว <c>PartnerPayablePipeline.SlipKindOf</c> · ไม่มีผลอ่าน ⇒ null)</summary>
    public enum SlipKind { None, Unique, Many, NotOurs, Unreadable }

    public sealed record Slip(SlipKind Kind, Guid? AccountId = null);

    /// <param name="Tier">ลำดับชั้น (เลขน้อย = แข็งกว่า)</param>
    /// <param name="AllowsAuto">ชั้นนี้บันทึกเองได้ไหม (เมื่อไม่ขัดกัน)</param>
    /// <param name="Confidence">ความมั่นใจเมื่อชั้นนี้ชนะโดยไม่มีข้อขัด</param>
    public sealed record PrecedenceRow(Origin Origin, int Tier, bool AllowsAuto, decimal Confidence, string Kind);

    /// <summary>ตารางลำดับชั้น (team-264-a2b-design §3.1) — ข้อมูลตัวเดียว · เทสต์อ้างตารางนี้</summary>
    public static IReadOnlyList<PrecedenceRow> Precedence { get; } = new[]
    {
        new PrecedenceRow(Origin.BankLine, 1, true, 1.00m, "ข้อเท็จจริง — เงินออกจากบัญชีนั้นจริง (รายการเดินบัญชี)"),
        new PrecedenceRow(Origin.PayerDeclared, 2, true, 0.95m, "ประกาศของคนก่อนจ่าย (เลือกตอนชำระจากระบบของเรา)"),
        new PrecedenceRow(Origin.SlipUnique, 3, false, 0.90m, "สลิปตรงบัญชีเดียวของเรา (preselect เท่านั้น · ไม่บันทึกเอง — ฝ่ายค้าน 264-P2 M1 · แบบ 268-1-1 จนกว่ารายการเดินบัญชี P4 ยืนยัน)"),
        // คำตัดสิน 268-1-1 (แทน 264-3-14): ชั้นนี้ไม่ใช่หลักฐานว่าเงินออกจากบัญชีนั้นจริง ⇒ preselect บนปุ่มเดียวได้ แต่ไม่บันทึกเอง จนกว่าจะมีชั้นรายการเดินบัญชี (P4)
        new PrecedenceRow(Origin.ConsentDefault, 4, false, 0.85m, "ค่าตั้งของคน — บัญชีจ่ายคู่ค้านี้ที่ตั้งไว้ในความยินยอม (preselect เท่านั้น · ไม่บันทึกเอง — 268-1-1)"),
        new PrecedenceRow(Origin.HistoryHint, 5, false, 0.50m, "สัญญาณอ่อน — เคยจ่ายผู้ขายนี้จากบัญชีนี้ / บัญชีบนใบซื้อ (คำแนะนำเท่านั้น)"),
        new PrecedenceRow(Origin.Unknown, 6, false, 0m, "ไม่ทราบ"),
    };

    public static PrecedenceRow Row(Origin o) => Precedence.First(r => r.Origin == o);

    /// <summary>ความมั่นใจเมื่อหลักฐานระดับกลางขัดกัน (ต่ำกว่าเกณฑ์อัตโนมัติ — G4)</summary>
    public const decimal ConflictConfidence = 0.60m;

    /// <param name="Usable">บัญชีของผู้ซื้อที่ใช้จ่ายได้ (ผู้เรียกกรอง: ของบริษัท · ใช้งาน · ไม่ถูกลบ) → ป้าย (ธนาคาร ••1234)</param>
    /// <param name="BankLine">รายการเดินบัญชีฝั่งเงินออกที่ตรงการแจ้งเดียว (P4 · ยังไม่มี ⇒ null)</param>
    /// <param name="BankLineLabel">ป้ายของบรรทัด (วันที่) · null ได้</param>
    /// <param name="PayerDeclared">บัญชีที่ผู้จ่ายเลือกตอนชำระจากระบบของเรา (P4 · ยังไม่มี ⇒ null)</param>
    /// <param name="PayerName">ชื่อผู้เลือก (ป้าย)</param>
    /// <param name="SlipMatch">ผลจับคู่สลิป (P2 · ยังไม่มี ⇒ null)</param>
    /// <param name="ConsentDefault">บัญชีจ่ายที่ผู้ให้ความยินยอมประกาศ (ผ่าน <c>PartnerConsent.UseCheck.DefaultPayBankAccountId</c> — มีค่าเฉพาะเมื่อยังใช้ได้)</param>
    /// <param name="ConsentDefaultProblem">ประกาศไว้แต่ใช้ไม่ได้แล้ว (<c>UseCheck.PayAccountProblem</c>) ⇒ ใช้หลักฐานเท่านั้น ไม่เดาบัญชีอื่น</param>
    /// <param name="History">บัญชีที่จ่ายผู้ขายรายนี้บ่อยสุด (คำแนะนำ)</param>
    /// <param name="PurchaseDocBank">บัญชีที่ตั้งบนใบซื้อ (คำแนะนำ — ทิศตรงข้ามของการถอด "บัญชีแรก": ใบที่ตั้งธนาคารไว้ยังเห็นธนาคารนั้นเป็นคำแนะนำ)</param>
    public sealed record Facts(
        IReadOnlyDictionary<Guid, string> Usable,
        Guid? BankLine = null, string? BankLineLabel = null,
        Guid? PayerDeclared = null, string? PayerName = null,
        Slip? SlipMatch = null,
        Guid? ConsentDefault = null, string? ConsentDefaultProblem = null,
        Guid? History = null,
        Guid? PurchaseDocBank = null);

    /// <summary>หลักฐานหนึ่งชั้นที่มีจริง (ชนะหรือแพ้)</summary>
    public sealed record Candidate(Origin Origin, Guid BankAccountId, string Label);

    /// <param name="BankAccountId">บัญชีที่ใช้ได้ (preselect บนหน้า + บันทึกเองเมื่อ <see cref="AllowsAuto"/>) · null = ไม่รู้ (ห้ามเติมค่าอื่นแทน)</param>
    /// <param name="Reason">ข้อความไทยที่โชว์/ลง audit ได้ตรง</param>
    /// <param name="Losers">ชั้นที่แพ้ (บัญชีต่างจากผู้ชนะ) — ลง audit/หน้า (G2)</param>
    /// <param name="HintBankAccountId">คำแนะนำ (ประวัติ/บัญชีบนใบซื้อ) — <b>ไม่ preselect</b></param>
    /// <param name="OfferDirectorAdvance">หน้าเปิดตัวเลือก "กรรมการจ่ายแทน (21230)" ให้คนเลือกเอง (สลิปเป็นบัญชีที่ไม่ใช่ของบริษัท — G5)</param>
    public sealed record Decision(Guid? BankAccountId, Origin Origin, bool AllowsAuto, decimal Confidence, string Reason, string? Label,
        IReadOnlyList<Candidate> Losers, Guid? HintBankAccountId, string? HintLabel, bool OfferDirectorAdvance)
    {
        /// <summary>ไม่มีจุดเรียก AI ในตัวตัดสินนี้ — ป้ายบนหน้าเป็น ⚙️ เสมอ</summary>
        public bool UsedAi => false;
    }

    /// <summary>ฝ่ายค้าน M3: ป้ายซื่อสัตย์ของชั้นที่ 4 — หลักฐาน = "ค่าตั้งของเรา + คำบอกของผู้ขาย" ไม่ใช่หลักฐานว่าเงินออกจากบัญชีนั้นจริง (ตัวเดียว: หน้า · audit · หมายเหตุการจ่าย)</summary>
    public const string ConsentDefaultLabelPrefix = "บัญชีตั้งต้นที่ตั้งไว้ตอนยินยอม + ผู้ขายแจ้งรับเงิน — ";
    public const string ConsentDefaultCaveat = " (ยังไม่ได้ตรวจกับรายการเดินบัญชีของเรา)";

    /// <summary>คำตัดสิน 268-1-1: เหตุที่ท่อไม่บันทึกเองเมื่อหลักฐานบัญชีมีแค่บัญชีตั้งต้นในความยินยอม (ตัวเดียว: เหตุบนแถว · หน้า · ป้ายหน้าความยินยอม)</summary>
    public const string ConsentDefaultNotAutoReason = "ยังไม่บันทึกเอง — หลักฐานบัญชีที่จ่ายมีแค่บัญชีตั้งต้นที่ตั้งไว้ตอนยินยอม (ยังไม่มีรายการเดินบัญชีของเรายืนยันว่าเงินออกจากบัญชีนั้น) · "
        + "บัญชีนั้นถูกเลือกไว้ให้แล้ว ตรวจแล้วกด “บันทึกจ่าย” ครั้งเดียว";

    /// <summary>ฝ่ายค้าน 264-P2 M1: เหตุที่ท่อไม่บันทึกเองเมื่อหลักฐานบัญชีคือสลิปตรงบัญชีเดียว (ตัวเดียว: เหตุบนแถว · หน้า)</summary>
    public const string SlipUniqueNotAutoReason = "ยังไม่บันทึกเอง — บัญชีมาจากเลขท้ายบนสลิปที่ผู้ขายได้รับ (อ่านจากรูป · ยังไม่มีรายการเดินบัญชีของเรายืนยัน) · "
        + "บัญชีนั้นถูกเลือกไว้ให้แล้ว ตรวจแล้วกด “บันทึกจ่าย” ครั้งเดียว";

    /// <summary>ป้ายหน้า “งานอัตโนมัติกับคู่ค้า” ของขอบเขตบันทึกจ่าย ขณะชั้นบัญชีตั้งต้นไม่บันทึกเอง (ห้าม silent no-op — ให้ความยินยอมแล้วต้องรู้ว่าผลจริงคืออะไร)</summary>
    public const string ConsentDefaultNotAutoNote = "ตอนนี้ระบบยังไม่บันทึกจ่ายเองจากบัญชีตั้งต้นที่ตั้งไว้ที่นี่ (คำตัดสิน 268-1-1) — จนกว่าจะตรวจกับรายการเดินบัญชีของเราได้ · "
        + "ระหว่างนี้บัญชีนี้ถูกเลือกไว้ให้ในปุ่ม “บันทึกจ่าย” ของการแจ้งแต่ละรายการ (กดครั้งเดียว) · เพดานและการถอนยังมีผลตามเดิม";

    /// <summary>ชั้นบัญชีตั้งต้นบันทึกเองได้ไหม (อ่านจากตาราง — ผู้อ่าน: ป้ายหน้า/ความยินยอม)</summary>
    public static bool ConsentDefaultAutoEnabled => Row(Origin.ConsentDefault).AllowsAuto;

    public const string UnknownReason = "ไม่ทราบแหล่งเงินออก — เลือกบัญชีที่จ่ายจริง หรือรอรายการเดินบัญชีของเรา (ระบบไม่เดาบัญชีให้ · ไม่ใช้เงินสด/บัญชีแรกแทน)";
    public const string SlipNotOursReason = "บัญชีบนสลิปไม่ใช่บัญชีของบริษัท (อาจเป็นกรรมการ/พนักงานจ่ายแทน) — เลือกเองว่าเงินออกจากไหน (เช่น กรรมการจ่ายแทน 21230)";
    public const string SlipVsConsentReason = "สลิปตรงบัญชีหนึ่ง แต่บัญชีที่ตั้งไว้ในความยินยอมเป็นอีกบัญชี — หลักฐานขัดกัน ระบบไม่บันทึกเอง (เลือกบัญชีที่จ่ายจริง)";
    public const string ConsentAccountUnusableReason = "บัญชีจ่ายที่ตั้งไว้ในความยินยอมใช้ไม่ได้แล้ว (ปิดใช้งาน/ถูกลบ) — ระบบไม่เลือกบัญชีอื่นแทน";

    public static Decision Decide(Facts f)
    {
        string L(Guid id) => f.Usable.TryGetValue(id, out var l) ? l : "บัญชี";

        var present = new List<Candidate>();
        if (InUsableSet(f.Usable, f.BankLine))
            present.Add(new(Origin.BankLine, f.BankLine!.Value, "จากรายการเดินบัญชี " + L(f.BankLine.Value) + (string.IsNullOrWhiteSpace(f.BankLineLabel) ? "" : " " + f.BankLineLabel)));
        if (InUsableSet(f.Usable, f.PayerDeclared))
            present.Add(new(Origin.PayerDeclared, f.PayerDeclared!.Value, "ผู้จ่ายเลือกตอนชำระ" + (string.IsNullOrWhiteSpace(f.PayerName) ? "" : " (" + f.PayerName + ")") + " — " + L(f.PayerDeclared.Value)));
        if (f.SlipMatch is { Kind: SlipKind.Unique, AccountId: Guid slipId } && f.Usable.ContainsKey(slipId))
            present.Add(new(Origin.SlipUnique, slipId, "จากสลิป: " + L(slipId)));
        if (InUsableSet(f.Usable, f.ConsentDefault))
            present.Add(new(Origin.ConsentDefault, f.ConsentDefault!.Value, ConsentDefaultLabelPrefix + L(f.ConsentDefault.Value) + ConsentDefaultCaveat));

        // คำแนะนำ (ไม่อัตโนมัติ · ไม่ preselect): ประวัติก่อน แล้วบัญชีบนใบซื้อ
        var hint = InUsableSet(f.Usable, f.History) ? f.History : InUsableSet(f.Usable, f.PurchaseDocBank) ? f.PurchaseDocBank : null;
        var hintLabel = hint is Guid h
            ? (h == f.History ? "เคยจ่ายผู้ขายนี้จาก " : "บัญชีที่ตั้งบนใบซื้อ: ") + L(h) + " (คำแนะนำ — ตรวจก่อนเลือก)"
            : null;
        var slipNotOurs = f.SlipMatch?.Kind == SlipKind.NotOurs;

        if (present.Count == 0)
        {
            var reason = slipNotOurs ? SlipNotOursReason
                : f.ConsentDefaultProblem != null ? ConsentAccountUnusableReason + " · " + UnknownReason
                : UnknownReason;
            return new Decision(null, Origin.Unknown, false, 0m, reason, null, Array.Empty<Candidate>(), hint, hintLabel, slipNotOurs);
        }

        var ordered = present.OrderBy(c => Row(c.Origin).Tier).ToList();
        var win = ordered[0];
        var losers = ordered.Skip(1).Where(c => c.BankAccountId != win.BankAccountId).ToList();
        var row = Row(win.Origin);

        // หลักฐานขัดกัน ⇒ ไม่รู้ (ไม่ preselect ฝั่งใด · ผู้สมัครทุกตัวอยู่ใน Losers ให้คนเลือก)
        Decision Conflict(string reason, bool offerDirector)
            => new(null, Origin.Unknown, false, ConflictConfidence, reason, null, ordered, hint, hintLabel, offerDirector);
        // G5: สลิปเป็นบัญชีที่ไม่ใช่ของบริษัท ⇒ ขัดทุกชั้นยกเว้นเงินออกจริง
        if (slipNotOurs && win.Origin != Origin.BankLine)
            return Conflict(SlipNotOursReason, true);
        // G4: สลิปขัดบัญชีที่ตั้งไว้
        if (win.Origin == Origin.SlipUnique && losers.Any(l => l.Origin == Origin.ConsentDefault))
            return Conflict(SlipVsConsentReason, false);
        // ผู้จ่ายประกาศบัญชีหนึ่ง สลิปเป็นอีกบัญชี
        if (win.Origin == Origin.PayerDeclared && losers.Any(l => l.Origin == Origin.SlipUnique))
            return Conflict(PayerVsSlipReason, false);

        var why = win.Label;
        // รอบ 264 P4 (คำตัดสิน 264-5-10): สลิปตรงบัญชีเดียวกับที่รายการเดินบัญชียืนยัน ⇒ ทางเดียวที่ชั้นสลิปถูกยกเป็นบันทึกเองได้ (ผ่านชั้นที่ 1) — บอกในเหตุ
        if (SlipConfirmedByBankLine(f)) why += " · สลิปตรงบัญชีเดียวกัน (ยืนยันกัน)";
        // ชั้น 1 ขัดชั้นอื่น ⇒ เงินออกจริงชนะ + ป้ายเตือน (ผู้แพ้ลง audit)
        if (win.Origin == Origin.BankLine && losers.Count > 0)
            why += " · ไม่ตรง" + string.Join("/", losers.Select(l => OriginLabel(l.Origin))) + " (รายการเดินบัญชีชนะ — เงินออกจริง)";
        var auto = row.AllowsAuto && row.Confidence >= AutoThreshold;
        // 268-1-1: ชนะด้วยบัญชีตั้งต้นอย่างเดียว ⇒ preselect + เหตุที่บอกตรง ๆ ว่าทำไมไม่บันทึกเอง (เหตุ = สิ่งที่ตรวจจริง)
        if (!auto && win.Origin == Origin.ConsentDefault)
            why = ConsentDefaultNotAutoReason + " · " + win.Label;
        // ฝ่ายค้าน 264-P2 M1: สลิปตรงบัญชีเดียว = อ่านจาก OCR 3–4 หลักของรูปในบริษัทผู้ขาย ⇒ preselect + เหตุตรง ๆ (ไม่บันทึกเองจนกว่ารายการเดินบัญชียืนยัน)
        if (!auto && win.Origin == Origin.SlipUnique)
            why = SlipUniqueNotAutoReason + " · " + win.Label;
        return new Decision(win.BankAccountId, win.Origin, auto, row.Confidence, why, win.Label, losers, hint, hintLabel, false);
    }

    /// <summary>รอบ 264 P4 (คำตัดสิน 264-5-10 · ฝ่ายค้าน 264-P2 M1 · 264-4-9): <b>ทางเดียว</b>ที่ชั้นสลิปถูกยกจาก preselect เป็นบันทึกเองได้ =
    /// รายการเดินบัญชีฝั่งเงินออก (<see cref="BankPaymentLineMatch"/> ผล Match) ยืนยันบัญชี<b>เดียวกับ</b>ที่สลิปตรง · ไม่มีรายการเดินบัญชี/คนละบัญชี/สลิปไม่ Unique ⇒ false
    /// (แถว <see cref="Origin.SlipUnique"/> ในตารางคง <c>AllowsAuto=false</c> — การยกเกิดผ่านชั้นที่ 1 เท่านั้น ไม่ใช่แก้ตาราง)</summary>
    internal static bool SlipConfirmedByBankLine(Facts f)
    {
        return f.SlipMatch is { Kind: SlipKind.Unique, AccountId: Guid slip } && f.BankLine is Guid line && line == slip && f.Usable.ContainsKey(slip);
    }

    /// <summary>บัญชีนี้อยู่ในชุดที่ใช้จ่ายได้ของบริษัทผู้เรียกไหม (null/นอกชุด = ไม่ใช่หลักฐาน)</summary>
    private static bool InUsableSet(IReadOnlyDictionary<Guid, string> usable, Guid? id) => id is Guid g && usable.ContainsKey(g);

    public const string PayerVsSlipReason = "บัญชีที่ผู้จ่ายเลือกไม่ตรงบัญชีบนสลิป — หลักฐานขัดกัน ระบบไม่บันทึกเอง (เลือกบัญชีที่จ่ายจริง)";

    /// <summary>ป้ายสั้นของที่มา (server computes · หน้าแสดง)</summary>
    public static string OriginLabel(Origin o) => o switch
    {
        Origin.BankLine => "รายการเดินบัญชี",
        Origin.PayerDeclared => "ที่ผู้จ่ายเลือก",
        Origin.SlipUnique => "สลิป",
        Origin.ConsentDefault => "บัญชีที่ตั้งไว้ในความยินยอม",
        Origin.HistoryHint => "ประวัติการจ่าย",
        _ => "ไม่ทราบ",
    };
}
