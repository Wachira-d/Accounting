namespace Accounting.Services.Payments.Providers;

/// <summary>
/// รอบ 219 ส่วน B ฝ่ายค้าน (B3) — รูปแบบคีย์ของ Omise ต่อโหมด · pure · ตัวเดียวของ adapter (<c>OmisePaymentProvider.KeyModeProblem</c>)
/// <para><b>ที่มา:</b> ช่อง "คีย์ทดสอบ" รับคีย์จริงได้ (และกลับกัน) ⇒ "ทดสอบเชื่อมต่อ" ผ่านเพราะคีย์ใช้ได้ ⇒ หน้าทดลองจ่าย/ปุ่มทดลองจ่ายเก็บเงินจริงได้ ·
/// คีย์ทดสอบของ Omise ขึ้นต้น <c>pkey_test_</c>/<c>skey_test_</c> · คีย์จริงขึ้นต้น <c>pkey_</c>/<c>skey_</c> (บางบัญชี <c>_live_</c>) และ<b>ไม่มี</b> <c>_test_</c>
/// (ข้อเท็จจริงเชิง API มาจากความรู้ทั่วไป — ยืนยันกับแดชบอร์ด Omise ตอนตั้งค่าจริง; ด่าน livemode ตอนทดสอบเป็นตาข่ายชั้นที่สอง)</para>
/// </summary>
public static class OmiseKeyFormat
{
    /// <summary>null = รูปแบบใช้ได้ · ว่าง = ไม่ตรวจ (ไม่เปลี่ยน) · <paramref name="secret"/> = คีย์ลับ (skey) ไม่ใช่คีย์สาธารณะ (pkey)</summary>
    public static string? Problem(bool live, bool secret, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim();
        var prefix = secret ? "skey_" : "pkey_";
        var name = (secret ? "คีย์ลับ" : "คีย์สาธารณะ") + (live ? " (ใช้งานจริง)" : " (ทดสอบ)");
        if (!v.StartsWith(prefix, StringComparison.Ordinal))
            return $"{name} ต้องขึ้นต้นด้วย {prefix} — ตรวจว่าคัดลอกคีย์ถูกช่อง";
        var isTestKey = v.StartsWith(prefix + "test_", StringComparison.Ordinal);
        if (live && isTestKey)
            return $"{name}: ใส่คีย์ทดสอบ ({prefix}test_…) ในช่องโหมดใช้งานจริงไม่ได้";
        if (!live && !isTestKey)
            return $"{name}: ช่องโหมดทดสอบรับเฉพาะคีย์ทดสอบ ({prefix}test_…) — คีย์จริงในช่องนี้จะทำให้ปุ่ม/หน้าทดลองจ่ายเก็บเงินจริง";
        return null;
    }
}
