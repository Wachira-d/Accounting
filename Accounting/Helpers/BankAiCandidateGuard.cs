using System;
using System.Collections.Generic;
using System.Linq;

namespace Accounting.Helpers;

/// <summary>
/// **ด่านกันคู่ที่ AI แต่งขึ้นในแผนจับคู่ธนาคารทั้งก้อน** (OWNER file · pure) — รอบ 201 ทีม AI · A-AI7 (H-9)
///
/// ═══ ของเดิมพังตรงไหน ═══
/// <c>BulkBankAiMatchService</c> เขียนทับยอดที่ AI อ้างด้วยยอดจริงจาก DB (ต้นแบบที่ดีที่สุดตาม
/// <c>DECISION_DOCTRINE</c> §2.4) — แต่เมื่อ <c>candidateId</c> **ไม่อยู่ในชุดจริงเลย** การค้นยอดพลาด
/// แล้ว <c>realAmountById.TryGetValue(key, out var ra2) ? ra2 : c.Amount</c> ตกไปใช้ **ยอดที่ AI แต่ง**
/// ⇒ ผลรวมตรงยอดธนาคารเป๊ะ ⇒ ความมั่นใจ 100% "พร้อมยืนยัน" กับเอกสารที่ไม่มีตัวตน · และ
/// <c>bankTxnId</c> ที่ไม่รู้จักก็ผ่านไปถึงหน้าจอตรง ๆ ⇒ กดยืนยันแล้วเด้ง error ปลายทาง
/// (confirm-then-error loop ที่ <c>CalibrateConfidence</c> ตั้งใจจะกัน)
///
/// ═══ กติกา ═══
/// 1. บรรทัดธนาคารที่ไม่อยู่ในชุดที่ส่งให้พิจารณา ⇒ **ทิ้งทั้งข้อเสนอ**
/// 2. ผู้สมัครที่ไม่อยู่ในชุดจริง (id ว่าง/ไม่รู้จัก) ⇒ **ตัดออก** — ยอดของ AI ห้ามรอดไปถึงจอ
/// 3. ตัดจนไม่เหลือผู้สมัคร ⇒ **ทิ้งทั้งข้อเสนอ**
/// 4. เหลือบางส่วน ⇒ เก็บไว้ให้คนตรวจ แต่ <b>เพดานความมั่นใจ</b> <see cref="FabricatedConfidenceCap"/>
///    (AI ที่แต่งคู่หนึ่งขึ้นมาแล้ว คู่อื่นในข้อเสนอเดียวกันก็เชื่อเต็มไม่ได้ — ห้ามติ๊กให้อัตโนมัติ)
///
/// ทิศปลอดภัย (DOCTRINE §1 G5): ข้อเสนอที่ถูกตัดถูก **นับและบอก** ในคำเตือนของแผน — ไม่หายเงียบ
/// และบรรทัดธนาคารนั้นยังจับคู่ด้วยมือได้ตามปกติ
/// </summary>
public static class BankAiCandidateGuard
{
    /// <summary>เพดานความมั่นใจของข้อเสนอที่มีผู้สมัครแต่งขึ้นอย่างน้อย 1 ราย —
    /// เท่ากับเพดานของ "AI ระบุยอดผิด" เดิม (0.55) ให้ทั้งสองชนิดของการแต่งได้ผลเท่ากัน</summary>
    public const decimal FabricatedConfidenceCap = 0.55m;

    public enum Outcome
    {
        /// <summary>ทุกผู้สมัครอยู่ในชุดจริง — ไม่แตะอะไร</summary>
        Keep = 0,
        /// <summary>ตัดผู้สมัครที่แต่งขึ้นออกบางส่วน — เก็บข้อเสนอไว้แต่ต้องเพดานความมั่นใจ</summary>
        KeepTrimmed = 1,
        /// <summary>บรรทัดธนาคารไม่อยู่ในชุดที่พิจารณา — ทิ้งทั้งข้อเสนอ</summary>
        DropUnknownBankTxn = 2,
        /// <summary>ไม่เหลือผู้สมัครที่มีอยู่จริงเลย — ทิ้งทั้งข้อเสนอ</summary>
        DropNoRealCandidate = 3,
    }

    /// <param name="Outcome">ทำอะไรกับข้อเสนอนี้</param>
    /// <param name="KeptCandidateIds">ผู้สมัครที่อยู่ในชุดจริง (คงลำดับเดิม)</param>
    /// <param name="FabricatedCandidateIds">ผู้สมัครที่ถูกตัดเพราะไม่อยู่ในชุดจริง</param>
    public sealed record Result(
        Outcome Outcome,
        IReadOnlyList<Guid> KeptCandidateIds,
        IReadOnlyList<Guid> FabricatedCandidateIds)
    {
        public bool Dropped => Outcome is Outcome.DropUnknownBankTxn or Outcome.DropNoRealCandidate;
    }

    /// <summary>ตัดสินข้อเสนอ 1 รายการ — ใช้ได้กับทั้งข้อเสนอของ AI และของเซิร์ฟเวอร์
    /// (ข้อเสนอของเซิร์ฟเวอร์สร้างจากชุดจริง ⇒ ผ่านเป็น <see cref="Outcome.Keep"/> เสมอ)</summary>
    public static Result Screen(
        Guid bankTxnId,
        IEnumerable<Guid>? candidateIds,
        Func<Guid, bool> isKnownBankTxn,
        Func<Guid, bool> isKnownCandidate)
    {
        var ids = candidateIds?.ToList() ?? new List<Guid>();
        if (bankTxnId == Guid.Empty || !isKnownBankTxn(bankTxnId))
            return new Result(Outcome.DropUnknownBankTxn, Array.Empty<Guid>(), ids);

        var kept = new List<Guid>(ids.Count);
        var fabricated = new List<Guid>();
        foreach (var id in ids)
        {
            if (id != Guid.Empty && isKnownCandidate(id)) kept.Add(id);
            else fabricated.Add(id);
        }
        if (kept.Count == 0)
            return new Result(Outcome.DropNoRealCandidate, kept, fabricated);
        return new Result(fabricated.Count == 0 ? Outcome.Keep : Outcome.KeepTrimmed, kept, fabricated);
    }

    /// <summary>เพดานความมั่นใจของข้อเสนอที่มีผู้สมัครแต่งขึ้น — ไม่ดันค่าขึ้นเด็ดขาด</summary>
    public static decimal CapConfidence(decimal confidence, int fabricatedCount)
        => fabricatedCount > 0 ? Math.Min(confidence, FabricatedConfidenceCap) : confidence;

    /// <summary>id ที่คำขอยืนยันส่งมาแต่ไม่พบในฐานข้อมูลของบริษัทนี้ (ด่านฝั่งเขียน — เส้น batch
    /// ยืนยันคู่) · ลำดับคงตามคำขอ · ไม่นับ <see cref="Guid.Empty"/></summary>
    public static IReadOnlyList<Guid> MissingIds(IEnumerable<Guid>? requested, IReadOnlyCollection<Guid> found)
    {
        if (requested == null) return Array.Empty<Guid>();
        var set = found as ISet<Guid> ?? new HashSet<Guid>(found);
        return requested.Where(id => id != Guid.Empty && !set.Contains(id)).Distinct().ToList();
    }

    /// <summary>ข้อความเตือนรวมของแผน — นับให้ผู้ใช้เห็นว่ามีข้อเสนอถูกตัดกี่รายการ (ห้ามหายเงียบ)</summary>
    public static string? PlanWarning(int droppedUnknownBankTxn, int droppedNoRealCandidate, int trimmedCandidates)
    {
        if (droppedUnknownBankTxn + droppedNoRealCandidate + trimmedCandidates == 0) return null;
        var parts = new List<string>();
        if (droppedUnknownBankTxn > 0) parts.Add($"อ้างบรรทัดธนาคารที่ไม่อยู่ในชุด {droppedUnknownBankTxn} ข้อเสนอ");
        if (droppedNoRealCandidate > 0) parts.Add($"อ้างแต่เอกสาร/รายการที่ไม่มีอยู่จริง {droppedNoRealCandidate} ข้อเสนอ");
        if (trimmedCandidates > 0) parts.Add($"ตัดเอกสาร/รายการที่ไม่มีอยู่จริงออก {trimmedCandidates} รายการ");
        return "⚠ ตัดข้อเสนอของ AI ที่อ้างรายการที่ไม่มีอยู่จริง: " + string.Join(" · ", parts)
            + " — บรรทัดเหล่านั้นจับคู่ด้วยมือได้ตามปกติ";
    }
}
