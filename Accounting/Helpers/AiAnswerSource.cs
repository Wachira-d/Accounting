namespace Accounting.Helpers;

/// <summary>
/// **ใครเป็นคนตอบคำแนะนำนี้จริง** — AI ภายนอก · นักเรียน (โมเดลในบ้าน) · ไม่มีใครตอบ — ตัวตัดสินและข้อความตัวเดียว
/// (รอบ 201 ทีม AI · A-AI2/A-AI4 · H-4/H-6)
///
/// <para>กฎเหล็ก #1: ป้าย "🤖 AI แนะนำ" ขึ้นเฉพาะตอนเรียก AI จริง ไม่งั้นเป็น "⚙️ ระบบแนะนำ" — และหลักการข้อ 7
/// "ข้อความที่ระบุสาเหตุต้องตรวจสาเหตุนั้นจริง": เดิม bulk PV ขึ้น "AI ตอบกลับ JSON ไม่ valid" ทั้งที่ไม่เคยเรียก AI
/// และขึ้น "⚙️ ระบบ (local model)" ทั้งที่ไม่มีใครตอบเลย ⇒ ผู้ใช้เข้าใจผิดว่า AI เสีย</para>
///
/// <para>เซิร์ฟเวอร์คำนวณป้าย/ข้อความ · หน้าเว็บแสดงอย่างเดียว (หลักการข้อ 5)</para>
/// </summary>
public static class AiAnswerSource
{
    public enum Kind
    {
        /// <summary>ไม่มีใครตอบ — AI ไม่ได้ตอบ (ปิด/ล่ม/เกินงบ) และนักเรียนยังไม่มีคำตอบ</summary>
        None = 0,
        /// <summary>นักเรียน (โมเดลในบ้าน) ตอบ — <c>AiResponse.FromLocalModel</c></summary>
        Student = 1,
        /// <summary>AI ภายนอกตอบจริง — <c>AiResponse.UsedAi</c></summary>
        Ai = 2,
    }

    /// <summary>ตัดสินจากธงของ <c>AiResponse</c> — AI ชนะเมื่อธงขึ้นทั้งคู่ (เรียกครูจริงแล้ว)</summary>
    public static Kind Of(bool usedAi, bool fromLocalModel)
        => usedAi ? Kind.Ai : fromLocalModel ? Kind.Student : Kind.None;

    /// <summary>ป้ายผู้ตอบบนจอ</summary>
    public static string Label(Kind who) => who switch
    {
        Kind.Ai => "🤖 AI",
        Kind.Student => "⚙️ ระบบ (โมเดลในบ้าน)",
        _ => "ยังไม่มีคำแนะนำ",
    };

    /// <summary>ป้ายเมื่อไม่มีโมเดลตอบ แต่<b>กฎในบ้าน</b> (เช่น DurableGoodsHeuristic) ให้คำตอบบางบรรทัด</summary>
    public const string RuleLabel = "⚙️ ระบบ (กฎในบ้าน)";

    /// <summary>ป้ายของทั้งชุด — โมเดลตอบใช้ <see cref="Label"/> · ไม่มีโมเดลตอบแต่กฎตอบบางบรรทัด = <see cref="RuleLabel"/></summary>
    public static string LabelWithRules(Kind who, bool anyRuleAnswer)
        => who == Kind.None && anyRuleAnswer ? RuleLabel : Label(who);

    /// <summary>คำตอบที่ได้มาอ่านไม่ออก — บอกผู้ตอบจริง</summary>
    public static string UnreadableMessage(Kind who) => who switch
    {
        Kind.Ai => "AI ตอบกลับในรูปที่อ่านไม่ได้ — คงผังเดิมทุกบรรทัด",
        Kind.Student => "คำตอบของโมเดลในบ้านอ่านไม่ได้ — คงผังเดิมทุกบรรทัด",
        _ => "ยังไม่มีคำแนะนำ — คงผังเดิมทุกบรรทัด",
    };

    /// <summary>ผู้ตอบตอบไม่ครบ — นับรวมเป็นข้อความเดียว (เดิมขึ้นทีละบรรทัดพร้อม id ภายในที่ผู้ใช้อ่านไม่ออก)</summary>
    public static string MissingLinesMessage(Kind who, int missingLines) => who == Kind.Ai
        ? $"AI ไม่ตอบ {missingLines} บรรทัด — คงผังเดิมของบรรทัดเหล่านั้น"
        : $"โมเดลในบ้านยังไม่มีคำตอบ {missingLines} บรรทัด — คงผังเดิมของบรรทัดเหล่านั้น";

    /// <summary>ไม่มีใครตอบเลย — ข้อความเดียวแทน "AI ไม่ตอบบรรทัด…" ทีละบรรทัด (ซึ่งโทษ AI ที่ไม่ได้ถูกถาม)</summary>
    public static string NobodyAnsweredMessage(int lines)
        => $"ยังไม่มีคำแนะนำ: AI ไม่ได้ตอบ (ปิดอยู่/ไม่พร้อม) และโมเดลในบ้านยังไม่มีประวัติผังของรายการแบบนี้ — "
           + $"เลือกผังเอง {lines} บรรทัด แล้วระบบจะเรียนจากที่คุณเลือก";
}
