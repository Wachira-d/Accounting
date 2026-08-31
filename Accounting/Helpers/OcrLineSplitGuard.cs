using System.Text.Json;

namespace Accounting.Helpers;

/// <summary>
/// ด่านตรวจผลลัพธ์ "AI แตกบรรทัดจากข้อความ" ก่อนรับเข้าระบบ
///
/// ═══ ทำไมต้องมี ═══
/// บรรทัดที่ AI แต่งขึ้นจะกลายเป็น <b>รายการทางบัญชีจริง</b> (DocumentLine →
/// JE → รายงานภาษี) การรับบางบรรทัดที่ "ดูเข้าท่า" จึงอันตรายกว่าการไม่รับเลย
/// — ระบบมี local path อยู่แล้ว (บรรทัดสรุปใบเดียวจากยอดหัวกระดาษ) ที่ลงบัญชี
/// ได้ถูกต้องเสมอ ⇒ ทิ้งทั้งชุดคือทางที่ปลอดภัยกว่า
///
/// ═══ กติกา ═══
/// <list type="number">
/// <item>บรรทัดต้องมีคำอธิบาย และยอด &gt; 0 — ไม่ครบ = ตัดทิ้งทั้งบรรทัด</item>
/// <item>ผลรวมทุกบรรทัดต้องลงตัวกับ <b>ยอดก่อนภาษี</b> หรือ <b>ยอดรวม</b>
///   (กรณีราคารวม VAT) ภายใน ±1 บาท</item>
/// <item>ไม่ลงตัว = <b>ทิ้งทั้งชุด</b> ไม่ใช่รับเท่าที่รับได้</item>
/// </list>
/// </summary>
public static class OcrLineSplitGuard
{
    /// <summary>ค่าคลาดเคลื่อนที่ยอมรับได้ (บาท) — เผื่อการปัดเศษบนกระดาษ</summary>
    public const decimal ToleranceBaht = 1m;

    public sealed record SplitLine(
        string Description,
        decimal Quantity,
        decimal? UnitPrice,
        decimal Amount,
        string? Unit);

    /// <param name="Accepted">รับผลลัพธ์ไปใช้ได้หรือไม่</param>
    /// <param name="Lines">บรรทัดที่ผ่านด่าน (ว่างเมื่อ <c>Accepted=false</c>)</param>
    /// <param name="Sum">ผลรวมยอดของบรรทัดที่ parse ได้ — ใช้เขียนเหตุผลให้ผู้ใช้</param>
    /// <param name="Reason">เหตุผลภาษาไทยเมื่อไม่รับ (null เมื่อรับ)</param>
    public sealed record GuardResult(
        bool Accepted,
        IReadOnlyList<SplitLine> Lines,
        decimal Sum,
        string? Reason);

    private static GuardResult Reject(string reason, decimal sum = 0m)
        => new(false, Array.Empty<SplitLine>(), sum, reason);

    /// <summary>
    /// อ่าน JSON ที่โมเดลตอบกลับ แล้วตัดสินว่ารับได้ไหม
    /// </summary>
    /// <param name="responseJson">JSON ดิบจากโมเดล (คาดว่ามีคีย์ <c>lines</c>)</param>
    /// <param name="subTotal">ยอดก่อนภาษีจากหัวกระดาษ (0 = ไม่รู้)</param>
    /// <param name="totalAmount">ยอดรวมจากหัวกระดาษ (0 = ไม่รู้)</param>
    public static GuardResult Evaluate(string? responseJson, decimal subTotal, decimal totalAmount)
    {
        if (string.IsNullOrWhiteSpace(responseJson)) return Reject("โมเดลไม่ได้ตอบอะไรกลับมา");
        if (subTotal <= 0m && totalAmount <= 0m)
            return Reject("ไม่มียอดบนหัวกระดาษให้ตรวจสอบผลลัพธ์");

        JsonElement root;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(responseJson); }
        catch (JsonException) { return Reject("รูปแบบคำตอบของโมเดลไม่ใช่ JSON ที่อ่านได้"); }

        using (doc)
        {
            root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("lines", out var linesEl)
                || linesEl.ValueKind != JsonValueKind.Array)
                return Reject("คำตอบไม่มีรายการบรรทัด");

            var parsed = new List<SplitLine>();
            foreach (var el in linesEl.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;

                var desc = el.TryGetProperty("description", out var d) ? d.GetString() : null;
                if (string.IsNullOrWhiteSpace(desc)) continue;

                var amount = Num(el, "amount");
                if (amount is not > 0m) continue;             // ไม่มียอด = ไม่รับบรรทัดนี้

                var qty = Num(el, "quantity") is { } q && q > 0m ? q : 1m;
                var unitPrice = Num(el, "unit_price")
                    ?? (qty > 0m ? Math.Round(amount.Value / qty, 4, MidpointRounding.AwayFromZero) : null);
                var unit = el.TryGetProperty("unit", out var u) ? u.GetString() : null;

                parsed.Add(new SplitLine(desc!.Trim(), qty, unitPrice, amount.Value,
                    string.IsNullOrWhiteSpace(unit) ? null : unit!.Trim()));
            }

            if (parsed.Count == 0) return Reject("ไม่มีบรรทัดที่ใช้ได้เลย");

            var sum = parsed.Sum(x => x.Amount);
            var matchesSub = subTotal > 0m && Math.Abs(sum - subTotal) <= ToleranceBaht;
            var matchesTotal = totalAmount > 0m && Math.Abs(sum - totalAmount) <= ToleranceBaht;
            if (!matchesSub && !matchesTotal)
                return Reject(
                    $"ผลรวมบรรทัด ฿{sum:N2} ไม่ตรงกับยอดบนกระดาษ " +
                    $"(ก่อนภาษี ฿{subTotal:N2} / รวม ฿{totalAmount:N2})", sum);

            return new GuardResult(true, parsed, sum, null);
        }

        static decimal? Num(JsonElement el, string name)
            => el.TryGetProperty(name, out var v)
               && v.ValueKind == JsonValueKind.Number
               && v.TryGetDecimal(out var dec) ? dec : null;
    }
}
