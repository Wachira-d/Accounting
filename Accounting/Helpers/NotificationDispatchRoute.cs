namespace Accounting.Helpers;

/// <summary>รอบ 250 (WP-02 §2.4 ข้อ 4 · คำตัดสิน §2 #11 · B F-8): <b>ลำดับตัดสินเส้นทางของ <c>NotificationEngine.DispatchAsync</c> — ตัวเดียว</b> (pure)
///
/// <para>ที่มา: รอบ 249 แก้ "บริษัทปิดทุกช่องของคีย์แล้วได้ค่าตั้งต้นกลับมาเงียบ ๆ" เฉพาะเส้นปกติ — แต่เหตุการณ์ที่ผู้เรียกระบุ <c>RecipientUserId</c>
/// (อนุมัติ/รายการประจำ/ค่าบริการ) ยังเข้าสาขา "ส่งตรงด้วยค่าตั้งต้น" ก่อนดูว่าบริษัทบันทึกแถวปิดไว้ ⇒ ปิดไม่ได้ (คลาสเดียวกับบั๊ก (3) รอบ 249) ·
/// ลำดับที่ถูก: บริษัทบันทึกแถวของคีย์แล้วแต่ปิดทุกช่อง ⇒ ไม่ส่ง <b>ก่อน</b>สาขาผู้รับที่ระบุ</para></summary>
public static class NotificationDispatchRoute
{
    public enum Route
    {
        /// <summary>ไม่ส่ง — บริษัทบันทึกแถวของคีย์นี้แล้วและปิดทุกช่อง (ทางเลือกของผู้ใช้ชนะเสมอ)</summary>
        SuppressedByCompany,
        /// <summary>ยังไม่ตั้งคีย์นี้เลย + ผู้เรียกระบุผู้รับ ⇒ ส่งตรงถึงผู้รับด้วยช่องตั้งต้น (กระดิ่ง + อีเมล)</summary>
        DirectToRecipient,
        /// <summary>ยังไม่ตั้งคีย์นี้เลย ⇒ ค่าตั้งต้นของระบบ (NotificationAudiencePolicy.DefaultMatrix)</summary>
        SystemDefaults,
        /// <summary>มีแถวที่เปิดอย่างน้อยหนึ่งช่อง ⇒ ตามค่าตั้งของบริษัท</summary>
        CompanySettings,
    }

    /// <param name="savedRows">จำนวนแถวที่บริษัทบันทึกไว้ของคีย์นี้ (รวมแถวที่ปิดทุกช่อง)</param>
    /// <param name="enabledRows">จำนวนแถวที่เปิดอย่างน้อยหนึ่งช่อง</param>
    /// <param name="hasRecipientUser">ผู้เรียกระบุ <c>RecipientUserId</c></param>
    public static Route Decide(int savedRows, int enabledRows, bool hasRecipientUser)
    {
        if (savedRows > 0 && enabledRows == 0) return Route.SuppressedByCompany;
        if (enabledRows > 0) return Route.CompanySettings;
        return hasRecipientUser ? Route.DirectToRecipient : Route.SystemDefaults;
    }
}
