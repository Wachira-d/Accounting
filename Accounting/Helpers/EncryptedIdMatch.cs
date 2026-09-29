namespace Accounting.Helpers;

/// <summary>จับคู่เลขบัตรประชาชน/เลขผู้เสียภาษีกับคอลัมน์ที่เข้ารหัส (รอบ 200 ทีม R · G2-06)
///
/// ═══ ที่มา ═══
/// <c>Employee.CitizenId</c>/<c>TaxId</c> ผ่าน <c>EncryptedColumnConverter</c> ที่ใช้ nonce สุ่มทุกครั้ง ⇒ ciphertext ของค่าเดียวกันไม่ซ้ำกัน
/// ⇒ <c>Where(e =&gt; e.CitizenId == idCard)</c> ใน SQL <b>ไม่มีวันเจอ</b> ⇒ นำเข้าทะเบียนพนักงานซ้ำแล้วตัวจับซ้ำเงียบ ⇒ พนักงานคนเดิมสองแถว
/// ได้เงินเดือนสองครั้ง ⇒ ต้องถอดรหัส (โหลดแถว) แล้วเทียบในหน่วยความจำด้วยรูปที่ normalize แล้ว (ตัวเลขล้วน)</summary>
public static class EncryptedIdMatch
{
    /// <summary>ตัวเลขล้วน (ตัดขีด/ช่องว่าง) · ว่าง = null</summary>
    public static string? Normalize(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var digits = new string(id.Where(char.IsDigit).ToArray());
        return digits.Length == 0 ? null : digits;
    }

    /// <summary>true = เลขเดียวกันหลัง normalize (ว่างไม่เท่ากับอะไรเลย)</summary>
    public static bool Same(string? a, string? b)
    {
        var x = Normalize(a);
        return x != null && x == Normalize(b);
    }
}
