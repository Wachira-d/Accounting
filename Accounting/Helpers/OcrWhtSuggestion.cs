namespace Accounting.Helpers;

/// <summary>
/// **ที่มาของยอด/อัตราหัก ณ ที่จ่ายที่อยู่บนผลสแกนใบนี้** — ค่าที่ต้อง<b>เห็นได้</b>
/// ไม่ใช่เดาย้อนกลับจาก "ช่องไหนมีค่า"
///
/// <para>⚠️ ที่มา (D-3 · <c>DECISION_AUDIT_2026-09-18.md</c> §9.3): ข้อความ
/// <c>[WHT-SUGGEST]</c> เดิมเลือกประโยคด้วย <c>it != null</c> (มีรหัสประเภทเงินได้ ม.40
/// ไหม) แล้วสรุปว่า "ถ้ามีรหัส = กฎหมายสั่ง · ถ้าไม่มี = ประวัติผู้ขาย" — นั่นคือการ
/// <b>เดาที่มาจากช่องที่บังเอิญมีค่า</b> ซึ่งผิดทันทีที่ตัวเสนอสองตัวเขียนคนละช่อง
/// (ประวัติผู้ขายก็เติม <c>WhtIncomeTypeCode</c> ได้ · ตัวจัดหมวดก็ไม่เติมได้)
/// ⇒ ประโยคที่ระบุ "สาเหตุ" ต้องตรวจสาเหตุนั้นจริง (หลักการ 10 ข้อ · ข้อ 7)</para>
/// </summary>
public enum WhtEvidenceSource
{
    /// <summary>ไม่มีใครเสนอ — ใบนี้ไม่มีเรื่องหัก ณ ที่จ่าย (หรือยังไม่ได้ตรวจ)</summary>
    None = 0,

    /// <summary><b>กระดาษพิมพ์ไว้เอง</b> (<see cref="PaperWhtReader"/> อ่านได้) —
    /// หลักฐานชั้นบนสุด · เป็นคำประกาศของผู้ออกเอกสาร ไม่ใช่ข้อเสนอของเรา</summary>
    Paper = 1,

    /// <summary><b>ตารางกฎหมาย</b> (ท.ป.4/2528 ผ่าน <see cref="ThaiWhtRateTable"/>)
    /// สำหรับหมวดรายจ่ายที่ตัวจัดหมวดอ่านได้จากรายการบนกระดาษใบนี้ —
    /// ตัวเลขอัตราไม่มีทางผิด จุดอ่อนอยู่ที่ "หมวดที่อ่านมา"</summary>
    Statute = 2,

    /// <summary><b>นิสัยของผู้ขายรายนี้</b> จากเอกสารที่อนุมัติไปแล้ว —
    /// ไม่ใช่สิ่งที่กระดาษใบนี้พูด ⇒ เป็นได้แค่ <b>ข้อเสนอ</b> เสมอ</summary>
    VendorHistory = 3,

    /// <summary>ผู้ใช้กรอก/ยืนยันเอง — ชนะทุกข้อเสนอของระบบ (one-way governor)</summary>
    User = 4,
}

/// <summary>
/// **ด่านเดียวของ "ประวัติผู้ขายเสนออัตราหัก ณ ที่จ่ายได้ไหม"** (pure, ไม่มี I/O)
///
/// ═══ ทำไมต้องเข้มขึ้น (D-3 รอบ 184) ═══
/// <para>รอบ 183 ตัดวงจร "ประวัติยืนยันประวัติ" ไปแล้ว (เขียนลง <c>SuggestedWhtRate</c>
/// แทน <c>HasWht</c>) แต่<b>เกณฑ์การเสนอยังเป็นของเดิม</b> คือ
/// <c>VendorIntelligenceService.MediumConfidence</c> (0.65) และ<b>ไม่มีขั้นต่ำของขนาด
/// ตัวอย่างเลย</b> ⇒ ผู้ขายที่มีเอกสารใบเดียวในระบบได้
/// <c>WhtConfidence = max(p, 1-p) = 1.0</c> ทันที (บล็อก WHT ไม่มี <c>sizeFactor</c>
/// ต่างจากบล็อกชนิดเอกสาร/ผังบัญชีที่มี — ยืนยันแล้วใน §9.5)
/// ⇒ <b>ใบแรกของผู้ขายรายใหม่กลายเป็น "ประวัติ" ทันที</b></para>
///
/// <para>กติกาใหม่: <b>ประวัติใบเดียวไม่ใช่ประวัติ</b> — ต้องครบทั้งสองข้อพร้อมกัน
/// (<see cref="MinHistoryDocuments"/> และ <see cref="MinHistoryConfidence"/>)
/// และ<b>ต่อให้ครบก็ยังเป็นแค่ข้อเสนอ</b>: ความมั่นใจที่เขียนลงช่องของฟอร์มถูกตรึงไว้ที่
/// <see cref="SuggestionFieldConfidence"/> ซึ่งต่ำกว่าเกณฑ์ไฮไลต์เหลือง (0.85) เสมอ</para>
///
/// <para><b>ทำไมสองตัวเลขนี้ไม่ใช่ตัวเดียวกัน</b> — "มั่นใจแค่ไหนว่าผู้ขายรายนี้มีนิสัย
/// หักภาษี" กับ "มั่นใจแค่ไหนว่ายอดในช่องของ<b>ใบนี้</b>ถูก" เป็นคนละคำถาม · การยืมเลข
/// ตัวเดียวไปตอบสองคำถามคือ defect class "reuse field ผิดความหมาย" (กฎเหล็ก #4 E)
/// ⇒ ถ้ายืม ใบที่ผู้ขายมีนิสัยชัด (0.95) จะ<b>ไม่ขึ้นไฮไลต์เหลือง</b> ทั้งที่กระดาษ
/// ใบนั้นไม่ได้พิมพ์อะไรเรื่องหักภาษีเลย — ตรงข้ามกับกฎเหล็ก #3 ข้อ 3</para>
///
/// <para><b>ทางไปต่อของผู้ใช้ที่ถูกกัน</b> (F2 ข้อ 8): เมื่อด่านไม่ให้เสนอ ช่องอัตรา
/// ยังกรอกเองได้ตามปกติ · <c>[WHT-SUGGEST]</c> ของ<b>ตารางกฎหมาย</b> (หมวดรายจ่าย)
/// ยังทำงานเหมือนเดิมทุกประการ — ด่านนี้แคบเฉพาะ "ข้อเสนอจากนิสัยผู้ขาย"</para>
/// </summary>
public static class OcrWhtSuggestionGate
{
    /// <summary>จำนวนเอกสารขั้นต่ำของผู้ขายรายนั้นก่อนจะเรียกว่า "ประวัติ" —
    /// 3 ใบคือจุดที่ "เคยเกิด 2 ใน 3" ต่างจาก "เกิด 1 ใน 1" อย่างมีความหมาย
    /// (1 ใบ = ไม่มีตัวส่วน · 2 ใบ = 50/50 แปลว่าอะไรก็ได้)</summary>
    public const int MinHistoryDocuments = 3;

    /// <summary>ความเด่นของฝั่งข้างมากขั้นต่ำ — ยกจาก 0.65 เป็น 0.85 ตาม D-3
    /// (เท่ากับ <c>VendorIntelligenceService.HighConfidence</c> โดยตั้งใจ:
    /// ข้อเสนอที่แตะเงินภาษีต้องใช้เกณฑ์เดียวกับชั้น "มั่นใจสูง" ของระบบ)</summary>
    public const decimal MinHistoryConfidence = 0.85m;

    /// <summary>ความมั่นใจ<b>รายช่อง</b>ที่เขียนลง <c>FieldConfidence["WhtRate"]</c>
    /// เมื่อเสนอ — ต้อง &lt; 0.85 เสมอเพื่อให้ขึ้นไฮไลต์เหลือง
    /// "ตรวจสอบอีกครั้ง" (กฎเหล็ก #3 ข้อ 3) · 0.60 = ชั้น "medium" ของหน้า review</summary>
    public const double SuggestionFieldConfidence = 0.60;

    /// <summary>ผลการตัดสิน + เหตุผลภาษาไทยที่เอาไปลง trace/audit ได้ตรง ๆ</summary>
    public readonly record struct Verdict(bool Suggest, string Reason);

    /// <param name="paperAlreadyAnswered">กระดาษใบนี้พิมพ์ส่วนหักไว้แล้วไหม
    /// (จริง = ไม่ต้องเสนอ เพราะมีคำตอบชั้นบนกว่าแล้ว)</param>
    /// <param name="historyLeansToWithhold">ฝั่งข้างมากของประวัติคือ "หัก"</param>
    /// <param name="historyRate">อัตราที่ผู้ขายรายนี้ถูกหักบ่อยที่สุด</param>
    /// <param name="totalDocuments">จำนวนเอกสารทั้งหมดของผู้ขายในคลังประวัติ</param>
    /// <param name="dominance">ความเด่นของฝั่งข้างมาก 0–1 (<c>WhtConfidence</c>)</param>
    /// <remarks><b>ไม่มีพารามิเตอร์ "มีข้อเสนออยู่แล้วไหม" โดยตั้งใจ</b> — การแข่งกัน
    /// ระหว่างข้อเสนอของตารางกฎหมายกับของประวัติผู้ขายเป็นหน้าที่ของ
    /// <see cref="OcrFieldArbiter"/> (ลำดับชั้นเป็นข้อมูล) ไม่ใช่ของ "ใครเขียนก่อน"
    /// ⇒ ด่านนี้ตอบคำถามเดียว: "ประวัติของผู้ขายรายนี้หนักแน่นพอจะเสนอไหม"</remarks>
    public static Verdict Judge(
        bool paperAlreadyAnswered,
        bool historyLeansToWithhold, decimal? historyRate,
        int totalDocuments, decimal dominance)
    {
        if (paperAlreadyAnswered)
            return new(false, "กระดาษใบนี้พิมพ์ส่วนหัก ณ ที่จ่ายไว้แล้ว — ไม่ต้องเสนอจากประวัติ");
        if (!historyLeansToWithhold)
            return new(false, "ประวัติของผู้ขายรายนี้ไม่ได้เอนไปทาง 'ถูกหัก ณ ที่จ่าย'");
        if (historyRate is not > 0m)
            return new(false, "ประวัติบอกว่าเคยถูกหัก แต่ไม่มีอัตราที่ใช้บ่อย — ไม่เดาอัตราให้");
        if (totalDocuments < MinHistoryDocuments)
            return new(false,
                $"ผู้ขายรายนี้มีประวัติเพียง {totalDocuments} ใบ (ต้องมีอย่างน้อย {MinHistoryDocuments}) "
                + "— ใบเดียว/สองใบยังไม่ใช่ 'นิสัย'");
        if (dominance < MinHistoryConfidence)
            return new(false,
                $"ความเด่นของประวัติ {dominance:P0} ต่ำกว่าเกณฑ์ {MinHistoryConfidence:P0}");
        return new(true,
            $"ผู้ขายรายนี้ถูกหัก ณ ที่จ่าย {historyRate}% เป็นปกติ "
            + $"({dominance:P0} จาก {totalDocuments} ใบ) — ข้อเสนอ ไม่ใช่ยอดบนกระดาษใบนี้");
    }

    /// <summary>คำอธิบายที่มาเป็นภาษาไทย — <b>ทางเดียว</b>ที่ระบบพูดถึงที่มาของอัตรา
    /// (ห้ามให้หน้าจอ/ผู้เรียกแต่งประโยคเอง = สำเนาชุดที่สอง)</summary>
    public static string Describe(WhtEvidenceSource source) => source switch
    {
        WhtEvidenceSource.Paper => "อ่านจากส่วนหัก ณ ที่จ่ายที่พิมพ์บนกระดาษใบนี้",
        WhtEvidenceSource.Statute => "อัตราตามกฎหมาย (ท.ป.4/2528) ของหมวดรายจ่ายที่อ่านได้จากใบนี้",
        WhtEvidenceSource.VendorHistory => "ประวัติของผู้ขายรายนี้ — ไม่ใช่สิ่งที่กระดาษใบนี้พิมพ์ไว้",
        WhtEvidenceSource.User => "ผู้ใช้กรอก/ยืนยันเอง",
        _ => "ยังไม่มีข้อมูลเรื่องหัก ณ ที่จ่ายของใบนี้",
    };

    /// <summary>อ่านผลตัดสินของ <see cref="OcrFieldArbiter"/> กลับมาเป็น "ที่มา" ที่เก็บลงแถว
    ///
    /// <para>ทิศเดียวโดยตั้งใจ: มีแต่ <b>arbiter → ที่มา</b> เพราะชั้นที่เสนอประกาศ
    /// <see cref="OcrFieldSource"/> ของตัวเองอยู่แล้วตอน <c>Note()</c> — ฟังก์ชันแปลงกลับ
    /// จะเป็น helper ที่ไม่มีใครเรียก (F2 ข้อ 2: มี &#8800; ถูกเรียก)</para>
    ///
    /// <para>ผู้เสนอที่ไม่ใช่สี่ชั้นนี้ ⇒ <see cref="WhtEvidenceSource.None"/> — <b>ห้ามเดา</b></para></summary>
    public static WhtEvidenceSource FromFieldSource(OcrFieldSource source) => source switch
    {
        OcrFieldSource.UserConfirmed => WhtEvidenceSource.User,
        OcrFieldSource.PaperLabel => WhtEvidenceSource.Paper,
        OcrFieldSource.Statute => WhtEvidenceSource.Statute,
        OcrFieldSource.VendorHistory => WhtEvidenceSource.VendorHistory,
        _ => WhtEvidenceSource.None,
    };
}
