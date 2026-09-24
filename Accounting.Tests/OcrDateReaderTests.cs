using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// <see cref="OcrDateReader"/> — ขั้นตรวจวันที่เอกสารกับกระดาษ ของ<b>ทุก engine</b>
///
/// <para>═══ ที่มา (รอบ 190 · เจ้าของข้อ 11 "ทั้ง Azure และ local ยังจับวันที่เพี้ยน") ═══
/// ใบจริงสองใบพิมพ์ปี ค.ศ. 2 หลัก: Wine Pro <c>Date: 18/09/26 6:35 PM</c> · Radisson
/// <c>วันที่ DATE 12/09/26</c> — "26" คือ ค.ศ. 2026 (ไม่ใช่ พ.ศ. 2526) และตีความได้อีก 2 แบบที่
/// "ดูใช้ได้" (26 ก.ย. 2018 · 9 ธ.ค. 2026) ซึ่ง engine ที่ใช้ locale อเมริกันเลือกได้เงียบ ๆ</para>
///
/// <para>เทสต์สองครึ่ง (CLAUDE.md §H): ครึ่งแรก = ใบที่ engine ให้ผิด/ไม่ได้ ต้องกลับมาถูก ·
/// ครึ่งหลัง = ใบที่ engine ให้ถูกอยู่แล้ว / เอกสารเก่าจริง / กระดาษไม่มีป้าย <b>ต้องไม่ถูกแตะ</b></para>
/// </summary>
public class OcrDateReaderTests
{
    /// <summary>วันอัปโหลดของใบทั้งสอง (รอบ 190)</summary>
    private static readonly DateTime Uploaded = new(2026, 9, 24, 3, 0, 0, DateTimeKind.Utc);

    private static DateTime D(int y, int m, int d) => new(y, m, d, 0, 0, 0, DateTimeKind.Utc);

    // ข้อความถอดจากกระดาษจริง (erp-review/2026-09-24/BRIEF.md) — เฉพาะส่วนหัวที่มีวันที่
    private const string WinePro =
        "Wine Pro Co.,Ltd. Branch 00012\n12/861 Moo 15 Bangkaew,\nBangplee, Samutprakarn 10540\n"
        + "Tel : 02-100-6401\nVAT Registration No.: 0105555175590\nPOS Terminal ID.: E051120003A1433\n"
        + "Receipt / Tax Invoice (Original)\nใบเสร็จรับเงิน/ใบกำกับภาษี (ต้นฉบับ)\nTax Inv No.:  BA2609-569\n"
        + "Slip: 0000000BN2000000413\nStaff: Kade\nDate: 18/09/26 6:35 PM\nCustomer Info. [CZBNG2600843]\n"
        + "หจก.แอม แฮปปี้เนส\nVAT Reg. No.: 0203562005871\nTotal                                    3,593.00\n";

    private const string Radisson =
        "854/2 ถนนบุรีรัมย์ ต.ชะอำ อ.ชะอำ จ.เพชรบุรี 76120\n"
        + "ใบเสร็จรับเงิน/ใบกำกับภาษี  OFFICIAL RECEIPT / TAX INVOICE          วันที่ DATE 12/09/26\n"
        + "เลขประจำตัวผู้เสียภาษี / TAX ID. 0105551136085\n"
        + "เล่มที่ BOOK NO. 066                                             เลขที่ SERIAL NO. 3267\n"
        + "Tel (66 32) 708 300 Fax (66 32) 708310\nค่าอาหารและเครื่องดื่ม     1,705.00\n";

    // ═════════ ครึ่งแรก: ใบที่พัง → ต้องกลับมาถูก ═════════

    [Fact]
    public void WinePro_engine_ไม่ได้วันที่_เช่น_python_ที่รู้จักแต่ปี4หลัก_ต้องเติม_18ก_ย_2026()
    {
        var r = OcrDateReader.CrossCheck(null, WinePro, Uploaded);
        Assert.Equal(OcrDateVerdict.Filled, r.Verdict);
        Assert.Equal(D(2026, 9, 18), r.Date);
        Assert.Equal(OcrDateReader.LabelledConfidence, r.Confidence);
        Assert.True(r.FromPaperLabel);
    }

    [Fact]
    public void WinePro_engine_อ่านเป็น_ปี_เดือน_วัน_2018_09_26_ต้องกลับเป็น_2026_09_18_และไฮไลต์()
    {
        var r = OcrDateReader.CrossCheck(D(2018, 9, 26), WinePro, Uploaded);
        Assert.Equal(OcrDateVerdict.Replaced, r.Verdict);
        Assert.Equal(D(2026, 9, 18), r.Date);
        Assert.True(r.Confidence < 0.85m, "ค่าที่ทับ engine ต้องขึ้นไฮไลต์เหลือง (G4)");
    }

    [Fact]
    public void Radisson_engine_อ่านแบบอเมริกัน_เดือน_วัน_ได้_9ธ_ค_ต้องกลับเป็น_12ก_ย_2026()
    {
        var r = OcrDateReader.CrossCheck(D(2026, 12, 9), Radisson, Uploaded);
        Assert.Equal(OcrDateVerdict.Replaced, r.Verdict);
        Assert.Equal(D(2026, 9, 12), r.Date);
    }

    [Fact]
    public void Radisson_engine_อ่านเป็น_2012_09_26_ต้องกลับเป็น_2026_09_12()
    {
        var r = OcrDateReader.CrossCheck(D(2012, 9, 26), Radisson, Uploaded);
        Assert.Equal(OcrDateVerdict.Replaced, r.Verdict);
        Assert.Equal(D(2026, 9, 12), r.Date);
    }

    [Fact]
    public void วันที่พิมพ์ไทยปีย่อ_และ_engine_หยิบวันครบกำหนด_ต้องใช้วันที่เอกสาร()
    {
        const string text = "ใบกำกับภาษี\nวันที่ 12 ก.ย. 69\nครบกำหนด 12 ต.ค. 69\nรวมทั้งสิ้น 1,070.00\n";
        var picked = OcrDateReader.CrossCheck(D(2026, 10, 12), text, Uploaded);
        Assert.Equal(OcrDateVerdict.Replaced, picked.Verdict);
        Assert.Equal(D(2026, 9, 12), picked.Date);

        var filled = OcrDateReader.CrossCheck(null, text, Uploaded);
        Assert.Equal(D(2026, 9, 12), filled.Date);
    }

    [Fact]
    public void ป้าย_Due_Date_มีคำว่า_Date_ข้างใน_ต้องไม่นับเป็นวันที่เอกสาร()
    {
        const string text = "INVOICE\nDue Date: 30/09/2026\nInvoice Date: 01/09/2026\n";
        var r = OcrDateReader.CrossCheck(D(2026, 9, 30), text, Uploaded);
        Assert.Equal(OcrDateVerdict.Replaced, r.Verdict);
        Assert.Equal(D(2026, 9, 1), r.Date);
    }

    [Fact]
    public void แบบฟอร์มที่ป้ายอยู่บรรทัดบน_และ_engine_สลับวันเดือน_ต้องได้แบบไทย()
    {
        const string text = "ใบเสร็จรับเงิน\nวันที่\n05/08/2569\nรวม 500.00\n";
        var r = OcrDateReader.CrossCheck(D(2026, 5, 8), text, Uploaded);
        Assert.Equal(OcrDateVerdict.Replaced, r.Verdict);
        Assert.Equal(D(2026, 8, 5), r.Date);
    }

    [Fact]
    public void engine_ให้วันที่อนาคตไกลที่ไม่อยู่บนกระดาษ_ต้องใช้ป้ายวันที่บนกระดาษ()
    {
        var r = OcrDateReader.CrossCheck(D(2027, 6, 1), WinePro, Uploaded);
        Assert.Equal(OcrDateVerdict.Replaced, r.Verdict);
        Assert.Equal(D(2026, 9, 18), r.Date);
    }

    [Fact]
    public void engine_ห่างจากวันอัปโหลดเกินช่วง_และกระดาษไม่มีวันที่_ต้องลดความมั่นใจแต่ไม่ล้าง()
    {
        var r = OcrDateReader.CrossCheck(D(2018, 1, 1), "ใบเสร็จ\nรวม 100.00\n", Uploaded);
        Assert.Equal(OcrDateVerdict.Doubtful, r.Verdict);
        Assert.Equal(D(2018, 1, 1), r.Date);   // ห้ามปัดเงียบ — คงค่าไว้ให้คนตัดสิน
        Assert.Equal(OcrDateReader.ImplausibleConfidence, r.Confidence);
    }

    // ═════════ ครึ่งหลัง: ของที่ถูกอยู่แล้ว / ไม่มีหลักฐาน → ห้ามแตะ ═════════

    [Fact]
    public void WinePro_engine_ถูกอยู่แล้ว_ต้องยืนยัน_ไม่เปลี่ยนค่า()
    {
        var r = OcrDateReader.CrossCheck(D(2026, 9, 18), WinePro, Uploaded);
        Assert.Equal(OcrDateVerdict.Confirmed, r.Verdict);
        Assert.Equal(D(2026, 9, 18), r.Date);
    }

    [Fact]
    public void Radisson_engine_ถูกอยู่แล้ว_ต้องยืนยัน_ไม่เปลี่ยนค่า()
    {
        var r = OcrDateReader.CrossCheck(D(2026, 9, 12), Radisson, Uploaded);
        Assert.Equal(OcrDateVerdict.Confirmed, r.Verdict);
        Assert.Equal(D(2026, 9, 12), r.Date);
    }

    [Fact]
    public void เอกสารเก่าหนึ่งปีที่สแกนตามเก็บ_ไม่ใช่ความผิด_ต้องไม่ถูกลดความมั่นใจ()
    {
        var r = OcrDateReader.CrossCheck(D(2025, 8, 15), "ใบกำกับภาษี\nDate: 15/08/2025\n", Uploaded);
        Assert.Equal(OcrDateVerdict.Confirmed, r.Verdict);
        Assert.Equal(D(2025, 8, 15), r.Date);
    }

    [Fact]
    public void กระดาษไม่มีวันที่เลย_engine_ให้วันที่ปกติ_ต้องไม่แตะ()
    {
        var r = OcrDateReader.CrossCheck(D(2026, 9, 1), "ใบเสร็จ\nรวม 100.00\n", Uploaded);
        Assert.Equal(OcrDateVerdict.NoChange, r.Verdict);
    }

    [Fact]
    public void วันที่บนกระดาษไม่มีป้าย_ไม่มีสิทธิ์ทับค่า_engine()
    {
        // ไม่มีป้าย = หลักฐานอ่อน ⇒ ไม่ทับ engine (engine อาจเห็นสิ่งที่ข้อความเราไม่มี)
        var r = OcrDateReader.CrossCheck(D(2026, 9, 10), "ใบเสร็จ\n12/09/2026 14:22\nรวม 100.00\n", Uploaded);
        Assert.Equal(OcrDateVerdict.NoChange, r.Verdict);
        Assert.Equal(D(2026, 9, 10), r.Date);
    }

    [Fact]
    public void มีแต่วันครบกำหนดบนกระดาษ_ห้ามหยิบมาเติมเป็นวันที่เอกสาร()
    {
        var r = OcrDateReader.CrossCheck(null, "ใบแจ้งหนี้\nครบกำหนดชำระ 30/10/2569\n", Uploaded);
        Assert.Equal(OcrDateVerdict.NoChange, r.Verdict);
        Assert.Null(r.Date);
    }

    [Fact]
    public void เบอร์โทร_ยอดเงิน_เลขที่บ้าน_ไม่ใช่วันที่()
    {
        const string text = "Tel : 02-100-6401\n12/861 Moo 15\n3,357.94   235.06   3,593.00\nTel (66 32) 708 300\n";
        Assert.Empty(OcrDateReader.FindCandidates(text, Uploaded));
    }

    [Theory]
    [InlineData("date:", OcrDateLabel.DocumentDate)]
    [InlineData("วันที่date", OcrDateLabel.DocumentDate)]
    [InlineData("invoicedate:", OcrDateLabel.DocumentDate)]
    [InlineData("duedate:", OcrDateLabel.OtherDate)]
    [InlineData("printdate", OcrDateLabel.OtherDate)]
    [InlineData("expirydate", OcrDateLabel.OtherDate)]
    [InlineData("วันที่ครบกำหนด", OcrDateLabel.OtherDate)]
    [InlineData("ถึงวันที่", OcrDateLabel.OtherDate)]
    // ชื่อ "โรงพิมพ์" อยู่ห่างก่อนป้ายวันที่ — ต้องไม่ทำให้วันที่เอกสารกลายเป็นวันพิมพ์
    [InlineData("บจก.โรงพิมพ์สยามวันที่", OcrDateLabel.DocumentDate)]
    [InlineData("staff:kade", OcrDateLabel.None)]
    public void ตัดสินป้ายหน้าวันที่(string squashedPrefix, OcrDateLabel expected)
        => Assert.Equal(expected, OcrDateReader.ClassifyPrefix(squashedPrefix));

    [Theory]
    [InlineData("ก.ย.", 9)]
    [InlineData("กย", 9)]
    [InlineData("กันยายน", 9)]
    [InlineData("Sept", 9)]
    [InlineData("Sep.", 9)]
    public void ชื่อเดือนแบบตรงทั้งคำ(string token, int month)
        => Assert.Equal(month, ThaiMonthName.TryParseExact(token));

    [Theory]
    [InlineData("market")]   // "mar" อยู่ข้างใน — TryParse แบบ Contains จะตอบ 3
    [InlineData("mayor")]
    [InlineData("bottle")]
    public void คำที่มีชื่อเดือนซ่อนอยู่ข้างใน_ไม่ใช่เดือน(string token)
        => Assert.Null(ThaiMonthName.TryParseExact(token));

    [Fact]
    public void ปีสองหลัก_26_คือ_ค_ศ_2026_ส่วน_69_คือ_พ_ศ_2569()
    {
        var c = OcrDateReader.FindCandidates("Date: 18/09/26\nวันที่ 12/09/69\n", Uploaded);
        Assert.Equal(D(2026, 9, 18), c[0].Date);
        Assert.Equal(D(2026, 9, 12), c[1].Date);
    }

    // ── ด่านอนุมัติอัตโนมัติ (ฝ่ายค้านรอบ 190) — วันที่ที่ระบบเดา/ทับ ต้องให้คนยืนยันก่อน ──

    [Fact]
    public void ด่านอนุมัติ_วันที่ที่ทับ_engine_หรือเติมให้_ต้องให้คนยืนยัน_และแท็กบล็อกการอนุมัติเอง()
    {
        var replaced = OcrDateReader.CrossCheck(D(2018, 9, 26), WinePro, Uploaded);
        var filled = OcrDateReader.CrossCheck(null, WinePro, Uploaded);
        Assert.True(OcrDateReader.NeedsHumanConfirm(replaced));
        Assert.True(OcrDateReader.NeedsHumanConfirm(filled));
        var notes = "\n" + OcrPostingReadiness.DateUnsureTag + " " + replaced.Reason;
        Assert.False(OcrPostingReadiness.Evaluate(notes, hasUsableDate: true).CanAutoApprove);
    }

    [Fact]
    public void ด่านอนุมัติ_ทิศตรงข้าม_engine_อ่านตรงกับป้าย_ไม่ต้องให้คนยืนยัน_อนุมัติเองได้เหมือนเดิม()
    {
        var confirmed = OcrDateReader.CrossCheck(D(2026, 9, 18), WinePro, Uploaded);
        Assert.Equal(OcrDateVerdict.Confirmed, confirmed.Verdict);
        Assert.False(OcrDateReader.NeedsHumanConfirm(confirmed));
        Assert.False(OcrDateReader.NeedsHumanConfirm(
            new OcrDateCheck(OcrDateVerdict.NoChange, D(2026, 9, 18), 0.95m, "")));
        Assert.True(OcrPostingReadiness.Evaluate("", hasUsableDate: true).CanAutoApprove);
    }

    // ═════════ รอบ 193 · คำตัดสินเจ้าของข้อ 22 — วันที่กำกวมบนใบอังกฤษล้วน + สกุลเงินต่างประเทศ ═════════
    // "05/08/2026" อ่านได้ทั้ง 5 ส.ค. (ไทย) และ 8 พ.ค. (อเมริกัน) — ทั้งคู่อยู่ในช่วงเทียบวันอัปโหลด 24 ก.ย. 2026

    private const string AwsLike =
        "Amazon Web Services, Inc.\nTax Invoice\nInvoice Number: 1234567890\nInvoice Date: 05/08/2026\n"
        + "Bill to: Example Co., Ltd.\nTotal amount due USD 12.34\n";

    [Fact]
    public void ใบอังกฤษล้วน_สกุลUSD_engine_อ่านเดือนก่อนวัน_ต้องเชื่อengine()
    {
        Assert.True(OcrDateReader.IsForeignEnglishPaper(AwsLike));
        var r = OcrDateReader.CrossCheck(D(2026, 5, 8), AwsLike, Uploaded);
        Assert.Equal(OcrDateVerdict.Confirmed, r.Verdict);   // ⬅ เดิม: Replaced เป็น 2026-08-05
        Assert.Equal(D(2026, 5, 8), r.Date);
        Assert.False(OcrDateReader.NeedsHumanConfirm(r));
    }

    [Fact]
    public void ใบอังกฤษล้วน_สกุลUSD_engine_อ่านแบบไทยอยู่แล้ว_ยังยืนยันตามเดิม()
    {
        var r = OcrDateReader.CrossCheck(D(2026, 8, 5), AwsLike, Uploaded);
        Assert.Equal(OcrDateVerdict.Confirmed, r.Verdict);
        Assert.Equal(D(2026, 8, 5), r.Date);
    }

    [Fact]
    public void ใบอังกฤษล้วน_สกุลUSD_engineไม่ได้วันที่_ยังเติมแบบไทยก่อน()
    {
        // คำตัดสินให้ "เชื่อ engine" — ไม่มีค่าของ engine ก็ไม่มีอะไรให้เชื่อ ⇒ กติกาเดิม
        var r = OcrDateReader.CrossCheck(null, AwsLike, Uploaded);
        Assert.Equal(OcrDateVerdict.Filled, r.Verdict);
        Assert.Equal(D(2026, 8, 5), r.Date);
    }

    [Fact]
    public void ใบอังกฤษล้วน_แต่เป็นเงินบาท_ยังแบบไทยก่อน()
    {
        var thb = AwsLike.Replace("USD 12.34", "12.34");
        Assert.False(OcrDateReader.IsForeignEnglishPaper(thb));
        var r = OcrDateReader.CrossCheck(D(2026, 5, 8), thb, Uploaded);
        Assert.Equal(OcrDateVerdict.Replaced, r.Verdict);
        Assert.Equal(D(2026, 8, 5), r.Date);
    }

    [Fact]
    public void ใบมีภาษาไทยปน_แม้เป็นUSD_ยังแบบไทยก่อน()
    {
        var bilingual = AwsLike.Replace("Tax Invoice", "Tax Invoice / ใบกำกับภาษี");
        Assert.False(OcrDateReader.IsForeignEnglishPaper(bilingual));
        var r = OcrDateReader.CrossCheck(D(2026, 5, 8), bilingual, Uploaded);
        Assert.Equal(OcrDateVerdict.Replaced, r.Verdict);
        Assert.Equal(D(2026, 8, 5), r.Date);
    }

    [Fact]
    public void ใบอังกฤษล้วน_USD_แต่engineให้ปีผิดช่วง_ยังซ่อมจากกระดาษตามเดิม()
    {
        // engine อ่าน "18/09/26" เป็น 2018-09-26 (ห่างวันอัปโหลดเกิน 2 ปี) — ข้อยกเว้นใช้เฉพาะเมื่อ engine สมเหตุสมผล
        const string text = "Invoice\nDate: 18/09/26\nTotal USD 99.00\n";
        var r = OcrDateReader.CrossCheck(D(2018, 9, 26), text, Uploaded);
        Assert.Equal(OcrDateVerdict.Replaced, r.Verdict);
        Assert.Equal(D(2026, 9, 18), r.Date);
    }
}
