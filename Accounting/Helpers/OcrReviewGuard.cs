using System.Globalization;
using System.Text.Json;

namespace Accounting.Helpers;

/// <summary>
/// **ด่านตรวจคำตอบของปุ่ม "🤖 ตรวจสอบกับ AI" ก่อนให้มันแตะฟอร์ม**
///
/// ═══ ที่มา (ผลตรวจไปป์ไลน์ OCR 2026-09-06 · T3-07) ═══
/// <para>ทุก AI call บนเส้น <c>ScanAsync</c> มี anti-hallucination guard ครบ (enum ·
/// ผังบัญชีของบริษัทนี้ · รายชื่อคู่ค้า · <c>OcrLineSplitGuard</c>) — <b>ยกเว้นปุ่มนี้</b>
/// ซึ่งเขียนคำตอบ AI ลงฟอร์ม<b>ตรง ๆ ทุกช่อง รวมยอดเงินและเลขผู้เสียภาษี</b> โดย
/// เซิร์ฟเวอร์ตรวจแค่ว่า JSON มี key ครบไหม ⇒ ตัวเลขที่โมเดลแต่งขึ้นกลายเป็นยอดบน
/// ใบกำกับ §86/4 และ ภ.พ.30 ด้วยการกดปุ่มเดียว — ขัด anti-pattern ที่ CLAUDE.md
/// เขียนไว้ตรง ๆ ว่า "ห้ามใช้คำตอบ AI โดยไม่ validate"</para>
///
/// <para>กติกา: เซิร์ฟเวอร์เป็นคนตัดสิน ไม่ใช่หน้าเว็บ — คืน <c>Accepted</c> (ช่องที่
/// ผ่านด่าน) กับ <c>Rejected</c> (ช่องที่ไม่ผ่าน + เหตุผลภาษาคน) แล้ว UI เอา
/// Rejected ไปแสดงเป็น "คำแนะนำ" ไม่ใช่เขียนทับ</para>
/// </summary>
public static class OcrReviewGuard
{
    /// <summary>ช่องที่ถูกปฏิเสธ + เหตุผลที่เอาไปโชว์ได้</summary>
    public readonly record struct RejectedField(string Field, string Value, string Reason);

    /// <param name="Accepted">ชื่อช่อง → ค่าที่ผ่านด่าน (ส่งให้ UI เขียนลงฟอร์มได้)</param>
    /// <param name="Rejected">ช่องที่ไม่ผ่าน — UI แสดงเป็นคำแนะนำเท่านั้น</param>
    public sealed record Result(
        IReadOnlyDictionary<string, string> Accepted,
        IReadOnlyList<RejectedField> Rejected);

    private static readonly Result Empty = new(
        new Dictionary<string, string>(), Array.Empty<RejectedField>());

    /// <summary>ค่าที่ตัวกรอง PII ปิดบังไว้ ("0xxxxxxxxx5") ห้ามเขียนกลับลงข้อมูลจริง</summary>
    private static bool LooksMasked(string? v) =>
        !string.IsNullOrEmpty(v) && v!.Count(c => c is 'x' or 'X') >= 3;

    /// <summary>
    /// กรอง <c>corrections</c> จาก <c>OcrReviewPrompt</c> เทียบกับความจริงบนแถวสแกน
    /// </summary>
    /// <param name="structuredJson">JSON ที่โมเดลตอบ (มี key <c>corrections</c>)</param>
    /// <param name="paperSubTotal">ยอดก่อน VAT ที่ engine อ่านได้ (ใช้เทียบสามเหลี่ยม)</param>
    /// <param name="paperVat">VAT ที่ engine อ่านได้</param>
    /// <param name="paperTotal">ยอดรวมที่ engine อ่านได้</param>
    public static Result Filter(string? structuredJson,
        decimal? paperSubTotal, decimal? paperVat, decimal? paperTotal)
    {
        if (string.IsNullOrWhiteSpace(structuredJson)) return Empty;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(structuredJson); }
        catch (JsonException) { return Empty; }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("corrections", out var c)
                || c.ValueKind != JsonValueKind.Object)
                return Empty;

            var accepted = new Dictionary<string, string>(StringComparer.Ordinal);
            var rejected = new List<RejectedField>();

            void Take(string field, Func<string, (bool Ok, string Why)> check)
            {
                if (!c.TryGetProperty(field, out var el)) return;
                var raw = el.ValueKind switch
                {
                    JsonValueKind.String => el.GetString(),
                    JsonValueKind.Number => el.GetRawText(),
                    _ => null,
                };
                if (string.IsNullOrWhiteSpace(raw)) return;
                var v = raw!.Trim();
                if (LooksMasked(v))
                {
                    rejected.Add(new RejectedField(field, v, "ค่าถูกปิดบังก่อนส่งให้โมเดล (PDPA) — ไม่ใช่ข้อมูลจริง"));
                    return;
                }
                var (ok, why) = check(v);
                if (ok) accepted[field] = v;
                else rejected.Add(new RejectedField(field, v, why));
            }

            // เลขผู้เสียภาษี — ต้องผ่าน mod-11 ไทย (ตัวเดียวกับทั้งระบบ)
            (bool, string) TaxIdCheck(string v) =>
                ThaiTaxId.IsValid(v) ? (true, "") : (false, "เลขผู้เสียภาษีไม่ผ่านการตรวจหลักที่ 13 (mod-11)");
            Take("vendor_tax_id", TaxIdCheck);
            Take("buyer_tax_id", TaxIdCheck);

            // วันที่ — ต้อง parse ได้และอยู่ในช่วงที่เป็นไปได้ของเอกสารบัญชี
            Take("document_date", v =>
                DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                && d.Year >= 2000 && d <= DateTime.UtcNow.AddDays(370)
                    ? (true, "")
                    : (false, "วันที่อ่านไม่ได้หรืออยู่นอกช่วงที่เป็นไปได้"));

            // ข้อความล้วน — ไม่มีอะไรให้พิสูจน์ นอกจากต้องไม่ว่างและไม่ถูกปิดบัง
            Take("document_number", _ => (true, ""));
            Take("vendor_name", _ => (true, ""));
            Take("buyer_name", _ => (true, ""));

            // ── ยอดเงินสามช่อง: รับ "ทั้งชุด" เท่านั้น ──────────────────────────
            // ยอดเงินคือสิ่งที่กลายเป็นรายการบัญชีจริง — รับทีละช่องแล้วเอาไปผสมกับ
            // ค่าที่อ่านมาเดิม = ได้ใบที่ sub + vat ≠ total โดยไม่มีใครเห็น
            var sub = Money(c, "sub_total");
            var vat = Money(c, "vat_amount");
            var tot = Money(c, "total_amount");
            if (sub.HasValue || vat.HasValue || tot.HasValue)
            {
                var s = sub ?? paperSubTotal;
                var v2 = vat ?? paperVat;
                var t = tot ?? paperTotal;
                if (s is > 0m && v2 is >= 0m && t is > 0m && Math.Abs(s.Value + v2.Value - t.Value) <= 1m)
                {
                    if (sub.HasValue) accepted["sub_total"] = sub.Value.ToString(CultureInfo.InvariantCulture);
                    if (vat.HasValue) accepted["vat_amount"] = vat.Value.ToString(CultureInfo.InvariantCulture);
                    if (tot.HasValue) accepted["total_amount"] = tot.Value.ToString(CultureInfo.InvariantCulture);
                }
                else
                {
                    var shown = $"sub={sub?.ToString("N2", CultureInfo.InvariantCulture) ?? "-"} · " +
                                $"vat={vat?.ToString("N2", CultureInfo.InvariantCulture) ?? "-"} · " +
                                $"total={tot?.ToString("N2", CultureInfo.InvariantCulture) ?? "-"}";
                    rejected.Add(new RejectedField("amounts", shown,
                        "ยอดที่เสนอทำให้ ยอดก่อนภาษี + ภาษี ≠ ยอดรวม — ไม่เขียนทับยอดที่อ่านจากกระดาษ"));
                }
            }

            return new Result(accepted, rejected);
        }
    }

    private static decimal? Money(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el)) return null;
        if (el.ValueKind == JsonValueKind.Number) return el.GetDecimal();
        if (el.ValueKind == JsonValueKind.String)
        {
            var raw = (el.GetString() ?? "").Replace(",", "").Trim();
            if (decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var v)) return v;
        }
        return null;
    }
}
