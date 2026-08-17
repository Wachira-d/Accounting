using System.Globalization;
using System.Text;

namespace Accounting.Helpers;

/// <summary>
/// รูปแบบไฟล์ข้อความ (.txt) สำหรับ **นำเข้าแบบ ภ.ง.ด.3 / ภ.ง.ด.53 บนเว็บสรรพากร**
/// — pipe-delimited, **detail rows ล้วน ไม่มี header/trailer**
///
/// ที่มา: หน้า "นำเข้าข้อมูล" ของ RD ให้ผู้ใช้ map คอลัมน์จากไฟล์เข้ากับช่องของแบบ
/// เอง ⇒ บรรทัด <c>H|…</c> / <c>T|…</c> ที่ระบบเคยใส่ กลายเป็น "แถวข้อมูลขยะ"
/// 2 แถวที่ผู้ใช้ต้องลบเองทุกครั้ง และวันที่รูป <c>dd/MM/พ.ศ.</c> ที่มี slash ก็
/// map ไม่ได้ (หน้า RD รับ <c>ddMMyyyy</c> ติดกัน + มี radio พ.ศ./ค.ศ.)
///
/// ลำดับคอลัมน์ (ตรวจกับหน้า import จริงแล้ว — badge Col5-Col10 ตรงตามนี้):
/// <code>
///  Col1  ลำดับที่
///  Col2  เลขประจำตัวผู้เสียภาษีผู้ถูกหัก (13 หลัก, pad ซ้ายด้วย 0)
///  Col3  เลขที่สาขา 5 หลัก ("00000" = สำนักงานใหญ่)
///  Col4  ชื่อ        — นิติบุคคล = ชื่อเต็ม / บุคคลธรรมดา = ชื่อตัว
///  Col5  ชื่อสกุล    — บุคคลธรรมดาเท่านั้น (นิติบุคคล = ว่าง)
///  Col6  วันเดือนปีที่จ่าย  ddMMyyyy (ค.ศ. ไม่มีตัวคั่น เช่น 01072026)
///  Col7  ประเภทเงินได้ (รหัสตาม ท.ป.4/2528)
///  Col8  จำนวนเงินได้ที่จ่าย (15,2)
///  Col9  อัตราภาษี (4,2)
///  Col10 จำนวนภาษีที่หัก (15,2)
///  Col11 เงื่อนไขการหักภาษี  1=หัก ณ ที่จ่าย · 2=ออกให้ตลอดไป · 3=ออกให้ครั้งเดียว
/// </code>
///
/// ⚠️ **ตัวสร้างไฟล์ ภ.ง.ด.3/53 ทุกทางต้องเรียกคลาสนี้** — มีสองทางออกไฟล์
/// (<c>TaxFilingExportService.ExportPnd3/53Async</c> = เมนูส่งออก และ
/// <c>TaxService.EFiling.BuildPndAsync</c> = ปุ่ม e-Filing ในหน้ารายงาน)
/// ถ้าใครเขียน format เองอีกชุด ผู้ใช้จะได้ไฟล์คนละหน้าตาจากสองปุ่ม
/// (defect class "สอง renderer ห้าม drift" — CLAUDE.md กฎเหล็ก #4 A)
/// </summary>
public static class PndTextFileFormat
{
    /// <summary>1 รายการจ่ายเงินได้ (1 บรรทัดในไฟล์)</summary>
    public sealed record Row(
        string? PayeeTaxId,
        string? BranchCode,
        string? PayeeName,
        bool IsJuristic,
        DateTime PayDate,
        string? IncomeTypeCode,
        decimal IncomeAmount,
        decimal TaxRate,
        decimal TaxAmount,
        int Condition = 1);

    /// <summary>ประกอบไฟล์ทั้งฉบับ — detail rows ล้วน คั่นด้วย CRLF</summary>
    public static string Build(IEnumerable<Row> rows)
    {
        var sb = new StringBuilder();
        var seq = 1;
        foreach (var r in rows)
            sb.Append(DetailRow(seq++, r)).Append("\r\n");
        return sb.ToString();
    }

    /// <summary>1 บรรทัด (ไม่มี CRLF ต่อท้าย) — เปิด public เพื่อให้เทสต์ยิงตรงได้</summary>
    public static string DetailRow(int seq, Row r)
    {
        var (first, last) = SplitName(r.PayeeName, r.IsJuristic);
        return string.Join("|",
            seq.ToString(CultureInfo.InvariantCulture),                       // Col1
            Digits(r.PayeeTaxId).PadLeft(13, '0'),                            // Col2
            NormalizeBranch(r.BranchCode),                                    // Col3
            Clean(first),                                                     // Col4
            Clean(last),                                                      // Col5
            r.PayDate.ToString("ddMMyyyy", CultureInfo.InvariantCulture),      // Col6
            Clean(r.IncomeTypeCode),                                          // Col7
            Money(r.IncomeAmount),                                            // Col8
            Rate(r.TaxRate),                                                  // Col9
            Money(r.TaxAmount),                                               // Col10
            (r.Condition is 1 or 2 or 3 ? r.Condition : 1)
                .ToString(CultureInfo.InvariantCulture));                     // Col11
    }

    /// <summary>บุคคลธรรมดา → แยก "ชื่อตัว | ชื่อสกุล" ที่ช่องว่างแรก
    /// (คำนำหน้าถูกตัดทิ้ง — หน้า RD มีช่องคำนำหน้าแยกและเราไม่ได้ map).
    /// นิติบุคคล → ชื่อเต็มอยู่ Col4, Col5 ว่างเสมอ (สรรพากรไม่มีนามสกุลนิติบุคคล)</summary>
    public static (string First, string Last) SplitName(string? fullName, bool isJuristic)
    {
        var name = (fullName ?? "").Trim();
        if (isJuristic || name.Length == 0) return (name, "");

        foreach (var t in ThaiTitles)
            if (name.StartsWith(t, StringComparison.Ordinal))
            {
                name = name[t.Length..].TrimStart();
                break;
            }

        var sp = name.IndexOf(' ');
        return sp < 0 ? (name, "") : (name[..sp].Trim(), name[(sp + 1)..].Trim());
    }

    private static readonly string[] ThaiTitles =
        { "นางสาว", "น.ส.", "นาง", "นาย", "ดร.", "Mr.", "Mrs.", "Miss", "Ms." };

    /// <summary>ตัวเลขล้วน — เลขผู้เสียภาษีที่ผู้ใช้พิมพ์ขีดคั่นมาต้องส่งเป็น 13 หลักติดกัน</summary>
    private static string Digits(string? raw) =>
        new((raw ?? "").Where(char.IsDigit).ToArray());

    private static string NormalizeBranch(string? raw)
    {
        var d = Digits(raw);
        if (d.Length == 0) return "00000";
        return d.Length >= 5 ? d[^5..] : d.PadLeft(5, '0');
    }

    /// <summary>15,2 — ทศนิยม 2 ตำแหน่งเสมอ ไม่มี thousand separator</summary>
    private static string Money(decimal v) =>
        Math.Round(v, 2, MidpointRounding.AwayFromZero)
            .ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>4,2 — อัตราภาษี เช่น 3.00</summary>
    private static string Rate(decimal v) =>
        Math.Round(v, 2, MidpointRounding.AwayFromZero)
            .ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>ตัด pipe/บรรทัดใหม่ออก — ไฟล์ pipe-delimited ไม่มี quoting</summary>
    private static string Clean(string? raw) =>
        (raw ?? "").Replace("|", " ").Replace("\r", " ").Replace("\n", " ").Trim();
}
