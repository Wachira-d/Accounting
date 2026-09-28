using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Accounting.Helpers;

/// <summary>ข้อมูลของแถวที่ใช้ทำคีย์ — ป้ายดิบถูก<b>แฮช</b>ก่อนเก็บ (ไม่มี PII ในคีย์) · <paramref name="PayoutRef"/> = รอบโอนของแถว
/// (คอลัมน์ในไฟล์ หรือเลขรอบโอนที่ผู้ใช้ระบุ)</summary>
public readonly record struct SettlementTxnKeyInput(string? RawTxnId, string? Label, string? OrderId, decimal Amount, DateTime? Date,
    string? PayoutRef = null);

/// <summary>
/// **คีย์กันนำเข้าซ้ำของบรรทัด settlement (<c>SettlementLine.ExternalTxnId</c> · unique ต่อช่องทาง) — pure · deterministic · รุ่น v2**
/// (รอบ 198 เฟส 1 ทีม B · แก้ฝ่ายค้าน review198-B R-B5/R-B6 ทีม S3)
///
/// <para>ที่มา: นำเข้าไฟล์เดิมซ้ำ (กดสองครั้ง · ไฟล์รอบโอนที่ช่วงวันทับกัน) ต้องไม่เกิดบรรทัดซ้ำ ⇒ ยอดรายได้/ค่าธรรมเนียมซ้ำ —
/// และ<b>ทิศตรงข้าม</b> (R-B5): สองแถวที่เป็นรายการจริงคนละรายการต้องไม่ได้คีย์เดียวกัน ⇒ แถวหลังถูกทิ้งเงียบ ๆ ด้วยสถานะ "ซ้ำ"
/// (รุ่นแรกใช้ id + ป้ายที่ตัดตัวเลขทิ้ง ⇒ คืนเงินบางส่วนครั้งที่สองของ id เดียวกัน · "ค่าธรรมเนียม 3%" กับ "5%" ชนกัน · แถวไม่มี id ของสองรอบโอน
/// ในวันเดียวกัน "Withdrawal fee −10" ชนกัน)</para>
/// <para>═══ กติกา v2 ═══ (ผลขึ้นกับ<b>เนื้อหาของแถวเท่านั้น</b> — ไม่ขึ้นกับแถวอื่นที่ไม่เหมือนกันทุกช่อง · R-A9)
/// <list type="number">
/// <item>id ของ adapter ภายใน (<c>pi:</c> — <see cref="ForPaymentIntent"/>) ⇒ ใช้ตามนั้น (ไม่ซ้ำโดยการออกแบบ)</item>
/// <item>มี id ดิบ ⇒ <c>v2:</c> + id + ":" + แฮช(ป้าย | ยอด | วันที่) — ยอดขาย/ค่าธรรมเนียม/คืนเงินของ id เดียวกันได้คีย์คนละตัว ·
/// คืนเงินครั้งที่สองยอดต่างกันได้คีย์ใหม่ · <b>ไม่ใส่รอบโอน</b> (id ของแพลตฟอร์มเป็นตัวกันซ้ำข้ามรอบ)</item>
/// <item>ไม่มี id ⇒ <c>v2:row:</c> + แฮช(ออเดอร์ | ป้าย | ยอด | วันที่ | <b>รอบโอน</b>) — แถวเดียวกันอยู่รอบโอนเดียวเสมอ ⇒ ไฟล์เดิมนำเข้าซ้ำยังชนกัน ·
/// สองรอบโอนที่มีแถวหน้าตาเหมือนกันไม่ชนกัน</item>
/// <item>ยังชนกันในไฟล์เดียว (แถวเหมือนกัน<b>ทุกช่อง</b> = รายการจริงหลายรายการ) ⇒ ต่อท้าย "#2", "#3" ตามลำดับในไฟล์ — ลำดับที่นับเฉพาะแถวที่เหมือนกันทุกช่อง</item>
/// <item>ยาวเกิน 200 ตัวอักษร (คอลัมน์) ⇒ "h:" + SHA-256 ของค่าเต็ม</item>
/// </list>
/// ป้ายผ่านตัว normalize ของที่นี่เอง (<see cref="FrozenLabel"/> — ตัดช่องว่างซ้ำ · ตัวพิมพ์เล็ก · <b>คงตัวเลข</b>) ไม่ใช้ตัวตัด PII/ตัวจัดประเภท
/// ร่วมกัน (R-B6: ปรับ regex ของสองตัวนั้นแล้วคีย์ของแถวที่นำเข้าไปแล้วต้องไม่เปลี่ยน) · เปลี่ยนกติกา = เปลี่ยนรุ่นคำนำหน้า</para>
/// </summary>
public static class SettlementTxnKey
{
    public const int MaxLength = 200;

    /// <summary>คำนำหน้ารุ่นของกติกาคีย์ — เปลี่ยนกติกาแล้วต้องเปลี่ยนรุ่น (คีย์รุ่นเก่ากับใหม่ไม่ชนกันโดยบังเอิญ)</summary>
    public const string Version = "v2:";

    /// <summary>คีย์ของทุกแถวตามลำดับเดิม</summary>
    public static IReadOnlyList<string> Assign(IReadOnlyList<SettlementTxnKeyInput> rows)
    {
        var used = new Dictionary<string, int>(StringComparer.Ordinal);
        var keys = new List<string>(rows.Count);
        foreach (var r in rows)
        {
            var amount = r.Amount.ToString("0.00", CultureInfo.InvariantCulture);
            var date = r.Date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";
            var label = FrozenLabel(r.Label);
            string key;
            if (!string.IsNullOrWhiteSpace(r.RawTxnId))
            {
                var id = r.RawTxnId.Trim();
                key = id.StartsWith("pi:", StringComparison.Ordinal)
                    ? id
                    : Version + id + ":" + Hash(string.Join("|", label, amount, date))[..24];
            }
            else
            {
                var basis = string.Join("|", (r.OrderId ?? "").Trim(), label, amount, date, (r.PayoutRef ?? "").Trim());
                key = Version + "row:" + Hash(basis)[..40];
            }
            var n = used.TryGetValue(key, out var seen) ? seen + 1 : 1;
            used[key] = n;
            if (n > 1) key += "#" + n.ToString(CultureInfo.InvariantCulture);
            keys.Add(Fit(key));
        }
        return keys;
    }

    /// <summary>คีย์ของบรรทัดที่ประกอบจาก PaymentIntent — ส่วน = "sale" · "fee" · "refund@{ยอดคืนสะสม}" (ยอดคืนเพิ่มภายหลัง ⇒ คีย์ใหม่)</summary>
    public static string ForPaymentIntent(Guid intentId, string part)
        => Fit("pi:" + intentId.ToString("N") + ":" + part);

    /// <summary>ป้ายสำหรับคีย์ — ตัวตั้งแยกของคีย์ (ห้ามใช้ตัว normalize ของตัวจัดประเภท/ตัวตัด PII · R-B6) · คงตัวเลข · ช่องว่างซ้ำเป็นช่องเดียว · ตัวพิมพ์เล็ก</summary>
    internal static string FrozenLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return "";
        var sb = new StringBuilder(label.Length);
        var space = false;
        foreach (var ch in label.Trim())
        {
            if (char.IsWhiteSpace(ch)) { space = true; continue; }
            if (space && sb.Length > 0) sb.Append(' ');
            space = false;
            sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }

    private static string Fit(string key) => key.Length <= MaxLength ? key : "h:" + Hash(key);

    private static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
}
