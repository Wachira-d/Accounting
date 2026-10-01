namespace Accounting.Helpers;

/// <summary>วิธีปรับ JE ของเอกสาร integration ที่ถูก resync (ระบบต้นทางส่งยอดใหม่มาแก้เอกสารเดิม)</summary>
public enum IntegrationResyncJournalAction
{
    /// <summary>แก้ JE เดิมในที่ (งวดเปิด + JE เดิมใบเดียว) — สำเร็จแล้ว</summary>
    InPlace = 1,
    /// <summary>กลับ JE เดิมแล้วลงใบใหม่ — สร้างบรรทัดใหม่ได้แล้ว (ตรวจก่อนกลับ)</summary>
    ReverseAndRepost = 2,
    /// <summary>สร้างบรรทัดใหม่ไม่ได้ ⇒ <b>คง JE เดิมไว้</b> (ไม่กลับ) + ดังสามที่ + ทางไปต่อ</summary>
    KeepOriginal = 3,
    /// <summary>เอกสารไม่มี JE เดิม ⇒ ลงใบใหม่ตามเส้นปกติ (สร้างไม่ได้ = หมายเหตุ "[ยังไม่ลงบัญชี]" ตามเดิม)</summary>
    PostFresh = 4,
}

/// <summary>
/// **ลำดับการปรับ JE ตอน resync เอกสาร integration — สร้างใหม่ให้ได้ก่อน แล้วค่อยกลับของเดิม** (รอบ 201 ทีม GW · A-GW12 · team-G E-4b)
///
/// <para>═══ ที่มา (บั๊กจริง) ═══ resync ที่แก้ JE ในที่ไม่ได้ (งวดเดิมปิด · มี JE หลายใบ · หรือบรรทัดใหม่สร้างไม่ได้) ตกไปทาง "กลับ JE เดิม + ลงใหม่" —
/// เดิม<b>กลับ JE เดิมก่อน</b>แล้วค่อยลองสร้างใหม่ ⇒ ถ้าสร้างใหม่ไม่ได้ (เหตุเดียวกับที่ in-place ล้ม: mapping ชี้ผังที่ปิดใช้ · ด่านโครงสร้างไม่ผ่าน)
/// เอกสารเหลือ<b>ไม่มี JE เลย</b> — ดัง แต่ทำลาย JE ที่ถูกอยู่แล้วทิ้ง · และข้อความบอกให้ "สั่งลงบัญชีใหม่จากหน้าเอกสาร" ซึ่ง<b>ไม่มีปุ่มนั้น</b></para>
///
/// <para>═══ กติกา ═══ ลองสร้างบรรทัดใหม่แบบไม่บันทึก (dry-run · ตัวสร้าง + ด่านโครงสร้างตัวเดียวกับตอนลงจริง) <b>ก่อน</b>แตะ JE เดิม · ได้ ⇒ กลับแล้วลงใหม่ ·
/// ไม่ได้ ⇒ คง JE เดิม (ยอดเก่า) + หมายเหตุบนเอกสาร + sync log <c>PartialSuccess</c> + ข้อความตอบคู่ค้า · ทางไปต่อ = แก้การจับคู่ผังของการเชื่อมต่อแล้วให้
/// ระบบต้นทางส่งเอกสารนี้ซ้ำ (resync จะลงใหม่ให้ — ทางเดียวที่มีอยู่จริง)</para>
///
/// <para>G6: pure</para>
/// </summary>
public static class IntegrationResyncJournal
{
    /// <summary>ตัดสินวิธีปรับ JE — <paramref name="inPlaceDone"/> = แก้ในที่สำเร็จแล้ว · <paramref name="originalCount"/> = JE forward เดิมที่ยังไม่ถูกกลับ ·
    /// <paramref name="rebuildable"/> = dry-run สร้างบรรทัดใหม่ผ่าน (ตัวสร้าง + ด่านโครงสร้าง)</summary>
    public static IntegrationResyncJournalAction Decide(bool inPlaceDone, int originalCount, bool rebuildable)
    {
        if (inPlaceDone) return IntegrationResyncJournalAction.InPlace;
        if (originalCount == 0) return IntegrationResyncJournalAction.PostFresh;
        return rebuildable ? IntegrationResyncJournalAction.ReverseAndRepost : IntegrationResyncJournalAction.KeepOriginal;
    }

    /// <summary>หมายเหตุเมื่อคง JE เดิม — ขึ้นต้นด้วยป้ายให้ค้นเจอ · บอกเหตุ + ผลที่ตามมา (ยอดในบัญชียังเป็นยอดเดิม) + ทางไปต่อที่มีจริง</summary>
    public static string KeepOriginalNote(string? reason)
        => $"{KeepOriginalTag} สร้างรายการบัญชีของยอดใหม่ไม่ได้ — {(string.IsNullOrWhiteSpace(reason) ? "ไม่ทราบสาเหตุ" : reason.Trim())} · "
           + "ระบบคงรายการบัญชีเดิม (ยอดก่อน resync) ไว้ ไม่กลับรายการ ⇒ ยอดในบัญชียังไม่ตรงเอกสารที่แก้แล้ว · "
           + ResendNextStep;

    /// <summary>ป้ายหมายเหตุบนเอกสาร (ค้นเอกสารที่ยอดในบัญชียังเป็นยอดเดิม)</summary>
    public const string KeepOriginalTag = "[JE ยังเป็นยอดเดิม]";

    /// <summary>ทางไปต่อเมื่อเอกสาร integration ลงบัญชีไม่ได้ — ตัวเดียวของหมายเหตุ "[ยังไม่ลงบัญชี]" และ "[JE ยังเป็นยอดเดิม]"
    /// (เดิมบอกให้ "สั่งลงบัญชีใหม่จากหน้าเอกสาร" ซึ่งไม่มีปุ่มนั้นในระบบ)</summary>
    public const string ResendNextStep =
        "ทางแก้: ตรวจการจับคู่ผังบัญชี (mapping) ของการเชื่อมต่อนี้ที่หน้า \"เชื่อมต่อระบบ\" ให้ชี้ผังที่ใช้งานได้ แล้วให้ระบบต้นทางส่งเอกสารนี้ซ้ำแบบแก้ไข "
        + "(resyncUpdate = true) — ระบบจะลงรายการบัญชีให้ใหม่";
}
