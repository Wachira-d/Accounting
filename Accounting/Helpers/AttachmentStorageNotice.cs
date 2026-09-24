using System.Globalization;

namespace Accounting.Helpers;

/// <summary>ระดับพื้นที่ไฟล์หลังอัปโหลด</summary>
public enum AttachmentStorageLevel
{
    /// <summary>ไม่รู้เพดาน/การใช้งาน (ไม่มี subscription · เพดาน ≤ 0) — ไม่ประกาศอะไร ห้ามแสดงเป็น 0</summary>
    Unknown = 0,
    Ok,
    /// <summary>ใช้ไปแล้ว ≥ <see cref="AttachmentStorageNotice.NearRatio"/> ของเพดาน</summary>
    Near,
    /// <summary>เกินเพดาน — ไฟล์ยังถูกบันทึก แต่ต้องบอกผู้ใช้</summary>
    Over,
}

/// <summary>
/// **เพดานพื้นที่ของไฟล์แนบ = เตือน ไม่บล็อก** (คำตัดสินเจ้าของ รอบ 193 ข้อ 30)
///
/// <para>ไฟล์แนบคือหลักฐานประกอบรายการบัญชี (พ.ร.บ.การบัญชี ม.10 · ป.รัษฎากร §87/3) — ถ้าบล็อกเพราะโควตา IT ผู้ใช้
/// ที่เกินแพ็กเกจจะ<b>แนบหลักฐานของรายการที่ลงบัญชีไปแล้วไม่ได้</b> ⇒ ความเสียหายทางกฎหมาย &gt; ความเสียหายทางพื้นที่
/// ⇒ บันทึกเสมอ แล้วตอบคำเตือนกลับไปให้หน้าเว็บแสดง (ทิศที่ "มองเห็นและแก้ทัน" — DOCTRINE §1)</para>
///
/// <para>ต่างจากสื่อ CMS (<c>CmsContentService</c> ยังบล็อกผ่าน <c>CanFitStorageAsync</c>) เพราะรูปบนเว็บไม่ใช่หลักฐาน</para>
///
/// <para>G6: pure · ไม่มี I/O · ตัวเลขการใช้งานต้องมาจาก<b>ของจริง</b> (Σ ขนาดไฟล์ในตาราง — <c>SubscriptionService.
/// GetStorageStatusAsync</c>) ไม่ใช่ counter <c>Subscription.CurrentStorageUsed</c> ที่ไม่มีใครเขียน</para>
/// </summary>
public static class AttachmentStorageNotice
{
    /// <summary>สัดส่วนที่เริ่มเตือนล่วงหน้า</summary>
    public const decimal NearRatio = 0.9m;

    /// <param name="usedAfterBytes">พื้นที่ที่ใช้ทั้ง pool <b>รวมไฟล์นี้แล้ว</b> · <c>null</c> = ไม่รู้</param>
    /// <param name="maxBytes">เพดานของแพ็กเกจ · <c>null</c>/≤ 0 = ไม่รู้</param>
    public static AttachmentStorageLevel Evaluate(long? usedAfterBytes, long? maxBytes)
    {
        if (usedAfterBytes is not { } used || maxBytes is not { } max || max <= 0 || used < 0)
            return AttachmentStorageLevel.Unknown;
        if (used > max) return AttachmentStorageLevel.Over;
        if (used >= max * NearRatio) return AttachmentStorageLevel.Near;
        return AttachmentStorageLevel.Ok;
    }

    /// <summary>ข้อความที่หน้าเว็บแสดง · <c>null</c> = ไม่ต้องเตือน (Ok/Unknown)</summary>
    public static string? Message(long? usedAfterBytes, long? maxBytes)
    {
        var level = Evaluate(usedAfterBytes, maxBytes);
        if (level is AttachmentStorageLevel.Ok or AttachmentStorageLevel.Unknown) return null;
        var used = FormatMb(usedAfterBytes!.Value);
        var max = FormatMb(maxBytes!.Value);
        return level == AttachmentStorageLevel.Over
            ? $"บันทึกไฟล์แล้ว แต่พื้นที่เกินแพ็กเกจ (ใช้ {used} จาก {max}) — ไฟล์หลักฐานยังแนบได้ตามปกติ "
              + "(ระบบไม่บล็อกหลักฐานบัญชี) · ติดต่อเจ้าของบริษัทเพื่อเพิ่มพื้นที่ที่หน้าแพ็กเกจ"
            : $"พื้นที่ไฟล์ใกล้เต็มแพ็กเกจ (ใช้ {used} จาก {max}) — ติดต่อเจ้าของบริษัทเพื่อเพิ่มพื้นที่ที่หน้าแพ็กเกจ";
    }

    private static string FormatMb(long bytes)
        => (bytes / 1024m / 1024m).ToString("#,##0.#", CultureInfo.InvariantCulture) + " MB";
}
