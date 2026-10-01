using System;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// **กติกาตัวเดียวของคลังจับคู่ธนาคาร (<c>BankReconciliationPattern</c>)** — ฝั่งเขียน (นับคำยืนยันแบบไหน)
/// และฝั่งอ่าน (ความเกี่ยวข้องของแพตเทิร์น · ความมั่นใจของนักเรียน) · OWNER file · pure ·
/// รอบ 201 ทีม AI · A-AI1 (H-1)
///
/// ═══ ของเดิมพังตรงไหน ═══
/// ปุ่ม "✨ AI จับคู่จากประวัติ" ติ๊กคู่ให้จากคลัง → ผู้ใช้กดยืนยันกลุ่ม → <c>TimesConfirmed</c> ของแพตเทิร์นที่
/// <b>เสนอเอง</b>เพิ่มขึ้น → คะแนน <c>0.2·min(1, TimesConfirmed/10)</c> และ Wilson ของนักเรียนโตเอง จนผ่านเกณฑ์
/// short-circuit 0.85 แล้วไม่มีครูมาขัดอีก (DOCTRINE §3 "ห้ามบันทึกแถวที่นักเรียนเองเป็นคนตอบแล้วผู้ใช้ปล่อยผ่าน"
/// · §3.1 กันคลังเอียงข้อ 1/3) — รอบ 178 แก้เรื่องเดียวกันให้ <c>AiSuggestionMemory</c> แต่ไม่ลามมาคลังธนาคาร
///
/// ═══ กติกา ═══
/// • ทุกการยืนยันยังนับเข้า <c>TimesConfirmed</c> (สถิติ "เคยเห็นคู่นี้") แต่ <b>ความมั่นใจนับเฉพาะ Explicit</b>
/// • ผู้เรียกที่ไม่บอกแหล่ง (API ภายนอก · ผู้เรียกเก่า) = <see cref="UserChoiceSource.Implicit"/> — ค่าที่ปลอดภัยที่สุด
///   (กติกาเดียวกับ <c>/ai-feedback/record</c>)
/// • แพตเทิร์นที่ไม่มีคำยืนยันแบบตั้งใจเลย ยังตอบได้ (cold-start ไม่ว่าง) แต่เพดาน
///   <see cref="ImplicitOnlyConfidenceCap"/> — ต่ำกว่าเกณฑ์ apply 0.70 และ short-circuit 0.85 เสมอ
/// </summary>
public static class BankPatternEvidence
{
    /// <summary>น้ำหนักสูงสุดของ "จำนวนครั้งที่ผู้ใช้เลือกคู่นี้เอง" ในความเกี่ยวข้องของแพตเทิร์น</summary>
    public const double ConfirmationWeight = 0.2;

    /// <summary>จำนวนคำยืนยันแบบตั้งใจที่ทำให้น้ำหนักข้างบนเต็ม</summary>
    public const int ConfirmationSaturation = 10;

    /// <summary>ความเกี่ยวข้องขั้นต่ำที่ยังนับว่าเป็นแพตเทิร์นของบรรทัดนี้</summary>
    public const double MinRelevance = 0.2;

    /// <summary>เพดานความมั่นใจของนักเรียนเมื่อแพตเทิร์นไม่มีคำยืนยันแบบตั้งใจเลย —
    /// เท่ากับเพดาน tier-2 ของ <c>GenericFeedbackDistillationModel</c> (0.45)</summary>
    public const decimal ImplicitOnlyConfidenceCap = 0.45m;

    /// <summary>แปลงแหล่งที่หน้าเว็บ/ผู้เรียกส่งมา — รับเฉพาะ<b>ชื่อ</b> enum (ไม่รับตัวเลข) ·
    /// ว่าง/อ่านไม่ออก = <see cref="UserChoiceSource.Implicit"/></summary>
    public static UserChoiceSource ParseSource(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return UserChoiceSource.Implicit;
        var s = raw.Trim();
        if (char.IsDigit(s[0]) || s[0] == '-') return UserChoiceSource.Implicit;
        return Enum.TryParse<UserChoiceSource>(s, ignoreCase: true, out var v) && Enum.IsDefined(v)
            ? v : UserChoiceSource.Implicit;
    }

    /// <summary>คำยืนยันครั้งนี้เพิ่ม <c>ExplicitConfirmCount</c> กี่ครั้ง (0 หรือ 1)</summary>
    public static int ExplicitIncrement(UserChoiceSource source)
        => source == UserChoiceSource.Explicit ? 1 : 0;

    /// <summary>ความเกี่ยวข้องของแพตเทิร์นกับบรรทัดธนาคาร 0..1 — <b>นับเฉพาะคำยืนยันแบบตั้งใจ</b>
    /// (เดิมนับ <c>TimesConfirmed</c> ⇒ กดผ่านรัว ๆ ดันคะแนนตัวเองขึ้นได้ถึง +0.2)</summary>
    /// <param name="jaccard">ความทับซ้อนของโทเค็นลายเซ็น 0..1</param>
    /// <param name="sameAmountBucket">ช่วงยอดเดียวกันไหม</param>
    /// <param name="explicitConfirms">จำนวนครั้งที่ผู้ใช้เลือกคู่นี้เอง</param>
    /// <param name="daysSinceLastUsed">กี่วันแล้วที่แพตเทิร์นนี้ถูกใช้ครั้งล่าสุด</param>
    public static double Relevance(double jaccard, bool sameAmountBucket, int explicitConfirms, double daysSinceLastUsed)
    {
        var j = Math.Clamp(jaccard, 0.0, 1.0);
        var bucketBoost = sameAmountBucket ? 0.3 : 0.0;
        var confirm = ConfirmationWeight * Math.Min(1.0, Math.Max(0, explicitConfirms) / (double)ConfirmationSaturation);
        var recencyDecay = Math.Max(0.5, 1.0 - Math.Max(0.0, daysSinceLastUsed) / 365.0);   // ครึ่งชีวิต ~1 ปี
        return (0.5 * j + bucketBoost + confirm) * recencyDecay;
    }

    /// <summary>ความมั่นใจของนักเรียน 0..1 ต่อคำตอบหนึ่งของกุญแจ (ลายเซ็น × ช่วงยอด) —
    /// Wilson ของ "สัดส่วนคำยืนยันแบบตั้งใจของคำตอบนี้ในกุญแจนั้น" · ไม่มีคำยืนยันแบบตั้งใจ ⇒ เพดาน
    /// <see cref="ImplicitOnlyConfidenceCap"/> (ใช้สัดส่วนของการยืนยันทั้งหมดแทน)</summary>
    public static decimal StudentConfidence(int explicitForAnswer, int explicitTotalForKey,
        int timesForAnswer, int timesTotalForKey)
    {
        if (explicitForAnswer > 0)
            return Wilson(explicitForAnswer, Math.Max(explicitForAnswer, explicitTotalForKey));
        var implicitScore = Wilson(Math.Max(0, timesForAnswer), Math.Max(Math.Max(0, timesForAnswer), timesTotalForKey));
        return Math.Min(ImplicitOnlyConfidenceCap, implicitScore);
    }

    /// <summary>ขอบล่าง Wilson 95% — สูตรเดียวกับนักเรียนตัวอื่น</summary>
    public static decimal Wilson(int successes, int n)
    {
        if (n <= 0 || successes <= 0) return 0m;
        const double z = 1.96;
        var p = (double)Math.Min(successes, n) / n;
        var denom = 1 + z * z / n;
        var center = p + z * z / (2 * n);
        var spread = z * Math.Sqrt((p * (1 - p) + z * z / (4 * n)) / n);
        return Math.Round((decimal)Math.Max(0, (center - spread) / denom), 4, MidpointRounding.AwayFromZero);
    }

    /// <summary>ข้อความเหตุผลบนจอ — บอกแยก "เลือกเอง" กับ "ยืนยันทั้งหมด" ให้ผู้ใช้รู้ว่าหลักฐานมีเท่าไรจริง</summary>
    public static string Reason(int explicitConfirms, int timesConfirmed, DateTime lastUsedAt)
    {
        var lastUsed = lastUsedAt.ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture);
        return explicitConfirms > 0
            ? $"ผู้ใช้เลือกคู่แบบนี้เอง {explicitConfirms} ครั้ง (ยืนยันรวม {timesConfirmed}) · ใช้ล่าสุด {lastUsed}"
            : $"ยังไม่มีใครเลือกคู่แบบนี้เอง (ยืนยันผ่าน {timesConfirmed} ครั้ง) · ใช้ล่าสุด {lastUsed}";
    }
}
