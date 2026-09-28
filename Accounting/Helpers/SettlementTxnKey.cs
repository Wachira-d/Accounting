using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Accounting.Helpers;

/// <summary>ข้อมูลของแถวที่ใช้ทำคีย์ (ไม่มี PII — ป้าย · เลขออเดอร์ · ยอด · วันที่)</summary>
public readonly record struct SettlementTxnKeyInput(string? RawTxnId, string? Label, string? OrderId, decimal Amount, DateTime? Date);

/// <summary>
/// **คีย์กันนำเข้าซ้ำของบรรทัด settlement (<c>SettlementLine.ExternalTxnId</c> · unique ต่อช่องทาง) — pure · deterministic**
/// (รอบ 198 เฟส 1 ทีม B)
///
/// <para>ที่มา: นำเข้าไฟล์เดิมซ้ำ (กดสองครั้ง · ไฟล์รอบโอนที่ช่วงวันทับกัน) ต้องไม่เกิดบรรทัดซ้ำ ⇒ ยอดรายได้/ค่าธรรมเนียมซ้ำ —
/// แต่ไฟล์จริงไม่ได้มี id ที่ไม่ซ้ำทุกแถวเสมอ: แบบยาวหลายเจ้าใส่เลขรายการเดียวกันให้แถวยอดขาย+ค่าธรรมเนียมของออเดอร์เดียวกัน ·
/// แบบกว้าง 1 แถว = หลายบรรทัด · บางไฟล์ไม่มี id เลย</para>
/// <para>═══ กติกา ═══ (ผลขึ้นกับ<b>เนื้อหาของแถวเท่านั้น</b> ไม่ขึ้นกับแถวอื่นในไฟล์ — ไฟล์รอบโอนที่ช่วงวันทับกันต้องได้คีย์เดียวกันของแถวเดียวกัน ·
/// ผลตรวจฝ่ายค้าน R-A9: คีย์ที่ขึ้นกับ "ซ้ำในไฟล์ไหม" ทำให้แถวเดียวกันได้คีย์ต่างกันระหว่างสองไฟล์ ⇒ นำเข้าซ้ำหลุด)
/// <list type="number">
/// <item>มี id ดิบ ⇒ id + "|" + ป้าย (normalize แล้ว) <b>เสมอ</b> — ยอดขาย · ค่าธรรมเนียม · คืนเงิน ที่ใช้ id รายการเดียวกันของแพลตฟอร์ม
/// (Omise ใส่ค่าธรรมเนียมในธุรกรรมเดียวกัน · แบบกว้าง 1 ออเดอร์หลายคอลัมน์) ได้คีย์คนละตัว ไม่ชน unique index (เดิม = 23505 → HTTP 500)</item>
/// <item>ไม่มี id ⇒ "row:" + SHA-256(เลขออเดอร์ | ป้าย | ยอด | วันที่) — ไม่ใส่เลขรอบโอน เพื่อให้ไฟล์สองรอบที่ช่วงวันทับกันชนกัน (กันซ้ำข้ามรอบ)</item>
/// <item>ยังชนกันในไฟล์เดียว (แถวเหมือนกันทุกช่อง = รายการจริงสองรายการ) ⇒ ต่อท้าย "#2", "#3" ตามลำดับในไฟล์</item>
/// <item>ยาวเกิน 200 ตัวอักษร (คอลัมน์) ⇒ "h:" + SHA-256 ของค่าเต็ม</item>
/// </list></para>
/// </summary>
public static class SettlementTxnKey
{
    public const int MaxLength = 200;

    /// <summary>คีย์ของทุกแถวตามลำดับเดิม</summary>
    public static IReadOnlyList<string> Assign(IReadOnlyList<SettlementTxnKeyInput> rows)
    {
        var used = new Dictionary<string, int>(StringComparer.Ordinal);
        var keys = new List<string>(rows.Count);
        foreach (var r in rows)
        {
            string key;
            if (!string.IsNullOrWhiteSpace(r.RawTxnId))
            {
                var id = r.RawTxnId.Trim();
                var label = SettlementLineClassification.NormalizeLabel(r.Label);
                key = label.Length == 0 ? id : id + "|" + label;
            }
            else
            {
                var basis = string.Join("|",
                    (r.OrderId ?? "").Trim(),
                    SettlementLineClassification.NormalizeLabel(r.Label),
                    r.Amount.ToString("0.00", CultureInfo.InvariantCulture),
                    r.Date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "");
                key = "row:" + Hash(basis)[..40];
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

    private static string Fit(string key) => key.Length <= MaxLength ? key : "h:" + Hash(key);

    private static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
}
