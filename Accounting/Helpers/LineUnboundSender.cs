namespace Accounting.Helpers;

/// <summary>
/// รอบ 258 (A-M2): ผู้ส่งข้อความถึงบอทกลาง (LINE OA ของ NextAcc) ที่<b>ยังไม่ได้ผูกบัญชี</b> — ตัวตั้งตัวเดียวของ "ตอบไหม · ตอบทางไหน" · pure
///
/// <para>═══ ที่มา ═══ บอทกลางตอบทุกข้อความด้วย push (กินโควตา push รายเดือนของ OA กลาง) ทั้งที่ event มี reply token (ฟรี) และคนแปลกหน้าได้คำตอบ
/// ทุกข้อความไม่จำกัด ⇒ ใครก็ได้เพิ่มเพื่อนแล้วส่งรัว ๆ จนโควตา push หมด = แจ้งเตือนจอง/คำสั่งซื้อ/บิลของ<b>ทุกบริษัท</b>หยุด</para>
///
/// <para>═══ กติกา ═══ (1) ตอบด้วย reply token ก่อนเสมอ (ไม่กินโควตา) (2) reply ล้ม (token หมดอายุเพราะงานช้า ฯลฯ) ⇒ ถอยไป push ได้<b>เฉพาะผู้ส่งที่ผูกบัญชีแล้ว</b>
/// — คนแปลกหน้าไม่มีวันทำให้เกิด push (3) คำตอบ "ยังไม่ได้เชื่อมต่อบัญชี" ถึงคนที่ยังไม่ผูก จำกัด <see cref="PerMinute"/>/นาที · <see cref="PerDay"/>/วัน ต่อผู้ส่ง
/// (ตัวนับในฐาน — <c>IChatRateLimiter</c> · หลายเครื่องนับร่วมกัน) · เกิน = เงียบ (ไม่ตอบ ไม่ประมวล)</para>
/// </summary>
public static class LineUnboundSender
{
    public const int PerMinute = 3;
    public const int PerDay = 20;

    /// <summary>กุญแจของตัวนับต่อผู้ส่ง (แยกจากตัวนับความพยายามผูก <c>linebind:</c>)</summary>
    public static string Key(string lineUserId) => "lineunbound:" + lineUserId;

    /// <summary>คำตอบถึงผู้ที่ยังไม่ผูก — เกินเพดาน = null (เงียบ)</summary>
    public static string? Reply(bool withinLimit, string reply) => withinLimit ? reply : null;

    /// <summary>reply ล้มแล้วถอยไป push ได้ไหม — เฉพาะผู้ส่งที่ผูกบัญชีแล้ว และ reply ไม่สำเร็จจริง</summary>
    public static bool PushFallbackAllowed(bool replySucceeded, bool senderBound) => !replySucceeded && senderBound;
}
