using Accounting.Helpers;
using Xunit;
using K = Accounting.Helpers.OcrSlipText.PartyKind;
using C = Accounting.Tests.SlipCorpusRound264;

namespace Accounting.Tests;

/// <summary>
/// รอบ 264 แพ็ก P2 (คำตัดสิน 264-4-3) — คู่กรณีบนสลิปจาก<b>ข้อความ OCR จริง</b> 10 ใบ (<see cref="SlipCorpusRound264"/> · กฎ #4 §H) ·
/// ล็อกทั้ง "อ่านได้ถูก" และ "อ่านไม่ได้ = null" · บทบาทต้องมีป้ายคู่หรือ QR (ไม่เดาจากลำดับ) · ผู้เรียกเดิม (ไม่มี QR) ได้ยอด/วันที่/เลขอ้างอิงเท่าเดิม (260 L7)
/// </summary>
public class OcrSlipTextPartiesTests
{
    private static OcrSlipText.Reading Read(C.Slip s, bool withQr = true) => OcrSlipText.Parse(s.Text, 2026, withQr ? s.Qr : null);

    // ═══ ฝั่งที่อ่านได้ (ป้าย จาก/ไปยัง · From/To) ═══

    [Fact]
    public void กรุงไทย_ไป_กสิกร_ป้ายไทย_ได้สองฝั่งพร้อมธนาคารและเลขปิดบาง()
    {
        var r = Read(C.KtbToKbank);
        Assert.Equal(OcrSlipText.RoleSource.Label, r.Roles);
        Assert.Equal((K.BankAccount, "006", "xxx-x-xx319-7"), (r.Payer!.Kind, r.Payer.BankCode, r.Payer.AccountMask));
        Assert.False(r.Payer.BankFromQr);   // ข้อความตรง QR (006) — ไม่ใช่ธนาคารจาก QR
        Assert.Equal("3197", r.Payer.VisibleText);
        Assert.Equal((K.BankAccount, "004", "xxx-x-xx168-1"), (r.Payee!.Kind, r.Payee.BankCode, r.Payee.AccountMask));
        Assert.Equal(10000.00m, r.Amount);
        Assert.Equal(new DateTime(2026, 11, 12, 0, 0, 0, DateTimeKind.Utc), r.Date);
    }

    [Fact]
    public void กรุงไทย_ไป_กรุงเทพ()
    {
        var r = Read(C.KtbToBbl);
        Assert.Equal(("006", "xxx-x-xx319-7"), (r.Payer!.BankCode, r.Payer.AccountMask));
        Assert.Equal(("002", "xxx-x-xx598-9"), (r.Payee!.BankCode, r.Payee.AccountMask));
        Assert.Equal(8371.00m, r.Amount);
        Assert.Equal(new DateTime(2026, 11, 8, 0, 0, 0, DateTimeKind.Utc), r.Date);
    }

    [Fact]
    public void KBIZ_อังกฤษ_From_To_บริษัทไปบุคคล_และ_Memo()
    {
        var r = Read(C.KbizTransferMemo);
        Assert.Equal(OcrSlipText.RoleSource.Label, r.Roles);
        Assert.Equal((K.BankAccount, "004", "xxx-x-x0621-x"), (r.Payer!.Kind, r.Payer.BankCode, r.Payer.AccountMask));
        Assert.Equal("0621", r.Payer.VisibleText);
        Assert.Equal((K.BankAccount, "004", "xxx-x-x8844-x"), (r.Payee!.Kind, r.Payee.BankCode, r.Payee.AccountMask));
        Assert.Equal(6305.00m, r.Amount);
        Assert.Equal(new DateTime(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc), r.Date);   // "2 Nov 26"
        Assert.Equal("<MEMO>", r.Memo);
        Assert.Null(r.Qr);
    }

    [Fact]
    public void KBIZ_จ่ายบิล_สปส_ผู้รับเป็น_Biller_ไม่ใช่บัญชี()
    {
        var r = Read(C.KbizBillSso);
        Assert.Equal((K.BankAccount, "004"), (r.Payer!.Kind, r.Payer.BankCode));
        Assert.Equal("0621", r.Payer.VisibleText);   // OCR "XXxX-X-X0621-x" (กลุ่มแรก 4 ตัว) — ยังนับจากขวาตรง
        Assert.Equal((K.Biller, "099400015967620"), (r.Payee!.Kind, r.Payee.BillerId));
        Assert.Null(r.Payee.AccountMask);
        Assert.Equal(8558.00m, r.Amount);
    }

    [Fact]
    public void SCB_ธนาคารผู้โอนมีแต่โลโก้_ได้จาก_QR_และจ่ายบิลได้_Biller()
    {
        var r = Read(C.ScbBillStore);
        Assert.Equal((K.BankAccount, "014", true, "xxx-xxx189-5"), (r.Payer!.Kind, r.Payer.BankCode, r.Payer.BankFromQr, r.Payer.AccountMask));   // "XXX-XXX1 89-5" ตัดช่องว่าง
        Assert.Equal((K.Biller, "010753600031501"), (r.Payee!.Kind, r.Payee.BillerId));
        Assert.Equal(49.00m, r.Amount);
    }

    [Fact]
    public void SCB_OCR_อ่านตัวปิดเป็นตัวเลข_ไม่เชื่อเลขปิดบาง_แต่ยังรู้ธนาคารจาก_QR()
    {
        var r = Read(C.ScbBillRefs);   // ผู้โอน "2%%%<-%%%189-5" — '2' ข้างหน้าคือตัวปิดที่อ่านผิด
        Assert.Equal((K.Unknown, "014", (string?)null), (r.Payer!.Kind, r.Payer.BankCode, r.Payer.AccountMask));
        Assert.Equal((K.Biller, "010753600037401"), (r.Payee!.Kind, r.Payee.BillerId));
        Assert.Equal(161.50m, r.Amount);
        var p = Read(C.ScbPromptPayMobile);   // "2%60%<-%%<6<189-5"
        Assert.Null(p.Payer!.AccountMask);
    }

    [Fact]
    public void SCB_พร้อมเพย์ไปเบอร์_ผู้รับเป็นพร้อมเพย์_ไม่ใช่บัญชี()
    {
        var r = Read(C.ScbPromptPayMobile);
        Assert.Equal((K.PromptPay, (string?)null, "xxx-xxxx-0035"), (r.Payee!.Kind, r.Payee.BankCode, r.Payee.AccountMask));
        Assert.Equal(120.00m, r.Amount);
        Assert.Equal(new DateTime(2026, 11, 9, 0, 0, 0, DateTimeKind.Utc), r.Date);
    }

    // ═══ ฝั่งที่ต้อง "ไม่รู้" (ไม่มีป้าย + QR ตัดสินไม่ได้ / ไม่มี QR) ═══

    [Fact]
    public void Kplus_โอนในธนาคารเดียวกัน_ไม่มีป้าย_QR_ตรงทั้งสองบล็อก_ไม่กำหนดบทบาท()
    {
        var r = Read(C.KplusSameBank);
        Assert.Equal("004", r.Qr!.SendingBankCode);
        Assert.Null(r.Payer);
        Assert.Null(r.Payee);
        Assert.Equal(OcrSlipText.RoleSource.None, r.Roles);
        Assert.Equal(800.00m, r.Amount);
        Assert.Null(r.Date);   // OCR อ่านวันที่เพี้ยน ("~ Qwel. 69") ⇒ ไม่เดา
    }

    [Theory]
    [InlineData(nameof(C.TtbCardEnglish))]
    [InlineData(nameof(C.KplusCardBill))]
    public void สลิปบัตรที่ไม่มีป้ายและไม่มี_QR_ไม่กำหนดบทบาท(string slip)
    {
        var r = Read(Slip(slip));
        Assert.Null(r.Payer);
        Assert.Null(r.Payee);
        Assert.Equal(OcrSlipText.RoleSource.None, r.Roles);
    }

    [Fact]
    public void ttb_อังกฤษ_ยอดมีจุลภาค_วันที่_Nov()
    {
        var r = Read(C.TtbCardEnglish);
        Assert.Equal(32974.40m, r.Amount);
        Assert.Equal(new DateTime(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc), r.Date);   // "2 Nov 26, 4:53 PM"
        Assert.True(r.LooksLikeSlip);
    }

    [Fact]
    public void Kplus_จ่ายบิล_OCR_อ่านค่าธรรมเนียมเป็น_660_คำว่า_จํานวน_ชี้ยอดจริง()
    {
        // เดิม: "จํานวน" (ํ+า) ไม่ตรง "จำนวน" ⇒ ยอดทั้งสลิป {159, 660} ⇒ ไม่มั่นใจ · รอบนี้ปรับตัวอักษรก่อนหาคำนำ ⇒ 159.00 (ใบที่พังกลับมาถูก)
        var r = Read(C.KplusCardBill);
        Assert.Equal(new[] { 159.00m, 660.00m }, r.Amounts);
        Assert.Equal(159.00m, r.Amount);
        Assert.Equal(new DateTime(2026, 11, 12, 0, 0, 0, DateTimeKind.Utc), r.Date);   // "12 พ.ย. 69"
    }

    // ═══ QR ตัดสินบทบาทเมื่อไม่มีป้าย (สลิปจริงที่ตัดป้ายออก) · QR ขัดข้อความ ⇒ ไม่เลือกข้าง ═══

    [Fact]
    public void ไม่มีป้าย_แต่_QR_ตรงบล็อกเดียว_บล็อกนั้นคือผู้โอน()
    {
        var noLabel = C.KtbToKbank.Text.Replace("๐   อาก", "").Replace("©   ไปยัง", "");
        var r = OcrSlipText.Parse(noLabel, 2026, C.KtbToKbank.Qr);
        Assert.Equal(OcrSlipText.RoleSource.Qr, r.Roles);
        Assert.Equal(("006", "xxx-x-xx319-7"), (r.Payer!.BankCode, r.Payer.AccountMask));
        Assert.Equal(("004", "xxx-x-xx168-1"), (r.Payee!.BankCode, r.Payee.AccountMask));
        // ทิศตรงข้าม: ไม่มีป้าย + ไม่มี QR ⇒ ไม่เดาจากลำดับบน-ล่าง
        var none = OcrSlipText.Parse(noLabel, 2026);
        Assert.Null(none.Payer);
        Assert.Null(none.Payee);
    }

    [Fact]
    public void ธนาคารผู้โอนบนข้อความขัด_QR_ธนาคารผู้โอน_null()
    {
        // QR ของ SCB (014) บนสลิปที่ข้อความบอกผู้โอนเป็นกรุงไทย ⇒ ไม่เลือกข้าง
        var r = OcrSlipText.Parse(C.KtbToKbank.Text, 2026, C.ScbBillStore.Qr);
        Assert.Null(r.Payer!.BankCode);
        Assert.Equal("004", r.Payee!.BankCode);
    }

    // ═══ ผู้เรียกเดิม (ไม่ส่ง QR) — ยอด/เลขอ้างอิง/วันที่เท่าเดิม · มี QR ⇒ เลขอ้างอิงจาก QR ═══

    [Theory]
    [InlineData(nameof(C.KtbToBbl), "79024680135", "Aba79024680135e25")]                  // OCR ตัดตัวพิมพ์เล็กทิ้ง ⇒ เลขครึ่งเดียว
    [InlineData(nameof(C.ScbPromptPayMobile), "2026110951", "202790246jQb35TfUhiPQWcRw")]
    [InlineData(nameof(C.ScbBillStore), "800468000002036504", "202790246jQb3RTYU2OP8WcLg")]   // OCR หยิบรหัสธุรกรรมของร้านแทน
    [InlineData(nameof(C.ScbBillRefs), "4717902468QWER790200", "202790246j0WcRe9gIiPa0E0Z")]  // OCR หยิบ Ref2 แทน
    [InlineData(nameof(C.KplusSameBank), "016790246801ERTY0209", "016790246801ERT90209")]
    public void เลขอ้างอิง_ไม่มี_QR_เท่าเดิม_มี_QR_ใช้ของ_QR(string slip, string textOnly, string fromQr)
    {
        var s = Slip(slip);
        Assert.Equal(textOnly, Read(s, withQr: false).Ref);
        Assert.Equal(fromQr, Read(s).Ref);
    }

    [Fact]
    public void กรุงไทยที่_OCR_อ่านคำว่าสำเร็จเพี้ยน_ไม่มี_QR_ไม่ดูเป็นสลิป_มี_QR_ดูเป็นสลิป()
    {
        var textOnly = Read(C.KtbToKbank, withQr: false);
        Assert.Null(textOnly.Ref);
        Assert.False(textOnly.LooksLikeSlip);   // เท่าเดิม (260-1-7: ลูกค้าต้องกดยืนยัน)
        Assert.True(Read(C.KtbToKbank).LooksLikeSlip);
    }

    [Fact]
    public void ยอดที่มั่นใจของทุกใบในชุดกระดาษจริง_ล็อกไว้()
    {
        var expected = new Dictionary<string, decimal>
        {
            ["ba71c6a6"] = 10000.00m, ["e00782b6"] = 159.00m, ["9dee166e"] = 49.00m, ["c9386b2a"] = 161.50m, ["6741f5ef"] = 120.00m,
            ["e1a1cd16"] = 800.00m, ["1abb4097"] = 8371.00m, ["8c972d25"] = 32974.40m, ["1e871a3e"] = 6305.00m, ["2e8d4452"] = 8558.00m,
        };
        foreach (var s in C.All)
        {
            Assert.Equal(expected[s.Key], Read(s, withQr: false).Amount);
            Assert.Equal(expected[s.Key], Read(s).Amount);   // QR ไม่แตะยอด
        }
    }

    [Fact]
    public void QR_อย่างเดียวไม่มีข้อความ_ได้เลขอ้างอิงแต่ไม่มียอด()
    {
        var r = OcrSlipText.Parse(null, 2026, C.KtbToBbl.Qr);
        Assert.False(r.HasAnyAmount);
        Assert.Equal("Aba79024680135e25", r.Ref);
        Assert.Same(OcrSlipText.Reading.Empty, OcrSlipText.Parse(null, 2026, "not-a-qr"));
    }

    // ═══ เลขปิดบาง ═══

    [Theory]
    [InlineData("XXX-X-XX319-7", K.BankAccount, "xxx-x-xx319-7")]
    [InlineData("xxx-x-x2577-x", K.BankAccount, "xxx-x-x2577-x")]
    [InlineData("xxx-xxx189-5", K.BankAccount, "xxx-xxx189-5")]
    [InlineData("XXX-X-KX395-2", K.BankAccount, "xxx-x-xx395-2")]   // OCR อ่าน X เป็น K
    [InlineData("%%%<-%-%2577-%", K.BankAccount, "xxxx-x-x2577-x")]
    [InlineData("xxx-xxx-0035", K.PromptPay, "xxx-xxx-0035")]
    [InlineData("4000-00xx-xxxx-1154", K.Card, "4000-00xx-xxxx-1154")]
    [InlineData("2%60%<-%%<6<189-5", K.Unknown, null)]
    [InlineData("XXX-X-X8177-xX", K.Unknown, null)]                 // กลุ่มท้าย 2 ตัว (OCR) ⇒ ไม่เชื่อ
    [InlineData("12 พ.ย. 2569", K.Unknown, null)]
    public void ClassifyMask(string token, K kind, string? mask)
        => Assert.Equal((kind, mask), OcrSlipText.ClassifyMask(token));

    [Fact]
    public void ผลอ่านที่เก็บลงฐาน_ไม่มีชื่อ_อ่านกลับได้_และรูปผิดได้_null()
    {
        var r = Read(C.KtbToKbank);
        var json = OcrSlipText.ToStoredJson(r)!;
        Assert.DoesNotContain("PAYER_NAME", json);
        Assert.DoesNotContain("PAYEE_NAME", json);
        var back = OcrSlipText.FromStoredJson(json)!;
        Assert.Equal(("006", "A31790g468abc5efe", OcrSlipText.RoleSource.Label), (back.QrBank, back.QrRef, back.Roles));
        Assert.Equal(("004", "xxx-x-xx168-1", K.BankAccount), (back.Payee!.BankCode, back.Payee.AccountMask, back.Payee.Kind));
        Assert.Null(back.Payee.Name);
        Assert.Null(OcrSlipText.FromStoredJson("{oops"));
        Assert.Null(OcrSlipText.FromStoredJson("""{"v":2}"""));
        Assert.Null(OcrSlipText.ToStoredJson(OcrSlipText.Parse("สวัสดีค่ะ 100.00")));   // ไม่มี QR และไม่มีคู่กรณี ⇒ ไม่เก็บ
        // mask ที่ถูกแก้ในฐานจนจัดชนิดไม่ตรง ⇒ ทิ้ง (ไม่ใช่หลักฐาน)
        var tampered = json.Replace("xxx-x-xx168-1", "1234567890");
        Assert.Null(OcrSlipText.FromStoredJson(tampered)!.Payee!.AccountMask);
    }

    private static C.Slip Slip(string name) => (C.Slip)typeof(C).GetField(name)!.GetValue(null)!;
}
