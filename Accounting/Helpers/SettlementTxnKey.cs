using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Accounting.Helpers;

/// <summary>ข้อมูลของแถวที่ใช้ทำคีย์ — ป้ายดิบถูก<b>แฮช</b>ก่อนเก็บ (ไม่มี PII ในคีย์) · <paramref name="PayoutRef"/> = เลขรอบโอน<b>จากคอลัมน์ในไฟล์</b>
/// เท่านั้น (ทีม S4 · review198-S3 S3-4: ห้ามส่งเลขที่ผู้ใช้พิมพ์ — ไฟล์ไม่มีคอลัมน์ ⇒ null แล้ว <see cref="SettlementTxnKey.Assign"/> ใช้ลายนิ้วมือเนื้อหาไฟล์แทน)</summary>
public readonly record struct SettlementTxnKeyInput(string? RawTxnId, string? Label, string? OrderId, decimal Amount, DateTime? Date,
    string? PayoutRef = null);

/// <summary>บรรทัดเดิมแบบไม่มี id: คีย์เนื้อหา (<see cref="SettlementTxnKey.ContentKey"/>) + ไฟล์ที่นำเข้ามา (<c>SettlementLine.ImportScope</c> · null = ก่อนรอบ 200)</summary>
public readonly record struct SettlementStoredContent(string ContentKey, string? ImportScope);

/// <summary>ผลของ <see cref="SettlementTxnKey.SplitRevisedFilePool"/> — คีย์เนื้อหาของบรรทัดเดิมที่เทียบได้ (ไฟล์รุ่นก่อนของไฟล์นี้) และที่ห้ามกลืน (ไฟล์อื่น)</summary>
public sealed record SettlementContentPool(IReadOnlyList<string> SameFile, IReadOnlyList<string> OtherFiles);

/// <summary>
/// **คีย์กันนำเข้าซ้ำของบรรทัด settlement (<c>SettlementLine.ExternalTxnId</c> · unique ต่อช่องทาง) — pure · deterministic · รุ่น v2**
/// (รอบ 198 เฟส 1 ทีม B · แก้ฝ่ายค้าน review198-B R-B5/R-B6 ทีม S3 · review198-S3 S3-4 ทีม S4)
///
/// <para>ที่มา: นำเข้าไฟล์เดิมซ้ำ (กดสองครั้ง · ไฟล์รอบโอนที่ช่วงวันทับกัน) ต้องไม่เกิดบรรทัดซ้ำ ⇒ ยอดรายได้/ค่าธรรมเนียมซ้ำ —
/// และ<b>ทิศตรงข้าม</b> (R-B5): สองแถวที่เป็นรายการจริงคนละรายการต้องไม่ได้คีย์เดียวกัน ⇒ แถวหลังถูกทิ้งเงียบ ๆ ด้วยสถานะ "ซ้ำ"
/// (รุ่นแรกใช้ id + ป้ายที่ตัดตัวเลขทิ้ง ⇒ คืนเงินบางส่วนครั้งที่สองของ id เดียวกัน · "ค่าธรรมเนียม 3%" กับ "5%" ชนกัน · แถวไม่มี id ของสองรอบโอน
/// ในวันเดียวกัน "Withdrawal fee −10" ชนกัน)</para>
/// <para>═══ กติกา v2 ═══
/// <list type="number">
/// <item>id ของ adapter ภายใน (<c>pi:</c> — <see cref="ForPaymentIntent"/>) ⇒ ใช้ตามนั้น (ไม่ซ้ำโดยการออกแบบ)</item>
/// <item>มี id ดิบ ⇒ <c>v2:</c> + id + ":" + แฮช(ป้าย | ยอด | วันที่) — ยอดขาย/ค่าธรรมเนียม/คืนเงินของ id เดียวกันได้คีย์คนละตัว ·
/// คืนเงินครั้งที่สองยอดต่างกันได้คีย์ใหม่ · <b>ไม่ใส่รอบโอน</b> (id ของแพลตฟอร์มเป็นตัวกันซ้ำข้ามรอบ) · ขึ้นกับเนื้อหาของแถวเท่านั้น (R-A9)</item>
/// <item>ไม่มี id + ไฟล์มี<b>คอลัมน์</b>เลขรอบโอน ⇒ <c>v2:row:</c> + แฮช(ออเดอร์ | ป้าย | ยอด | วันที่ | รอบโอนของแถว) — เหมือนรุ่นก่อนทุกตัวอักษร
/// (คีย์ที่เก็บไว้แล้วไม่เปลี่ยน)</item>
/// <item>ไม่มี id + ไฟล์<b>ไม่มี</b>คอลัมน์เลขรอบโอน ⇒ <c>v2:rowc:</c> + แฮช(ออเดอร์ | ป้าย | ยอด | วันที่ | <b>ลายนิ้วมือเนื้อหาไฟล์</b>
/// <see cref="ContentScope"/>) — S3-4: รุ่นก่อนใช้เลขรอบโอนที่ผู้ใช้<b>พิมพ์</b> ⇒ นำเข้าไฟล์เดิมด้วยเลขที่พิมพ์ต่าง (แก้คำผิด) ได้คีย์ใหม่ทั้งไฟล์ ⇒
/// ค่าธรรมเนียม/ปรับปรุงซ้ำทั้งก้อน · ลายนิ้วมือ = แฮชของทุกแถวที่นำเข้าครั้งนี้ (เรียงแล้ว — ส่งออกใหม่สลับลำดับ/เปลี่ยนรูปแบบไฟล์ได้ค่าเดิม) ⇒
/// ไฟล์เดิมได้คีย์เดิมไม่ว่าพิมพ์เลขอะไร · สองรอบโอนที่มีแถวหน้าตาเหมือนกันแต่ไฟล์ต่างกันไม่ชน (R-B5 คงอยู่) ·
/// ข้อจำกัดที่รู้: ไฟล์ฉบับแก้ของรอบเดิม (แถวเพิ่ม) ได้ลายนิ้วมือใหม่ — ผู้นำเข้าจับด้วย "เนื้อหาตรงกับบรรทัดของรอบโอนเดียวกัน"
/// (<see cref="ContentKey"/> · <see cref="MatchByContent"/>) แทน</item>
/// <item>ยังชนกันในไฟล์เดียว (แถวเหมือนกัน<b>ทุกช่อง</b> = รายการจริงหลายรายการ) ⇒ ต่อท้าย "#2", "#3" ตามลำดับในไฟล์ — ลำดับที่นับเฉพาะแถวที่เหมือนกันทุกช่อง</item>
/// <item>ยาวเกิน 200 ตัวอักษร (คอลัมน์) ⇒ "h:" + SHA-256 ของค่าเต็ม</item>
/// </list>
/// ป้ายผ่านตัว normalize ของที่นี่เอง (<see cref="FrozenLabel"/> — ตัดช่องว่างซ้ำ · ตัวพิมพ์เล็ก · <b>คงตัวเลข</b>) ไม่ใช้ตัวตัด PII/ตัวจัดประเภท
/// ร่วมกัน (R-B6: ปรับ regex ของสองตัวนั้นแล้วคีย์ของแถวที่นำเข้าไปแล้วต้องไม่เปลี่ยน) · เปลี่ยนกติกา = เปลี่ยนรุ่น/คำนำหน้า</para>
/// <para>═══ คีย์ที่เก็บไว้ด้วยกติการุ่นก่อน (S3-4 ข้อ 2) ═══ <see cref="LegacyKeys"/> คืนคีย์ของแถวเดียวกันตามกติการุ่น v1 (เฟส 1 ทีม B) และ v2 ก่อนแก้
/// (แถวไม่มี id ใช้เลขรอบโอนที่พิมพ์) — ผู้นำเข้า<b>ใช้เทียบเท่านั้น</b> (แถวที่มีคีย์ตัวใดตัวหนึ่งในช่องทางแล้ว = มีอยู่แล้ว) · บรรทัดใหม่เก็บคีย์รุ่นปัจจุบัน</para>
/// </summary>
public static class SettlementTxnKey
{
    public const int MaxLength = 200;

    /// <summary>คำนำหน้ารุ่นของกติกาคีย์ — เปลี่ยนกติกาแล้วต้องเปลี่ยนรุ่น (คีย์รุ่นเก่ากับใหม่ไม่ชนกันโดยบังเอิญ)</summary>
    public const string Version = "v2:";

    /// <summary>คีย์ของทุกแถวตามลำดับเดิม (รุ่นปัจจุบัน — คีย์ที่บรรทัดใหม่เก็บ)</summary>
    public static IReadOnlyList<string> Assign(IReadOnlyList<SettlementTxnKeyInput> rows)
    {
        string? scope = null;   // คิดเมื่อมีแถวที่ต้องใช้เท่านั้น
        return AssignCore(rows, r =>
        {
            var (label, amount, date) = Parts(r);
            if (!string.IsNullOrWhiteSpace(r.RawTxnId)) return IdKey(r.RawTxnId.Trim(), label, amount, date);
            var column = (r.PayoutRef ?? "").Trim();
            if (column.Length > 0)
                return Version + "row:" + Hash(string.Join("|", (r.OrderId ?? "").Trim(), label, amount, date, column))[..40];
            scope ??= ContentScope(rows);
            return Version + "rowc:" + Hash(string.Join("|", (r.OrderId ?? "").Trim(), label, amount, date, scope))[..40];
        });
    }

    /// <summary>
    /// **คีย์ของแถวเดียวกันตามกติการุ่นก่อน — ใช้เทียบกับบรรทัดที่นำเข้าไว้แล้วเท่านั้น** (S3-4 ข้อ 2 · ไม่ใช่คีย์ที่เก็บ)
    /// <list type="bullet">
    /// <item>v1 (เฟส 1 ทีม B): id + "|" + ป้ายของตัวจัดประเภท (ป้ายผ่านตัวตัด PII) · ไม่มี id ⇒ <c>row:</c> + แฮช(ออเดอร์|ป้าย|ยอด|วันที่) —
    /// ใช้ตัว normalize ของ<b>วันนี้</b> ⇒ ถ้าตัว normalize เปลี่ยนหลังนำเข้า คีย์ v1 บางแถวอาจไม่ตรง (ข้อจำกัดที่รู้ · R-B6 คือเหตุที่ v2 เลิกใช้)</item>
    /// <item>v2 ก่อนแก้ S3-4: แถวไม่มี id ที่ไฟล์ไม่มีคอลัมน์รอบโอน ⇒ <c>v2:row:</c> + แฮช(… | เลขรอบโอนที่ผู้ใช้พิมพ์ <paramref name="typedPayoutRef"/>)</item>
    /// </list>
    /// คืนรายการต่อแถว (ลำดับเดียวกับ <paramref name="rows"/>) — คีย์ที่เท่ากับรุ่นปัจจุบันไม่ซ้ำใส่
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> LegacyKeys(IReadOnlyList<SettlementTxnKeyInput> rows, string? typedPayoutRef)
    {
        var v1 = AssignCore(rows, r =>
        {
            var label = SettlementLineClassification.NormalizeLabel(SettlementPiiScrubber.Scrub(r.Label));
            if (!string.IsNullOrWhiteSpace(r.RawTxnId))
            {
                var id = r.RawTxnId.Trim();
                return label.Length == 0 ? id : id + "|" + label;
            }
            return "row:" + Hash(string.Join("|", (r.OrderId ?? "").Trim(), label,
                r.Amount.ToString("0.00", CultureInfo.InvariantCulture),
                r.Date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? ""))[..40];
        });
        var typed = (typedPayoutRef ?? "").Trim();
        var v2Typed = AssignCore(rows, r =>
        {
            var (label, amount, date) = Parts(r);
            if (!string.IsNullOrWhiteSpace(r.RawTxnId)) return IdKey(r.RawTxnId.Trim(), label, amount, date);
            var column = (r.PayoutRef ?? "").Trim();
            return Version + "row:" + Hash(string.Join("|", (r.OrderId ?? "").Trim(), label, amount, date,
                column.Length > 0 ? column : typed))[..40];
        });
        var current = Assign(rows);
        var result = new List<IReadOnlyList<string>>(rows.Count);
        for (var i = 0; i < rows.Count; i++)
            result.Add(new[] { v1[i], v2Typed[i] }
                .Where(k => !string.Equals(k, current[i], StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal).ToList());
        return result;
    }

    /// <summary>ลายนิ้วมือเนื้อหาของแถวทั้งชุดที่นำเข้าครั้งนี้ — ไม่ขึ้นกับลำดับแถว/รูปแบบไฟล์/สิ่งที่ผู้ใช้พิมพ์ (S3-4)</summary>
    internal static string ContentScope(IReadOnlyList<SettlementTxnKeyInput> rows)
    {
        var bases = rows.Select(r =>
        {
            var (label, amount, date) = Parts(r);
            return string.Join("|", (r.RawTxnId ?? "").Trim(), (r.OrderId ?? "").Trim(), label, amount, date);
        }).OrderBy(x => x, StringComparer.Ordinal);
        return Hash(string.Join("\n", bases))[..32];
    }

    /// <summary>
    /// **คีย์เนื้อหาของแถว** (ออเดอร์ | ป้าย | ยอด | วันที่ — ไม่มีรอบโอน/ลายนิ้วมือ/id) — ใช้เทียบแถวใหม่ที่ไม่มี id กับบรรทัดที่เก็บแล้ว
    /// (ผู้เรียกส่งค่าที่ผ่านการตัด PII/ตัดความยาวแบบเดียวกับที่เก็บ ⇒ สองฝั่งเทียบกันได้) · ไม่ใช่คีย์ที่เก็บ (S3-4)
    /// </summary>
    public static string ContentKey(string? orderId, string? storedLabel, decimal amount, DateTime? date)
        => "c:" + Hash(string.Join("|", (orderId ?? "").Trim(), FrozenLabel(storedLabel),
            Math.Round(amount, 2, MidpointRounding.AwayFromZero).ToString("0.00", CultureInfo.InvariantCulture),
            date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? ""))[..40];

    /// <summary>บรรทัดที่เก็บแล้วเป็นแถวที่ไม่มี id ไหม (คีย์ <c>v2:row:</c> · <c>v2:rowc:</c> · v1 <c>row:</c>)</summary>
    public static bool IsRowKey(string? externalTxnId)
        => externalTxnId != null
           && (externalTxnId.StartsWith(Version + "row", StringComparison.Ordinal)
               || externalTxnId.StartsWith("row:", StringComparison.Ordinal));

    /// <summary>
    /// **จับคู่แถวใหม่กับบรรทัดเดิมด้วยเนื้อหาแบบนับจำนวน (multiset)** — คืน index ของแถวใหม่ที่มีบรรทัดเดิมเนื้อหาเดียวกันรองรับ
    /// (บรรทัดเดิม 1 บรรทัดรองรับได้ 1 แถว · แถวเหมือนกัน 3 แถวกับบรรทัดเดิม 2 บรรทัด ⇒ 2 แถวนับว่ามีแล้ว 1 แถวใหม่) · null = แถวที่ไม่เทียบ
    /// </summary>
    public static IReadOnlySet<int> MatchByContent(IReadOnlyList<string?> newContentKeys, IEnumerable<string> existingContentKeys)
    {
        var pool = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var k in existingContentKeys) pool[k] = pool.TryGetValue(k, out var n) ? n + 1 : 1;
        var matched = new HashSet<int>();
        for (var i = 0; i < newContentKeys.Count; i++)
        {
            var k = newContentKeys[i];
            if (k == null || !pool.TryGetValue(k, out var left) || left <= 0) continue;
            pool[k] = left - 1;
            matched.Add(i);
        }
        return matched;
    }

    /// <summary>
    /// **ลายนิ้วมือเนื้อหาของไฟล์ที่นำเข้าครั้งนี้** — ค่าเดียวกับที่ใส่ในคีย์ <c>v2:rowc:</c> (<see cref="ContentScope"/>) · เก็บต่อบรรทัดใน
    /// <c>SettlementLine.ImportScope</c> ให้ผู้นำเข้ารู้ว่าบรรทัดเดิมมาจาก "ไฟล์ไหน" (review198-S4 S4-3 · ทีม I รอบ 200) · ว่าง ⇒ null
    /// </summary>
    public static string? ImportScopeOf(IReadOnlyList<SettlementTxnKeyInput> rows)
        => rows.Count == 0 ? null : ContentScope(rows);

    /// <summary>
    /// **แยกบรรทัดเดิม (ไม่มี id) ของรอบโอนเดียวกันเป็น "ไฟล์รุ่นก่อนของไฟล์นี้" กับ "ไฟล์อื่น"** (review198-S4 S4-3 · ทีม I รอบ 200)
    /// <para>ที่มา: S3-4 เทียบเนื้อหาแบบนับจำนวนกับบรรทัด<b>ทุกบรรทัด</b>ของรอบ ⇒ รอบที่สร้างจากไฟล์ 1 แล้ว<b>เติม</b>ไฟล์ 2 (ส่วนที่เหลือของรอบ) ซึ่งมีรายการจริง
    /// ที่หน้าตาเหมือนแถวในไฟล์ 1 ทุกช่อง (ค่าธรรมเนียมถอนเงิน −10 วันเดียวกัน) ⇒ แถวจริงถูกข้ามว่า "นำเข้าแล้ว" (R-B5 ถอยในรอบเดียว)</para>
    /// <para>กติกา: บรรทัดเดิมจัดกลุ่มตาม <c>ImportScope</c> (ไฟล์ที่นำเข้ามา) · กลุ่มที่<b>ทุกบรรทัด</b>มีแถวเนื้อหาเดียวกันในไฟล์นี้ (นับจำนวน ⊆) =
    /// ไฟล์นี้คือฉบับแก้ของไฟล์นั้น ⇒ <c>SameFile</c> (เทียบเนื้อหาได้ เหมือน S3-4) · กลุ่มที่ไฟล์นี้ไม่ครอบทั้งหมด = ไฟล์อื่น ⇒ <c>OtherFiles</c>
    /// (ห้ามกลืนแถว — ผู้เรียกเตือนรายแถวแทน · ทิศที่มองเห็นได้: แถวซ้ำโผล่ให้เห็นในรอบ + สมการรอบโอนไม่ลงตัว ดีกว่าแถวจริงหายเงียบ) ·
    /// บรรทัดที่นำเข้าก่อนมีคอลัมน์ (<c>ImportScope</c> null) = พฤติกรรมเดิมของ S3-4 (<c>SameFile</c>) — ไม่มีข้อมูลให้แยก</para>
    /// </summary>
    public static SettlementContentPool SplitRevisedFilePool(IReadOnlyList<string?> newContentKeys, IReadOnlyList<SettlementStoredContent> stored)
    {
        var incoming = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var k in newContentKeys)
            if (k != null) incoming[k] = incoming.TryGetValue(k, out var n) ? n + 1 : 1;
        var same = new List<string>();
        var other = new List<string>();
        foreach (var g in stored.GroupBy(x => x.ImportScope, StringComparer.Ordinal))
        {
            var keys = g.Select(x => x.ContentKey).ToList();
            if (g.Key == null) { same.AddRange(keys); continue; }
            var covered = keys.GroupBy(k => k, StringComparer.Ordinal)
                .All(kg => incoming.TryGetValue(kg.Key, out var have) && have >= kg.Count());
            (covered ? same : other).AddRange(keys);
        }
        return new SettlementContentPool(same, other);
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

    /// <summary>ต่อท้าย "#n" ให้แถวที่คีย์ฐานซ้ำกันในชุดเดียว (ตามลำดับ) + ตัดความยาว — ร่วมทุกรุ่นของกติกา</summary>
    private static IReadOnlyList<string> AssignCore(IReadOnlyList<SettlementTxnKeyInput> rows, Func<SettlementTxnKeyInput, string> baseKey)
    {
        var used = new Dictionary<string, int>(StringComparer.Ordinal);
        var keys = new List<string>(rows.Count);
        foreach (var r in rows)
        {
            var key = baseKey(r);
            var n = used.TryGetValue(key, out var seen) ? seen + 1 : 1;
            used[key] = n;
            if (n > 1) key += "#" + n.ToString(CultureInfo.InvariantCulture);
            keys.Add(Fit(key));
        }
        return keys;
    }

    private static (string Label, string Amount, string Date) Parts(SettlementTxnKeyInput r)
        => (FrozenLabel(r.Label), r.Amount.ToString("0.00", CultureInfo.InvariantCulture),
            r.Date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "");

    private static string IdKey(string id, string label, string amount, string date)
        => id.StartsWith("pi:", StringComparison.Ordinal) ? id : Version + id + ":" + Hash(string.Join("|", label, amount, date))[..24];

    private static string Fit(string key) => key.Length <= MaxLength ? key : "h:" + Hash(key);

    private static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
}
