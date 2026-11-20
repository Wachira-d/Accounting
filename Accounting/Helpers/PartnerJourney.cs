namespace Accounting.Helpers;

/// <summary>รอบ 281 (คำตัดสิน 281-3) — ตัวตัดสินเดียวของ "เส้นทางของใบนี้" ระหว่างคู่ค้า NextAcc (A ผู้ขาย ↔ B ผู้ซื้อ):
/// ใบแจ้งหนี้ → ใบซื้อร่าง → ชำระ → ใบเสร็จ/ใบกำกับ → ผู้ซื้อบันทึกจ่าย (ใบสำคัญจ่าย) → 50 ทวิ ออก → 50 ทวิ ส่ง → ผู้ขายกระทบภาษีถูกหัก
/// <para>pure · server computes, page displays (ป้าย/สถานะ/เหตุทั้งหมดมาจากที่นี่ — หน้า partners.html ไม่มีสำเนากติกา) ·
/// <b>ไม่แต่งสถานะ</b>: "เสร็จ" เฉพาะเมื่อผู้เรียกส่งหลักฐานจริงของขั้นนั้น (แถว/เวลา) · ไม่รู้ = "รอ" ไม่ใช่ "เสร็จ" ·
/// ฝั่งที่ดูเห็นเลขเอกสาร/เหตุภายในของตัวเองเท่านั้น — ของอีกบริษัทเห็นแค่สถานะ + วันที่ (ไม่เปิดเผยเลขภายใน/เหตุจากค่าตั้งของเขา)</para></summary>
public static class PartnerJourney
{
    public enum Step { InvoiceSent, PurchaseDraft, Payment, TaxInvoice, BuyerRecorded, WhtCertIssued, WhtCertDelivered, WhtCertCredited }
    public enum State { Done, Waiting, Blocked, NotApplicable }

    /// <summary>ข้อเท็จจริงจากฐานของทั้งสองบริษัท (ผู้โหลด <c>PartnerJourneyService</c> ผูก CompanyId ของแต่ละฝั่งตาม link) ·
    /// <paramref name="SellerSettled"/> = ยอดค้างของใบผู้ขาย ≤ 0 (ชำระครบ) · <paramref name="SellerReceived"/> = เงิน + ภาษีหักที่ผู้ขายบันทึกรับแล้ว</summary>
    public sealed record Facts(
        bool ViewerIsSeller,
        DateTime InvoiceSentAt,
        string SellerDocumentNumber,
        bool SourceIsTaxInvoice,
        bool SourceVoided,
        DateTime? PurchaseCreatedAt,
        string? PurchaseNumber,
        string? PurchaseProblem,
        decimal Total,
        decimal SellerReceived,
        bool SellerSettled,
        DateTime? LastReceivedAt,
        DateTime? ReceiptIssuedAt,
        string? ReceiptNumber,
        DateTime? ReceiptDeliveredAt,
        string? InputVatEvidenceLabel,
        DateTime? BuyerRecordedAt,
        string? BuyerPaymentNumber,
        string? BuyerBlockedReason,
        decimal PurchaseWht,
        DateTime? WhtCertIssuedAt,
        string? WhtCertNumber,
        decimal WhtCertAmount,
        DateTime? WhtDeliveredAt,
        string? WhtDeliveryError,
        bool SendWhtEnabled,
        DateTime? WhtCreditedAt,
        bool WhtAwaitingConfirm);

    /// <summary>หนึ่งแถวของเส้นทาง — <paramref name="State"/>/<paramref name="Step"/> ส่งเป็นชื่อ enum (กฎ F2 ข้อ 5)</summary>
    public sealed record Row(string Step, string Label, string State, string StateLabel, DateTime? At, string? Detail);

    public static string StateLabel(State s) => s switch
    {
        State.Done => "เสร็จแล้ว",
        State.Waiting => "รอ",
        State.Blocked => "หยุด — ต้องให้คนดำเนินการ",
        _ => "ไม่เกี่ยวกับใบนี้",
    };

    public static string Label(Step s) => s switch
    {
        Step.InvoiceSent => "ผู้ขายส่งใบแจ้งหนี้เข้าระบบผู้ซื้อ",
        Step.PurchaseDraft => "ผู้ซื้อได้ใบซื้อร่าง",
        Step.Payment => "ผู้ซื้อชำระ / ผู้ขายรับเงิน",
        Step.TaxInvoice => "ใบเสร็จ/ใบกำกับภาษีถึงผู้ซื้อ",
        Step.BuyerRecorded => "ผู้ซื้อบันทึกจ่าย (ใบสำคัญจ่าย)",
        Step.WhtCertIssued => "ผู้ซื้อออกหนังสือรับรอง 50 ทวิ",
        Step.WhtCertDelivered => "50 ทวิ ส่งเข้าระบบผู้ขาย",
        _ => "ผู้ขายกระทบภาษีถูกหัก ณ ที่จ่าย",
    };

    public static IReadOnlyList<Row> Build(Facts f)
    {
        var rows = new List<Row>();
        void Add(Step step, State state, DateTime? at, string? detail)
            => rows.Add(new Row(step.ToString(), Label(step), state.ToString(), StateLabel(state), at, detail));
        var seller = f.ViewerIsSeller;

        // 1. ใบแจ้งหนี้เข้าระบบผู้ซื้อ (link มีอยู่ = ส่งแล้ว)
        if (f.SourceVoided) Add(Step.InvoiceSent, State.Blocked, f.InvoiceSentAt, $"ผู้ขายยกเลิกใบ {f.SellerDocumentNumber} แล้ว — ห้ามลงภาษีซื้อ/จ่ายตามใบนี้");
        else Add(Step.InvoiceSent, State.Done, f.InvoiceSentAt, $"ใบ {f.SellerDocumentNumber} ยอด {f.Total:N2} บาท");

        // 2. ใบซื้อร่าง
        if (f.PurchaseCreatedAt is DateTime pc)
            Add(Step.PurchaseDraft, State.Done, pc, seller ? "ระบบผู้ซื้อสร้างใบซื้อจากใบนี้แล้ว" : $"ใบซื้อ {f.PurchaseNumber}");
        else if (f.PurchaseProblem != null)
            Add(Step.PurchaseDraft, State.Blocked, null, seller ? "ผู้ซื้อยังไม่ได้รับใบนี้เป็นใบซื้ออัตโนมัติ (ผู้ซื้อบันทึกเองในระบบของเขา)" : f.PurchaseProblem);
        else
            Add(Step.PurchaseDraft, State.Waiting, null, seller ? "รอระบบผู้ซื้อ" : "เปิด \"สร้างใบซื้อร่างจากใบแจ้งหนี้/ใบกำกับที่คู่ค้าส่งมา\" ที่หน้าคู่ค้า");

        // 3. ชำระ
        // ฝ่ายค้าน 281 LOW-3: ยอดที่ผู้ขายบันทึกรับเป็นข้อมูลในสมุดผู้ขาย ⇒ ผู้ซื้อเห็นแค่สถานะ + วันที่
        if (f.SellerSettled)
            Add(Step.Payment, State.Done, f.LastReceivedAt, seller ? $"รับครบ {f.SellerReceived:N2} บาท (รวมภาษีหัก ณ ที่จ่าย)" : "ผู้ขายบันทึกรับครบแล้ว");
        else if (f.SellerReceived > 0.005m)
            Add(Step.Payment, State.Waiting, f.LastReceivedAt, seller ? $"รับแล้ว {f.SellerReceived:N2} จาก {f.Total:N2} บาท" : "ผู้ขายบันทึกรับบางส่วนแล้ว");
        else
            Add(Step.Payment, State.Waiting, null, "ยังไม่ชำระ");

        // 4. ใบเสร็จ/ใบกำกับถึงผู้ซื้อ
        if (f.ReceiptIssuedAt is DateTime ri)
        {
            var own = seller ? $"ใบ {f.ReceiptNumber}" : "ผู้ขายออกใบเสร็จ/ใบกำกับแล้ว";
            if (f.ReceiptDeliveredAt is DateTime rd)
                Add(Step.TaxInvoice, State.Done, rd, own + " · เข้าระบบผู้ซื้อแล้ว" + (!seller && f.InputVatEvidenceLabel != null ? " · " + f.InputVatEvidenceLabel : ""));
            else
                Add(Step.TaxInvoice, State.Waiting, ri, own + " · ยังไม่เข้าระบบผู้ซื้อ (ระบบส่งเมื่อใบอนุมัติ — ล้ม = ป้ายบนใบของผู้ขาย)");
        }
        else if (f.SourceIsTaxInvoice)
            Add(Step.TaxInvoice, State.Done, f.InvoiceSentAt, "ใบที่ส่งเป็นใบกำกับภาษีอยู่แล้ว" + (!seller && f.InputVatEvidenceLabel != null ? " · " + f.InputVatEvidenceLabel : ""));
        else if (f.SellerReceived > 0.005m)
            Add(Step.TaxInvoice, State.Blocked, null, seller
                ? "รับเงินแล้วแต่ยังไม่มีใบเสร็จ/ใบกำกับ ณ วันรับเงิน — ออกที่ใบแจ้งหนี้ (คิว \"รับเงินแล้วยังไม่มีใบกำกับ\")"
                : "ผู้ขายรับเงินแล้วแต่ยังไม่ส่งใบเสร็จ/ใบกำกับ — ภาษีซื้อยังพักไว้");
        else
            Add(Step.TaxInvoice, State.Waiting, null, "ผู้ขายออกเมื่อรับเงิน");

        // 5. ผู้ซื้อบันทึกจ่าย
        if (f.BuyerRecordedAt is DateTime br)
            Add(Step.BuyerRecorded, State.Done, br, seller ? "ผู้ซื้อบันทึกจ่ายแล้ว" : $"ใบสำคัญจ่าย {f.BuyerPaymentNumber}");
        else if (f.BuyerBlockedReason != null)
            Add(Step.BuyerRecorded, State.Blocked, null, seller ? "รอผู้ซื้อบันทึกจ่าย" : f.BuyerBlockedReason);
        else
            Add(Step.BuyerRecorded, State.Waiting, null, seller ? "รอผู้ซื้อบันทึกจ่าย" : f.SellerReceived > 0.005m
                ? "ผู้ขายรับเงินแล้ว — กด \"บันทึกจ่ายตามที่ผู้ขายรับแล้ว\" หรือเปิดงานอัตโนมัติกับคู่ค้า"
                : "รอการชำระ");

        // 6–8. 50 ทวิ
        if (f.PurchaseWht <= 0.005m && f.WhtCertIssuedAt == null)
        {
            const string none = "ใบนี้ไม่มีภาษีหัก ณ ที่จ่าย";
            Add(Step.WhtCertIssued, State.NotApplicable, null, none);
            Add(Step.WhtCertDelivered, State.NotApplicable, null, none);
            Add(Step.WhtCertCredited, State.NotApplicable, null, none);
            return rows;
        }
        if (f.WhtCertIssuedAt is DateTime wi)
            Add(Step.WhtCertIssued, State.Done, wi, seller ? "ผู้ซื้อออก 50 ทวิ แล้ว" : $"50 ทวิ {f.WhtCertNumber} ภาษีหัก {f.WhtCertAmount:N2} บาท");   // LOW-3
        else
            Add(Step.WhtCertIssued, State.Waiting, null, f.BuyerRecordedAt != null
                ? (seller ? "รอผู้ซื้อออก 50 ทวิ" : "บันทึกจ่ายแล้วแต่ยังไม่มี 50 ทวิ — ออกที่ใบซื้อ (หรือตรวจค่าตั้งออก 50 ทวิ อัตโนมัติ)")
                : "ออกเมื่อผู้ซื้อบันทึกจ่าย");

        if (f.WhtDeliveredAt is DateTime wd)
            Add(Step.WhtCertDelivered, State.Done, wd, "เข้ากล่องรับ 50 ทวิ ของผู้ขายแล้ว");
        else if (f.WhtCertIssuedAt == null)
            Add(Step.WhtCertDelivered, State.Waiting, null, "รอ 50 ทวิ");
        else if (f.WhtDeliveryError != null)
            Add(Step.WhtCertDelivered, State.Blocked, null, seller ? "ผู้ซื้อส่ง 50 ทวิ ทางระบบไม่สำเร็จ — ขอใบจากผู้ซื้อ" : f.WhtDeliveryError);
        else if (!f.SendWhtEnabled)
            Add(Step.WhtCertDelivered, State.Blocked, null, seller
                ? "ผู้ซื้อยังไม่เปิดส่ง 50 ทวิ ให้คู่ค้าอัตโนมัติ — ขอใบจากผู้ซื้อ"
                : "ยังไม่เปิด \"ส่งหนังสือรับรอง 50 ทวิ ที่เราออกให้คู่ค้าเข้าระบบของเขาอัตโนมัติ\" (หน้าคู่ค้า) — ส่งเองทางอีเมล/กระดาษ");
        else
            Add(Step.WhtCertDelivered, State.Waiting, null, "ระบบส่งในรอบถัดไป");

        if (f.WhtCreditedAt is DateTime wc)
            Add(Step.WhtCertCredited, State.Done, wc, "ผู้ขายยืนยันเครดิตภาษีถูกหักแล้ว");
        else if (f.WhtAwaitingConfirm)
            Add(Step.WhtCertCredited, State.Waiting, null, seller ? "50 ทวิ เข้ากล่องรับแล้ว — ตรวจและยืนยันที่หน้าเครดิตภาษีถูกหัก" : "รอผู้ขายยืนยัน");
        else
            Add(Step.WhtCertCredited, State.Waiting, null, "รอ 50 ทวิ ถึงผู้ขาย");
        return rows;
    }

    /// <summary>ขั้นปัจจุบัน = แถวแรกที่ยังไม่เสร็จ (ข้ามแถวไม่เกี่ยว) · null = ครบทั้งเส้น</summary>
    public static string? CurrentStep(IReadOnlyList<Row> rows)
        => rows.FirstOrDefault(r => r.State is not (nameof(State.Done) or nameof(State.NotApplicable)))?.Step;
}
