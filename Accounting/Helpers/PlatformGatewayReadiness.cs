namespace Accounting.Helpers;

/// <summary>ข้อเท็จจริงของ "ความพร้อมเชื่อมโยงข้อมูล" ของช่องทางรับชำระ NextAcc (แยกจาก DB ⇒ เทสต์ได้)</summary>
public sealed record PlatformGatewayReadinessFacts(
    string? SystemBaseUrl,
    bool ClearingAccountFound,
    bool FeeExpenseAccountFound,
    string RevenueAccountCode,
    bool RevenueAccountFound,
    string? CashAccountCode,
    bool CashAccountFound,
    bool VatRegistered,
    bool SellerTaxIdValid,
    bool BankTransferInstructionsSet,
    int PayableMethodCount,
    // ฝ่ายค้านรอบ 227 S2: ค่าตั้ง "ราคารวม VAT แล้ว" (SiteSettings.PlatformPriceIncludesVat) — ค่าตั้งต้น true
    bool PriceIncludesVat = true);

/// <summary>หนึ่งข้อของรายการตรวจ — <c>Blocking</c> = ด่านเปิดรับเงินจริงใช้ข้อนี้ด้วย (ข้ออื่นเป็นคำเตือน)</summary>
public sealed record PlatformGatewayReadinessItem(string Key, bool Ok, bool Blocking, string Text);

/// <summary>
/// รอบ 227 (PLATFORM_OMISE_AUDIT C-1..C-5) — รายการตรวจ "เงินออนไลน์ค่าบริการไหลถึงบัญชีของ NextAcc ครบไหม" บนหน้าแอดมินช่องทางรับชำระ · pure ·
/// เซิร์ฟเวอร์ตัดสินทุกข้อ (หน้าเว็บแสดงอย่างเดียว)
/// <para><b>ที่มา:</b> เงินผ่าน Omise ต้องลง <c>Dr 11340 ลูกหนี้ผู้ให้บริการรับชำระเงิน</c> (ผู้ให้บริการถือเงิน T+n แล้วหักค่าธรรมเนียม) แล้วล้างตอนบันทึกรอบโอน
/// (<c>GatewaySettlementService</c> ในบริษัทผู้ให้บริการ) · ไม่มีผังนี้ ⇒ ระบบลงธนาคารทั้งที่เงินยังไม่เข้า (กระทบยอดพังเงียบ) · ผังรายได้/เงินสด/VAT ผู้ขาย
/// ตั้งที่หน้าตั้งค่าเว็บไซต์ — หน้าช่องทางรับชำระเดิมไม่บอกเลยว่าข้อใดขาด</para>
/// </summary>
public static class PlatformGatewayReadiness
{
    public static List<PlatformGatewayReadinessItem> Checks(PlatformGatewayReadinessFacts f)
    {
        var list = new List<PlatformGatewayReadinessItem>
        {
            new("systemBaseUrl", !string.IsNullOrWhiteSpace(f.SystemBaseUrl), true,
                string.IsNullOrWhiteSpace(f.SystemBaseUrl)
                    ? PaymentLiveGate.SystemBaseUrlMissing
                    : $"URL ของระบบ: {f.SystemBaseUrl} — ผู้จ่ายกลับมาที่โดเมนนี้หลังชำระ · URL แจ้งเตือนสร้างจากโดเมนนี้"),
            new("payableMethods", f.PayableMethodCount > 0, true,
                f.PayableMethodCount > 0 ? $"วิธีชำระที่ผู้เช่าใช้ได้ {f.PayableMethodCount} วิธี" : PaymentLiveGate.NoPayableMethod),
            // ฝ่ายค้านรอบ 227 S4: บัญชีพักหาย = เงินออนไลน์ลงธนาคารทั้งที่ยังไม่เข้า (กระทบยอดรอบโอนพังเงียบ) ⇒ ด่านเปิดรับเงินจริง ไม่ใช่แค่คำเตือน
            new("clearingAccount", f.ClearingAccountFound, true,
                f.ClearingAccountFound
                    ? "บัญชีพักเงินผู้ให้บริการรับชำระ (11340 หรือที่ตั้งไว้) พร้อม — เงินออนไลน์ลงบัญชีพักแล้วล้างตอนบันทึกรอบโอน"
                    : "บริษัทผู้ให้บริการยังไม่มีผังบัญชีพักเงินผู้ให้บริการรับชำระ (แนะนำ 11340) — เงินออนไลน์จะลงธนาคารทั้งที่ยังไม่เข้า ⇒ กระทบยอดรอบโอนไม่ได้ · เพิ่มผังในบริษัทผู้ให้บริการ"),
            new("feeExpenseAccount", f.FeeExpenseAccountFound, false,
                f.FeeExpenseAccountFound
                    ? "ผังค่าธรรมเนียมรับชำระ (54710 หรือที่ตั้งไว้) พร้อม — ค่าธรรมเนียม + VAT ลงตอนบันทึกรอบโอน"
                    : "บริษัทผู้ให้บริการยังไม่มีผังค่าธรรมเนียมรับชำระ (แนะนำ 54710) — บันทึกรอบโอนจะถูกบล็อกจนกว่าจะเพิ่ม"),
            new("revenueAccount", f.RevenueAccountFound, false,
                f.RevenueAccountFound
                    ? $"ผังรายได้ค่าบริการ {f.RevenueAccountCode} พร้อม"
                    : $"ไม่พบผังรายได้ค่าบริการ {f.RevenueAccountCode} ในบริษัทผู้ให้บริการ — ตั้งที่ ตั้งค่าเว็บไซต์ → ใบเสร็จค่าบริการ (ออกใบเสร็จจะล้ม)"),
            new("cashAccount", !string.IsNullOrWhiteSpace(f.CashAccountCode) && f.CashAccountFound, false,
                string.IsNullOrWhiteSpace(f.CashAccountCode)
                    ? "ยังไม่ได้ตั้งผังธนาคารรับเงินโอนค่าบริการ — สลิปที่อนุมัติจะลงเงินสด (ค่าเริ่มต้นของระบบ)"
                    : f.CashAccountFound ? $"ผังธนาคารรับเงินโอน {f.CashAccountCode} พร้อม (ใช้กับสลิป — เงินออนไลน์ใช้บัญชีพัก)"
                    : $"ไม่พบผังธนาคาร {f.CashAccountCode} ในบริษัทผู้ให้บริการ — สลิปที่อนุมัติจะลงเงินสด"),
            new("bankTransfer", f.BankTransferInstructionsSet, false,
                f.BankTransferInstructionsSet
                    ? "ประกาศวิธีโอนให้ NextAcc แล้ว (ทางสำรองเมื่อปิดช่องทางออนไลน์)"
                    : "ยังไม่ได้ประกาศเลขบัญชีรับโอน — ปิดฉุกเฉินแล้วผู้เช่าไม่รู้จะโอนที่ไหน (ตั้งค่าเว็บไซต์ → วิธีโอนเงินค่าบริการ)"),
        };
        if (f.VatRegistered)
            // ฝ่ายค้านรอบ 227 S2: จด VAT + "ราคาไม่รวม VAT" — ยอดเรียกเก็บแพ็กเกจ/ส่วนเสริม = ราคาตั้ง (ไม่บวก VAT) ⇒ ใบเสร็จถอด VAT จากเงินที่รับ (รายได้ต่ำกว่าที่ตั้งใจ)
            // หรือถ้าบวก = ใบสูงกว่าเงินที่รับ · ไม่บวกยอดเงียบ ๆ ⇒ ด่านเปิดรับเงินจริง: ตั้งราคาแบบรวม VAT ก่อน
            list.Add(new("priceIncludesVat", f.PriceIncludesVat, true,
                f.PriceIncludesVat ? "ราคาแพ็กเกจ/ส่วนเสริมตั้งแบบรวม VAT แล้ว — ใบเสร็จ = เงินที่รับ (ถอด VAT 7/107)"
                    : "ตั้ง \"ราคาไม่รวม VAT\" แต่ระบบเรียกเก็บตามราคาตั้งโดยไม่บวก VAT — เปลี่ยนเป็นราคารวม VAT (ตั้งค่าเว็บไซต์ → ใบเสร็จค่าบริการ) และปรับราคาให้รวม VAT ก่อนเปิดรับเงินออนไลน์"));
        if (f.VatRegistered)
            list.Add(new("sellerTaxId", f.SellerTaxIdValid, false,
                f.SellerTaxIdValid ? "จด VAT — ออกใบกำกับภาษี/ใบเสร็จรับเงินเมื่อรับเงิน (§86/4 · จุดรับเงินบริการ §78/1)"
                    : "ตั้งว่าจด VAT แต่เลขผู้เสียภาษีผู้ขายไม่ถูกต้อง (13 หลัก + เลขตรวจสอบ) — ระบบจะออกใบเสร็จธรรมดาแทนใบกำกับภาษี"));
        return list;
    }

    /// <summary>ข้อที่ด่านเปิดรับเงินจริงใช้แต่ยังไม่ผ่าน (ข้อความ) — ตัวเดียวกับ <see cref="PaymentLiveGate.PlatformRefusal"/></summary>
    public static List<string> BlockingMissing(IEnumerable<PlatformGatewayReadinessItem> items)
        => items.Where(i => i.Blocking && !i.Ok).Select(i => i.Text).ToList();
}
