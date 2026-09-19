using System;

namespace Accounting.Helpers;

/// <summary>ใครเป็นคนทำให้รายการเดินบัญชีบรรทัดนี้ถูกจับคู่</summary>
public enum BankMatchActorKind
{
    /// <summary>**ไม่รู้** — แถวเก่าที่ไม่เคยบันทึกไว้ หรือค่าที่อ่านไม่ออก
    /// หน้าจอต้องแสดงว่า "ไม่มีข้อมูล" ห้ามเดาเป็น "ระบบ" (G3)</summary>
    Unknown = 0,
    /// <summary>เซิร์ฟเวอร์คำนวณเอง (ตัวให้คะแนน + arbiter) — ไม่ได้เรียก provider</summary>
    System = 1,
    /// <summary>เรียก AI ภายนอก/โมเดลจริงเพื่อให้ได้คำตอบนี้</summary>
    Ai = 2,
    /// <summary>คนกดยืนยัน</summary>
    Person = 3,
    /// <summary>ระบบภายนอกผ่าน API (`/api/v1`)</summary>
    Api = 4,
}

/// <summary>
/// **เจ้าของค่าเดียวของช่อง `BankTransaction.ReconciledBy`** — ทั้งฝั่งเขียน
/// (ค่าคงที่ที่ทุกเส้นต้องใช้) และฝั่งอ่าน (แปลงเป็นป้ายบนจอ).
///
/// ═══ ของเดิมพังตรงไหน (`DECISION_AUDIT_2026-09-18.md` §3 D4-8) ═══
/// 1. **ป้ายโกหก** — `BatchReconcileAsync` เขียน `"AI-Batch"` ทุกแถว
///    **แม้ `item.WasAiValidated == false`** ⇒ การจับคู่ที่คนเลือกเองล้วน ๆ
///    ถูกบันทึกว่า AI ทำ (และคนที่กดหายไปจากแถว ทั้งที่ audit row รู้ว่าใคร)
/// 2. **ไม่เคยเดินทางถึงจอ** — `BankTransactionResponse` ไม่มีช่องนี้เลย
///    ⇒ ผู้ใช้ตอบไม่ได้ว่า "ใครจับคู่ให้" (กฎเหล็ก #1: ป้าย "🤖 AI แนะนำ"
///    ต้องขึ้นเฉพาะตอนเรียกจริง ไม่งั้นต้องเป็น "⚙️ ระบบแนะนำ")
///
/// ═══ กติกา ═══
/// **เซิร์ฟเวอร์คำนวณป้าย · หน้าเว็บแสดงอย่างเดียว** (F2 ข้อ 5) — ห้าม JS
/// ถือสำเนาตารางป้าย. `Describe` รู้จักทั้งค่าที่เขียนใหม่และค่าเดิมในฐาน
/// (`"AutoMatch"` / `"BankFeed"` / `"AI-Batch"` / GUID ของผู้ใช้) เพราะแถวเก่า
/// ยังอยู่และห้ามเปลี่ยนความหมายย้อนหลัง
/// </summary>
public static class BankMatchAttribution
{
    // ── ค่าที่เขียนลงฐาน (ฝั่งเขียนต้องใช้ค่าคงที่เหล่านี้เท่านั้น) ──────
    public const string AutoMatch = "AutoMatch";
    public const string AutoMatchSuggested = "AutoMatch (เสนอ รอยืนยัน)";
    public const string BankFeed = "BankFeed";
    public const string BankFeedSuggested = "BankFeed (เสนอ รอยืนยัน)";
    public const string BankFeedAiSuggested = "BankFeed/AI (เสนอ รอยืนยัน)";
    public const string OpenBanking = "OpenBanking";
    public const string OpenBankingSuggested = "OpenBanking (เสนอ รอยืนยัน)";
    public const string ApiV1 = "api:v1";

    /// <summary>คำต่อท้ายที่บอกว่า "คนกดยืนยัน **คำแนะนำของ AI**" — ต่างจาก
    /// "คนเลือกเอง" เพราะเป็นข้อมูลของวงจรเรียนรู้ (ใครยอมรับคำตอบ AI บ้าง)</summary>
    public const string AiAssistedSuffix = "+ai";

    /// <summary>ค่าที่ควรเขียนเมื่อ **คน** ยืนยันการจับคู่.
    /// `aiAssisted` = คำแนะนำนั้นมาจาก AI จริง (ไม่ใช่เดาจากชื่อปุ่ม)</summary>
    public static string Person(Guid userId, bool aiAssisted = false)
        => userId == Guid.Empty
            ? (aiAssisted ? "user:unknown" + AiAssistedSuffix : "user:unknown")
            : userId.ToString("D") + (aiAssisted ? AiAssistedSuffix : "");

    /// <param name="Kind">ชนิดผู้กระทำ</param>
    /// <param name="Label">ป้ายพร้อมแสดง เช่น "⚙️ ระบบ (จับคู่อัตโนมัติ)"</param>
    /// <param name="IsSuggestionOnly">เป็นแค่ "เสนอ" ยังไม่ได้กระทบยอด</param>
    public sealed record Attribution(BankMatchActorKind Kind, string Label, bool IsSuggestionOnly);

    private static readonly Attribution UnknownActor =
        new(BankMatchActorKind.Unknown, "ไม่มีข้อมูล", false);

    /// <summary>
    /// แปลงค่าที่เก็บไว้เป็นป้ายบนจอ. <paramref name="personName"/> คือชื่อผู้ใช้
    /// ที่ผู้เรียกค้นมาให้ (null = ค้นไม่เจอ → แสดง "ผู้ใช้" เฉย ๆ ห้ามแต่งชื่อ)
    /// </summary>
    public static Attribution Describe(string? reconciledBy, string? personName = null)
    {
        if (string.IsNullOrWhiteSpace(reconciledBy)) return UnknownActor;
        var raw = reconciledBy.Trim();

        var aiAssisted = raw.EndsWith(AiAssistedSuffix, StringComparison.OrdinalIgnoreCase);
        var core = aiAssisted ? raw[..^AiAssistedSuffix.Length] : raw;

        // ── คน ──────────────────────────────────────────────────────────
        // แถวเก่าเก็บ GUID ดิบ (`CreateReconciliationGroupAsync`), แถวใหม่เก็บ
        // GUID เหมือนกัน + suffix เมื่อยืนยันคำแนะนำ AI
        if (Guid.TryParse(core, out var uid) && uid != Guid.Empty)
        {
            var who = string.IsNullOrWhiteSpace(personName) ? "ผู้ใช้" : personName!.Trim();
            return new Attribution(BankMatchActorKind.Person,
                aiAssisted ? $"👤 {who} (ยืนยันคำแนะนำ AI)" : $"👤 {who}", false);
        }
        if (core.StartsWith("user:", StringComparison.OrdinalIgnoreCase))
        {
            var who = string.IsNullOrWhiteSpace(personName) ? "ผู้ใช้" : personName!.Trim();
            return new Attribution(BankMatchActorKind.Person,
                aiAssisted ? $"👤 {who} (ยืนยันคำแนะนำ AI)" : $"👤 {who}", false);
        }

        // ── ระบบภายนอก ─────────────────────────────────────────────────
        if (string.Equals(core, ApiV1, StringComparison.OrdinalIgnoreCase))
            return new Attribution(BankMatchActorKind.Api, "🔌 ระบบภายนอก (API)", false);

        // ── AI จริง (เรียก provider/โมเดล) ──────────────────────────────
        if (core.Contains("/AI", StringComparison.OrdinalIgnoreCase)
            || core.StartsWith("AI-", StringComparison.OrdinalIgnoreCase)
            || core.StartsWith("ai:", StringComparison.OrdinalIgnoreCase))
        {
            var suggest = core.Contains("เสนอ", StringComparison.Ordinal);
            return new Attribution(BankMatchActorKind.Ai,
                suggest ? "🤖 AI เสนอ — รอยืนยัน" : "🤖 AI", suggest);
        }

        // ── เซิร์ฟเวอร์คำนวณเอง ─────────────────────────────────────────
        if (core.StartsWith("AutoMatch", StringComparison.OrdinalIgnoreCase)
            || core.StartsWith("BankFeed", StringComparison.OrdinalIgnoreCase)
            || core.StartsWith("OpenBanking", StringComparison.OrdinalIgnoreCase)
            || core.StartsWith("system:", StringComparison.OrdinalIgnoreCase))
        {
            var suggest = core.Contains("เสนอ", StringComparison.Ordinal);
            var where = core.StartsWith("BankFeed", StringComparison.OrdinalIgnoreCase) ? "ดึงจากธนาคาร"
                : core.StartsWith("OpenBanking", StringComparison.OrdinalIgnoreCase) ? "Open Banking"
                : "จับคู่อัตโนมัติ";
            return new Attribution(BankMatchActorKind.System,
                suggest ? $"⚙️ ระบบเสนอ ({where}) — รอยืนยัน" : $"⚙️ ระบบ ({where})", suggest);
        }

        return UnknownActor;
    }
}
