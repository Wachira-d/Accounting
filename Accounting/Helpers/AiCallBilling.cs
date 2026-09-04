using System.Linq.Expressions;
using Accounting.Models.Entities;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// **"แถวนี้คือการเรียก AI ที่เสียเงินจริงหรือเปล่า" — ตัวตัดสินตัวเดียวของระบบ**
///
/// ═══ ที่มา (บั๊กจริง · ผลตรวจ AI-02 / AI-03) ═══
/// ทุก path ของ orchestrator เขียนแถว <c>AiSuggestionFeedback</c> หมด (local
/// short-circuit · cache hit · skip · budget-exceeded · แถวลูกสังเคราะห์ของ bulk)
/// และ <b>27 endpoint heuristic</b> ใน <c>AiSuggestionController</c> ก็เขียนด้วย
/// โดยติดป้าย <c>Success</c> + <c>ProviderUsed = DeepSeek</c> ทั้งที่ไม่เคยยิง
/// provider เลย ⇒
/// <list type="bullet">
/// <item><c>AiBudgetGuard</c> นับเป็น call ที่เสียเงิน ⇒ <b>วันที่ผู้ใช้กดปุ่ม
///   แนะนำ (ฟรี) เยอะ daily cap เต็ม แล้วบล็อก AI ของจริงทั้ง tenant</b> — และ
///   ยิ่ง local model แม่นขึ้น (ยิง provider น้อยลง) cap ยิ่งเต็มเร็วขึ้น
///   ตรงข้ามกับเจตนาของกฎเหล็ก #1 ทุกประการ</item>
/// <item>รายงานขึ้น "สำเร็จ (เรียก AI)" ให้ call ที่ไม่เคยเกิด ⇒ ตัวชี้วัด
///   "<c>UsedAi</c> ลดลงเรื่อย ๆ" (กฎเหล็ก #1 ข้อ 6) อ่านไม่ได้</item>
/// </list>
///
/// ═══ ทำไมเป็น <see cref="Expression"/> ไม่ใช่เมธอดธรรมดา ═══
/// ตัวตัดสินนี้ต้องใช้ได้ทั้ง <b>ใน SQL</b> (นับ call ของวันนี้ — จะโหลดทั้งตาราง
/// มานับในหน่วยความจำไม่ได้) และ <b>ในเทสต์</b> (คอมไพล์แล้วรันกับ object ธรรมดา)
/// — เขียนสองที่เมื่อไรก็ drift เมื่อนั้น (บทเรียน "hash/signature มี canonical
/// function เดียว" ใน CLAUDE.md กฎเหล็ก #4 C)
///
/// <para>⚠️ <b>กติกาเดียวกันนี้ถูกเขียนซ้ำเป็น SQL ใน
/// <c>DatabaseMigrationHelper</c></b> เพื่อล้างแถวเก่าที่ติดป้ายผิดไว้แล้ว —
/// แก้กติกาที่นี่ต้องไปแก้ SQL นั้นด้วย (มีคอมเมนต์โยงถึงกันไว้ทั้งสองฝั่ง)</para>
/// </summary>
public static class AiCallBilling
{
    /// <summary>สถานะที่ "ยิง provider ไปแล้ว" — สำเร็จ · ล้มเหลว · คำตอบไม่ผ่าน
    /// ก็ล้วนเสียเงินไปแล้วทั้งสิ้น (จ่ายค่า token ตอนส่ง ไม่ใช่ตอนได้คำตอบที่ดี)
    ///
    /// <para><see cref="AiCallStatus.Cached"/> ไม่อยู่ในลิสต์ — คำตอบเดิมที่เอามาใช้ซ้ำ
    /// ไม่ได้ยิงใหม่ · <see cref="AiCallStatus.LocalServed"/> และ
    /// <see cref="AiCallStatus.Skipped"/> ไม่เคยยิงเลย</para></summary>
    public static bool IsProviderAttemptStatus(AiCallStatus s) =>
        s == AiCallStatus.Success || s == AiCallStatus.Failed || s == AiCallStatus.InvalidResponse;

    /// <summary>
    /// แถวที่ต้องนับเข้า daily call cap — ใช้ตรง ๆ กับ <c>.Where(...)</c> ของ EF
    ///
    /// <para>สามด่านซ้อนกัน เพราะแต่ละด่านปิดช่องคนละแบบ:</para>
    /// <list type="number">
    /// <item><b>สถานะ</b> — ต้องเป็นสถานะที่แปลว่ายิง provider ไปแล้ว</item>
    /// <item><b>ผู้ให้บริการ</b> — <c>None</c> แปลว่า heuristic/กติกาในบ้าน
    ///   (ด่านนี้จำเป็นแม้แก้ต้นทางเป็น <c>LocalServed</c> แล้ว เพราะ
    ///   <b>แถวเก่าในฐานข้อมูลยังเป็น Success อยู่</b> จนกว่า migration จะรัน —
    ///   และเซิร์ฟเวอร์รุ่นเก่าที่ยังไม่ deploy ก็ยังเขียนแบบเดิมอยู่)</item>
    /// <item><b>ไม่ใช่แถวลูกสังเคราะห์</b> — <c>SynthesiseChildFeedbackAsync</c>
    ///   แตกคำตอบ bulk 1 ครั้งเป็นแถวลูกหลายสิบแถว (เพื่อให้ distillation เรียน
    ///   รายบรรทัดได้) โดยชี้กลับหาแม่ผ่าน <c>CacheHitOfFeedbackId</c> —
    ///   แม่ถูกนับไปแล้ว นับลูกอีกคือ <b>นับ provider call เดียวหลายสิบครั้ง</b></item>
    /// </list>
    /// </summary>
    public static Expression<Func<AiSuggestionFeedback, bool>> BillableRow =>
        f => (f.Status == AiCallStatus.Success
                || f.Status == AiCallStatus.Failed
                || f.Status == AiCallStatus.InvalidResponse)
             && f.ProviderUsed != AiProviderType.None
             && f.CacheHitOfFeedbackId == null;

    /// <summary>
    /// **ลายเซ็นของ "แถวที่อ้างว่าเรียก AI แต่ไม่เคยเรียก"** — ใช้คัดแถวเก่าที่
    /// สะสมไว้ก่อนมี <see cref="AiCallStatus.LocalServed"/>
    ///
    /// <para>เกณฑ์ต้องแม่นเพราะเดาผิดฝั่งไหนก็เสียหาย: ปล่อยแถวจริงหลุด = cap
    /// ไม่กันของจริง · ตีแถวจริงเป็น local = นับต้นทุนขาด. ใช้ลายเซ็นของ
    /// <b>การเรียก HTTP ที่เกิดขึ้นจริง</b> สามอย่างพร้อมกัน — มี latency ·
    /// มี token · มีต้นทุน ซึ่ง call จริงมีครบเสมอ ส่วน heuristic เขียน 0 ไว้
    /// ตายตัวทั้งสามช่อง · และเว้นแถวลูกสังเคราะห์ที่ 0 โดยชอบธรรม</para>
    ///
    /// <para>SQL ที่ทำเรื่องเดียวกันอยู่ใน <c>DatabaseMigrationHelper</c> —
    /// ต้องแก้คู่กันเสมอ</para>
    /// </summary>
    public static bool LooksLikeMislabelledLocalRow(
        AiCallStatus status, AiProviderType provider,
        int? latencyMs, int? inputTokens, int? outputTokens, decimal? costUsd,
        Guid? cacheHitOfFeedbackId) =>
        status == AiCallStatus.Success
        && provider != AiProviderType.None
        && (latencyMs ?? 0) == 0
        && (inputTokens ?? 0) == 0
        && (outputTokens ?? 0) == 0
        && (costUsd ?? 0m) == 0m
        && cacheHitOfFeedbackId == null;
}
