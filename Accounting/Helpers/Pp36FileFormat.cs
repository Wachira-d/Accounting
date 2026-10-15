using System.Globalization;
using System.Text;

namespace Accounting.Helpers;

/// <summary>
/// **ไฟล์รายการ ภ.พ.36 (ตัวตั้งเดียว)** — ทั้งเมนู "Export ยื่นภาษี" (<c>TaxFilingExportService.ExportPp36Async</c>) และปุ่ม e-Filing
/// ในหน้ารายงานภาษี (<c>TaxService.BuildPp36Async</c>) ประกอบไฟล์ผ่านคลาสนี้ (รอบ 2026-10-14 ทีม X1)
///
/// <para><b>ที่มา</b>: เดิมสองปุ่มให้ไฟล์ ภ.พ.36 <b>คนละหน้าตา</b>จากงวดเดียวกัน — เมนู Export เขียน <c>H|…</c>/<c>D|…</c>/<c>T|…</c> + วันที่
/// <c>dd/MM/พ.ศ.</c> + "ประเทศ" ที่จริงเป็น<b>จังหวัด</b>ของผู้ติดต่อ (<c>Contact.Province</c>) · ปุ่ม e-Filing เขียนบรรทัดหัวสรุป + วันที่
/// <c>ddMMyyyy</c> + รหัสประเภทภายใน (defect class "สอง renderer ห้าม drift" — กฎเหล็ก #4 A)</para>
///
/// <para>ใช้ข้อตกลงเดียวกับไฟล์ ภ.ง.ด. ที่ยืนยันกับหน้านำเข้าจริงแล้ว (<see cref="PndTextFileFormat"/>): detail rows ล้วน · <c>|</c> · CRLF ·
/// <c>ddMMyyyy</c> ค.ศ. · เงิน 2 ตำแหน่ง</para>
///
/// <para>⚠️ <b>ไม่ใช่ไฟล์นำเข้าเว็บสรรพากร</b> — ภ.พ.36 ยื่นโดยกรอกบนหน้าแบบทีละรายการ (ยังไม่พบหน้านำเข้าไฟล์ของ ภ.พ.36 · ต้องยืนยันกับ
/// เจ้าของ) ไฟล์นี้คือ "ใบงาน" ให้คัดลอกค่าไปกรอก · คอลัมน์:
/// ลำดับ · ชื่อผู้ให้บริการต่างประเทศ · ประเทศ (รหัส ISO ของผู้ติดต่อ) · วันที่จ่าย · เลขที่เอกสาร · รายละเอียด · มูลค่าที่ต้องเสียภาษี ·
/// อัตรา · ภาษีที่ต้องนำส่ง</para>
/// </summary>
public static class Pp36FileFormat
{
    /// <summary>1 รายการจ่ายค่าบริการ/สิทธิให้ผู้ประกอบการต่างประเทศ</summary>
    public sealed record Row(
        string? SupplierName,
        string? CountryCode,
        DateTime PayDate,
        string? DocumentNo,
        string? Description,
        decimal BaseAmount,
        decimal VatRate,
        decimal VatAmount);

    /// <summary>หัวคอลัมน์ (ไฟล์ .txt ไม่มีแถวหัว — ใช้แสดง/อธิบาย)</summary>
    public static readonly IReadOnlyList<string> Headers = new[]
    {
        "ลำดับที่", "ชื่อผู้ให้บริการต่างประเทศ", "ประเทศ", "วันที่จ่าย (ddMMyyyy ค.ศ.)", "เลขที่เอกสาร", "รายละเอียด",
        "มูลค่าที่ต้องเสียภาษี", "อัตราภาษี", "ภาษีที่ต้องนำส่ง",
    };

    /// <summary>ไฟล์ทั้งฉบับ — detail rows ล้วน คั่นด้วย CRLF</summary>
    public static string Build(IEnumerable<Row> rows)
    {
        var sb = new StringBuilder();
        var seq = 1;
        foreach (var r in rows)
            sb.Append(DetailRow(seq++, r)).Append("\r\n");
        return sb.ToString();
    }

    /// <summary>1 บรรทัด (ไม่มี CRLF)</summary>
    public static string DetailRow(int seq, Row r) => string.Join("|",
        seq.ToString(CultureInfo.InvariantCulture),
        Clean(r.SupplierName),
        Clean(r.CountryCode).ToUpperInvariant(),
        r.PayDate.ToString("ddMMyyyy", CultureInfo.InvariantCulture),
        Clean(r.DocumentNo),
        Clean(r.Description),
        Money(r.BaseAmount),
        Money(r.VatRate),
        Money(r.VatAmount));

    private static string Money(decimal v) =>
        Math.Round(v, 2, MidpointRounding.AwayFromZero).ToString("0.00", CultureInfo.InvariantCulture);

    private static string Clean(string? raw) =>
        (raw ?? "").Replace("|", " ").Replace("\r", " ").Replace("\n", " ").Trim();
}
