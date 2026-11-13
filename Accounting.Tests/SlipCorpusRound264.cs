namespace Accounting.Tests;

/// <summary>
/// รอบ 264 แพ็ก P2 — <b>ชุดกระดาษจริง</b> สลิป 10 ใบที่เจ้าของส่งมา (2026-11-13) · ข้อความ = ผล Tesseract 5.3.4 tha+eng (PSM 3 · preserve_interword_spaces · 300 dpi ·
/// สีเทา + ขยายกว้าง 1500 px เท่า <c>EmbeddedTesseractOcrService</c>) <b>ตามที่อ่านได้จริง รวมตัวเพี้ยน</b> · QR = ผลถอดด้วย ZXing/OpenCV ในเครื่อง
/// <para>PDPA: ชื่อคน/บริษัท/ร้าน ⇒ <c>&lt;PAYER_NAME&gt;</c>/<c>&lt;PAYEE_NAME&gt;</c> · บันทึกช่วยจำ ⇒ <c>&lt;MEMO&gt;</c> · เลขอ้างอิงรายการ (ทั้งข้อความและใน QR) ถูกแทนกลางด้วยตัวอักษรชนิดเดิม
/// แล้วคำนวณ CRC ของ QR ใหม่ (กันการนำไปตรวจกับธนาคาร) · BIN บัตร ⇒ 4000-00 · <b>คงไว้</b>: ชื่อธนาคาร · เลขบัญชีที่ธนาคารปิดมาแล้ว · ยอด · วันที่ · Biller ID ·
/// <b>ไม่ commit รูป</b> · ฝ่ายค้าน 264-P2 L6: Ref1 ของบิล · เลขท้ายบัตร · MID/TID ร้าน ถูกแทนด้วยค่าแทน (790246…/9999) ด้วย</para>
/// </summary>
public static class SlipCorpusRound264
{
    public sealed record Slip(string Key, string Layout, string Text, string? Qr);

    /// <summary>กรุงไทย → กรุงเทพ 8,371.00 (ป้าย จาก/ไปยัง · QR 006)</summary>
    public static readonly Slip KtbToBbl = new("1abb4097", "กรุงไทย → กรุงเทพ 8,371.00 (ป้าย จาก/ไปยัง · QR 006)",
        """
        ง๕ วัฮี
        ๕ “Wy
        โอนเงินสําเร็อ
        รหัสอฮ้างอิง Aba79024680135e25
        ๐   อจาก
        <PAYER_NAME>
        กรุงไทย
        XXX-X-XX319-7
        ©  ไปยัง
        <PAYEE_NAME>
        กรุงเทพ
        XXX-X-XX598-9
        จํานวนเงิน                          8,371.00 บาท
        ค่าธรรมเนียม                            0.00 บาท
        นที่ทํารายการ                08 พ.ย. 2569 - 17:55
        """,
        "0038000600000101030060217Aba79024680135e255102TH9104EEF2");

    /// <summary>KBIZ โอนบริษัท → บุคคล 6,305.00 อังกฤษ + Memo (ป้าย From/To · ไม่มี QR)</summary>
    public static readonly Slip KbizTransferMemo = new("1e871a3e", "KBIZ โอนบริษัท → บุคคล 6,305.00 อังกฤษ + Memo (ป้าย From/To · ไม่มี QR)",
        """
        Transfer Completed
        2 Nov 26 11:44           KBIZ
        From
             <PAYER_NAME>
        Kasikornbank
        XXX-X-X0621-x
        To
             <PAYEE_NAME>
        Kasikornbank
        XXX-X-X8844-x
        Amount
        6,305.00 Baht
        Fee
        0.00 Baht
        Received Date 2 Nov 26
        (Transfer money to the account immediately with effect)
        Transaction No. TRBR902468013579018
        Memo <MEMO>
        """,
        null);

    /// <summary>KBIZ จ่ายบิล สปส. Biller ID 8,558.00 อังกฤษ (ป้าย From/To · ไม่มี QR)</summary>
    public static readonly Slip KbizBillSso = new("2e8d4452", "KBIZ จ่ายบิล สปส. Biller ID 8,558.00 อังกฤษ (ป้าย From/To · ไม่มี QR)",
        """
        Bill Payment
        Completed                  K BIZ
        2 Nov 26 11:04
        From
        (¥)     <PAYER_NAME>
        Kasikornbank
        XXxX-X-X0621-x
        To
        ชําระเงินบสมทบมาตรา 33 ava.
        Biller ID: 099400015967620
        Amount
        8,558.00 Baht
        Fee
        0.00 Baht
        Reference 2 2667902426
        Reference No.2 20261102T2359
        Transaction No. BILR902468013579054
        """,
        null);

    /// <summary>SCB พร้อมเพย์ไปเบอร์ 120.00 (QR 014 · ผู้รับ xxx-xxx-0035)</summary>
    public static readonly Slip ScbPromptPayMobile = new("6741f5ef", "SCB พร้อมเพย์ไปเบอร์ 120.00 (QR 014 · ผู้รับ xxx-xxx-0035)",
        """
        @ โอนเงินสําเร็จ
        09 พ.ย. 2569 - 18:55
        รหัสอ้างอิง: 2026110951ป117!กเฮยน!งงเจิพ
        จาก                                    @ <PAYER_NAME>
        2%60%<-%%<6<189-5
        ไปยัง                           1) <PAYEE_NAME>
        XXX-XxXx-0035
        จํานวนเงิน                                                120.00
        ผู้รับเงินสามารถสแกนคิวอาร์โค้ดนี้เพื่อ
        ตรวจสอบสถานะการโอนเงิน
        """,
        "0046000600000101030140225202790246jQb35TfUhiPQWcRw5102TH91049734");

    /// <summary>ttb จ่ายบัตรเครดิต 32,974.40 อังกฤษ (ไม่มีป้าย · ไม่มี QR)</summary>
    public static readonly Slip TtbCardEnglish = new("8c972d25", "ttb จ่ายบัตรเครดิต 32,974.40 อังกฤษ (ไม่มีป้าย · ไม่มี QR)",
        """
        Lib
        Y)
        Payment successful
        2 Nov 26, 4:53 PM
        32,974.40
        Fee 0.00
        <PAYER_NAME>
        XXX-X-KX395-2
        ttb
        ttb credit card
        (9999)
        Lib
        Details
        Card Number                4000-00XX-XXXX-999
        9
        Reference no.: 261790246801357968
        K
        i
        | [| | | | |
        IN
        กณิแะ
        ย
        7
        """,
        null);

    /// <summary>SCB จ่ายบิล Biller ID + รหัสร้านค้า 49.00 (QR 014 · ธนาคารผู้โอนมีแต่โลโก้)</summary>
    public static readonly Slip ScbBillStore = new("9dee166e", "SCB จ่ายบิล Biller ID + รหัสร้านค้า 49.00 (QR 014 · ธนาคารผู้โอนมีแต่โลโก้)",
        """
        @ ง่ายบิลสําเร็จ
        12 พ.ย. 2569 - 13:53
        รหัสอ้างอิง: 202790246jQbc5TYUI4PQ0EdLg
        จาก                                    @ <PAYER_NAME>
        XXX-XXX1 89-5
        ไปยัง  <PAYEE_NAME>
        Biller ID : 010753600031501
        รหัสร้านค้า : KBO00002036504
        รหัสธุรกรรม : (800468000002036504
        จํานวนเงิน                                                           49.00
        ผู้รับเงินสามารถสแกนคิวอาร์โค้ดนี้เพื่อ
        ตรวจสอบสถานะการจ่ายเงิน
        """,
        "0046000600000101030140225202790246jQb3RTYU2OP8WcLg5102TH9104ABE2");

    /// <summary>กรุงไทย → กสิกรไทย 10,000.00 (ป้าย จาก/ไปยัง · QR 006)</summary>
    public static readonly Slip KtbToKbank = new("ba71c6a6", "กรุงไทย → กสิกรไทย 10,000.00 (ป้าย จาก/ไปยัง · QR 006)",
        """
        |
        =
        Was
        ย -) 9
        โอนเงินสําเรือ
        รหัสอฮ้างอิง A31790g468abcRefe
        ๐   อาก
        <PAYER_NAME>
        กรุงไทย
        XXX-X-XX319-7
        ©   ไปยัง
        <PAYEE_NAME>
        กสิกรไทย
        XXX-X-XX168-1
        จํานวนเงิน                     10,000.00 บาท
        ค่าธรรมเนียม                            0.00 บาท
        นที่ทํารายการ                12 พ.ย. 2569 - 16:41
        """,
        "0038000600000101030060217A31790g468abc5efe5102TH91045885");

    /// <summary>SCB จ่ายบิล Ref1/2/3 161.50 (QR 014 · OCR อ่านตัวปิดของผู้โอนเป็นเลข)</summary>
    public static readonly Slip ScbBillRefs = new("c9386b2a", "SCB จ่ายบิล Ref1/2/3 161.50 (QR 014 · OCR อ่านตัวปิดของผู้โอนเป็นเลข)",
        """
        @ ง่ายบิลสําเร็จ
        11 พ.ย. 2569 - 16:38
        รหัสอ้างอิง: 202611111ง9ถึง!00ง!ปไส่เฟ๒4502
        จาก                                    @ <PAYER_NAME>
        2%%%<-%%%189-5
        ไปยัง  <PAYEE_NAME>
        CO.,LTD
        Biller ID : 010753600037401
        หมายเลขร้านค้า1 : 000790246801354
        เลขที่อ้างอิง2 : 4717902468QWER790200
        เลขที่อ้างอิง 3 : 47179070
        จํานวนเงิน                                               161.50
        ผู้รับเงินสามารถสแกนคิวอาร์โค้ดน่เพือ
        ตรวจสอบสถานะการจ่ายเงิน
        """,
        "0046000600000101030140225202790246j0WcRe9gIiPa0E0Z5102TH91043181");

    /// <summary>K+ จ่ายบิลด้วยบัตรเครดิต 159.00 (MID/TID · ไม่มีป้าย · ไม่มี QR · OCR อ่านค่าธรรมเนียม 0.00 เป็น 660.00)</summary>
    public static readonly Slip KplusCardBill = new("e00782b6", "K+ จ่ายบิลด้วยบัตรเครดิต 159.00 (MID/TID · ไม่มีป้าย · ไม่มี QR · OCR อ่านค่าธรรมเนียม 0.00 เป็น 660.00)",
        """
        จ่ายบิลสําเร็จ
        ๑                    K+
        12 พ.ย. 69 13:32 น.
        ย ฮี บัตรเดอะแพสชัน
        <PAYER_NAME>
        ง/ 4000-00%*-%๐๓%-9999
           <PAYEE_NAME>
        MID: 4790246801357975
        TID: 62790246
        จํานวน:                        แป
        159.00 บาท
        ค่าธรรมเนียม:           | ee  :
        |   |                660.00 บาท
        .เลยที่รายการ: :
        016790246801357924 :
        """,
        null);

    /// <summary>K+ โอนกสิกร → กสิกร 800.00 (ไม่มีป้าย · QR 004 ตรงทั้งสองบล็อก)</summary>
    public static readonly Slip KplusSameBank = new("e1a1cd16", "K+ โอนกสิกร → กสิกร 800.00 (ไม่มีป้าย · QR 004 ตรงทั้งสองบล็อก)",
        """
        ๕ โอนเงินสําเร็จ            1<
        ~ Qwel. 69 23:27 น.                  ไม้
          <PAYER_NAME>
        ธ.กสิกรไทย
        ง    %%%<-%-%2577-%
          <PAYEE_NAME>
        ธ.กสิกรไทย
        XXX-X-X817 7-xX
        จํานวน:
        800.00 บาท
        ค่าธรรมเนียม:
        0.00 บาท
        เลขที่รายการ:
        016790246801ERTY0209 ตรวจสอบสลิป
        """,
        "0041000600000101030040220016790246801ERT902095102TH91049A55");

    public static IReadOnlyList<Slip> All { get; } = new[] { KtbToBbl, KbizTransferMemo, KbizBillSso, ScbPromptPayMobile, TtbCardEnglish, ScbBillStore, KtbToKbank, ScbBillRefs, KplusCardBill, KplusSameBank };
}
