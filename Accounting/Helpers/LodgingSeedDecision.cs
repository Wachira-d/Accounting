namespace Accounting.Helpers;

/// <summary>ผลของการ seed ที่พักให้เว็บ — ไม่ใช่แค่ "สร้าง/ไม่สร้าง": ผู้เรียกต้องบอกเจ้าของว่า<b>ทำไม</b>ไม่สร้าง (ล้มดัง)</summary>
public enum LodgingSeedOutcome
{
    /// <summary>สร้างที่พักตัวอย่างให้เว็บนี้แล้ว</summary>
    Created = 1,
    /// <summary>เว็บนี้มีที่พักผูกอยู่แล้ว — ไม่ต้องทำอะไร (idempotent)</summary>
    AlreadyBound = 2,
    /// <summary>บริษัทมีที่พักที่ยังไม่ผูกเว็บ — ไม่สร้างแห่งที่สอง ให้เจ้าของผูกที่พักเดิมแทน (คำตัดสินข้อ 118)</summary>
    ExistingUnlinked = 3,
    /// <summary>ด่าน "ที่พักหลายแห่ง" กัน (ไม่มี add-on) — ด่านเดียวกับการสร้างมือ</summary>
    QuotaBlocked = 4,
}

/// <summary>ตัวตัดสินว่า "สร้างเว็บที่พัก/เติมเทมเพลตที่พัก" ควรสร้างที่พักตัวอย่างไหม (รอบ 202 ทีม LW · W-05 · คำตัดสินข้อ 118)
///
/// <para>ที่มา: <c>LodgingSeeder</c> ดูแค่ "เว็บนี้มีที่พักผูกไหม" ⇒ บริษัทที่ตั้งที่พักจริงไว้แล้ว (ยังไม่ผูกเว็บ) กด "เติมเทมเพลต"
/// ได้ที่พักแห่งที่สองชื่อเดียวกับเว็บ ห้อง/ราคา seed ยึดเว็บไป — แขกจองที่พักปลอม ขณะเจ้าของตั้งราคาที่ที่พักจริง · และข้ามด่าน add-on
/// "ที่พักหลายแห่ง" ที่การสร้างมือต้องผ่าน. ลำดับ: ผูกอยู่แล้ว → มีที่พักไม่ผูก (เสนอผูก ไม่สร้าง) → ด่านโควตา → สร้าง</para></summary>
public static class LodgingSeedDecision
{
    /// <param name="siteAlreadyBound">เว็บนี้มีที่พักผูกอยู่แล้ว</param>
    /// <param name="unlinkedPropertyNames">ชื่อที่พักของบริษัทที่ยังไม่ผูกเว็บใด (ไม่นับที่ลบแล้ว)</param>
    /// <param name="quotaBlockReason">ผลของ <see cref="LodgingPropertyQuota.BlockReasonAsync"/> (null = ผ่าน)</param>
    public static (LodgingSeedOutcome Outcome, string? Message) Decide(
        bool siteAlreadyBound, IReadOnlyList<string> unlinkedPropertyNames, string? quotaBlockReason)
    {
        if (siteAlreadyBound) return (LodgingSeedOutcome.AlreadyBound, null);
        if (unlinkedPropertyNames.Count > 0)
        {
            var names = string.Join(", ", unlinkedPropertyNames.Take(3).Select(n => $"\"{n}\""))
                + (unlinkedPropertyNames.Count > 3 ? $" และอีก {unlinkedPropertyNames.Count - 3} แห่ง" : "");
            return (LodgingSeedOutcome.ExistingUnlinked,
                $"บริษัทมีที่พัก {names} ที่ยังไม่ผูกเว็บอยู่แล้ว — ระบบไม่สร้างที่พักแห่งที่สองให้ · "
                + "ผูกที่พักเดิมกับเว็บนี้ได้ที่หน้า \"ตั้งค่าที่พัก\" (ช่อง \"เว็บไซต์ที่ผูก\") แล้วหน้าจองห้องของเว็บจะใช้ห้อง/ราคาของที่พักนั้นทันที");
        }
        if (quotaBlockReason != null)
            return (LodgingSeedOutcome.QuotaBlocked, "ยังไม่ได้สร้างที่พักให้เว็บนี้ — " + quotaBlockReason);
        return (LodgingSeedOutcome.Created, null);
    }
}
