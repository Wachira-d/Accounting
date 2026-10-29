using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// ตัวตัดสินเดียว "เอกสารขายที่ผูกบัตรคอร์ส ถูกยกเลิก/ลดหนี้ได้ไหม" + "เปิดบัตรจากเอกสารได้ไหม" (ทีม L · ฝ่ายค้านรอบ 1) — pure
///
/// <para><b>ที่มา:</b> ยกเลิกใบขายที่ใช้เครดิตไปแล้ว ⇒ JE กลับ Dr 21713 เต็มยอด แต่รายได้ที่รับรู้แล้วไม่ถูกกลับ ⇒ 21713 ติดลบ + บัตรค้าง "ใช้งานได้" ·
/// ใบเสร็จที่ตัดลูกหนี้ใบแจ้งหนี้ (ไม่ Cr 21713) เคยเปิดบัตรได้จากยอดบรรทัด ⇒ สองบัตรจากเงินก้อนเดียว</para>
///
/// <para><b>กติกา:</b>
/// <list type="bullet">
/// <item>ยกเลิกใบขาย: บัตรยังไม่ถูกใช้เลย (ใช้ 0 · จอง 0 · รับรู้ 0) ⇒ ยกเลิกได้ + ปิดบัตรในธุรกรรมเดียวกัน · ใช้แล้ว ⇒ บล็อก (ทางไปต่อ: ใบลดหนี้ส่วนที่ยังไม่ใช้)</item>
/// <item>ใบลดหนี้อ้างใบขาย (ทุกบรรทัดต้องลงผังรายได้รับล่วงหน้าของบัตร): ยอดสุทธิ<b>เท่ากับ</b>ส่วนที่ยังไม่รับรู้ (±0.01) + ไม่มีการจองค้าง ⇒
///   คืนเงินทั้งหมดที่เหลือ + ปิดบัตร · ยอด<b>น้อยกว่า</b>ส่วนที่ยังไม่รับรู้ (รอบ 205 W6 · R-38 คืนเงินบางส่วน) ⇒ ลดเครดิต k หน่วย
///   โดยยอดต้อง<b>เท่ากับ</b> k × ราคาต่อเครดิตคงเหลือ (±0.01 · รอบ 208 ฝ่ายค้าน W6 ข้อ 3 — เดิม floor ทำให้คืนเงินเกินมูลค่าเครดิตที่ตัด) ·
///   ราคาต่อเครดิตคงเหลือ = ส่วนที่ยังไม่รับรู้ ÷ เครดิตที่ยังไม่ใช้ · เครดิตคงเหลือหลังลดต้อง<b>มากกว่า</b>ที่ใช้ไปแล้ว+จองไว้ (ห้ามเหลือบัตรที่ใช้ต่อไม่ได้แต่ยังเปิด) ·
///   บัตรยัง Active · ยอดเกินส่วนที่เหลือ / น้อยกว่าราคา 1 เครดิต / ไม่ลงตัวกับราคาต่อเครดิต ⇒ บล็อกพร้อมยอดที่ออกได้</item>
/// <item>ยกเลิกใบลดหนี้ที่ปิดบัตรไปแล้ว ⇒ บล็อก (บัตรเปิดกลับไม่ได้) · ยกเลิกใบลดหนี้คืนบางส่วน (รอบ 208) ⇒ คืนเครดิต + ถอนยอดคืนใต้ล็อกบัตร
///   (บัตรปิด/หมดอายุแล้ว ⇒ บล็อก)</item>
/// <item>เปิดบัตรจากเอกสาร: ยอดสัญญา = เครดิตสุทธิของ JE ที่ลงผังรายได้รับล่วงหน้า (บาท) · ≤ 0 ⇒ ไม่เปิด · สายเอกสารเดียวกันมีบัตรแล้ว ⇒ ไม่เปิด</item>
/// </list></para>
/// </summary>
public static class CreditPassDocumentRule
{
    public const string RuleCode = "TFRS-NPAE-6-CREDIT-DOC";

    /// <param name="CreditsTotal">เครดิตทั้งหมดของบัตร (หลังหักที่ถูกลดด้วยใบลดหนี้บางส่วนแล้ว) — ใช้คิดราคาต่อเครดิตของการคืนบางส่วน · 0 = ไม่รองรับคืนบางส่วน</param>
    public sealed record PassFacts(string PassNumber, CreditPassStatus Status, decimal CreditsUsed, decimal CreditsReserved,
        decimal ContractAmount, decimal RecognizedAmount, decimal RefundedAmount,
        decimal CreditsTotal = 0m, CreditUnit Unit = CreditUnit.Session)
    {
        public decimal Deferred => Math.Max(0m, ContractAmount - RecognizedAmount - RefundedAmount);
        public bool Untouched => CreditsUsed <= 0m && CreditsReserved <= 0m && RecognizedAmount <= 0m;
        /// <summary>เครดิตที่ยังไม่ใช้ (ทั้งหมด − ใช้แล้ว · ไม่ติดลบ)</summary>
        public decimal UnusedCredits => Math.Max(0m, CreditsTotal - CreditsUsed);
        /// <summary>รอบ 208 (ฝ่ายค้าน W6 ข้อ 3): ราคาต่อเครดิต<b>คงเหลือ</b>แบบไม่ปัด = ส่วนที่ยังไม่รับรู้ ÷ เครดิตที่ยังไม่ใช้ — มูลค่าที่ 21713 ถือต่อเครดิตจริง ·
        /// คืน k เครดิตที่ราคานี้ ⇒ ราคาต่อเครดิตของที่เหลือไม่เปลี่ยน (เดิมใช้ (สัญญา − คืนแล้ว) ÷ เครดิตทั้งหมด ⇒ เพี้ยนเมื่อที่รับรู้ไปแล้วไม่เท่าราคานั้น)</summary>
        public decimal RefundUnitPrice => UnusedCredits <= 0m ? 0m : Deferred / UnusedCredits;
        /// <summary>ราคาต่อเครดิตคงเหลือ (ปัด 2 ตำแหน่ง · แสดงผล) — ตัวเลขบนหน้าบัตร/ข้อความ</summary>
        public decimal PricePerCredit => Math.Round(RefundUnitPrice, 2, MidpointRounding.AwayFromZero);
        /// <summary>เครดิตที่ลดได้สูงสุดโดยยังเหลือให้ใช้อย่างน้อย 1 หน่วยนับ (คงเหลือ &gt; ใช้แล้ว+จองไว้)</summary>
        public decimal MaxRemovableCredits => Math.Max(0m, CreditsTotal - CreditsUsed - CreditsReserved - CourseCreditMath.CreditStep(Unit));
    }

    public enum DocAction { None = 0, CancelPass = 1, Block = 2, ReduceCredits = 3, RestoreCredits = 4 }

    /// <param name="CreditsToRemove">เฉพาะ <see cref="DocAction.ReduceCredits"/> — เครดิตที่ตัดออกจากบัตร (บัตรยัง Active) ·
    /// <see cref="DocAction.RestoreCredits"/> — เครดิตที่คืนเข้าบัตร (ยกเลิกใบลดหนี้คืนบางส่วน)</param>
    public sealed record Decision(DocAction Action, string? Message, decimal CreditsToRemove = 0m)
    {
        public static readonly Decision Nothing = new(DocAction.None, null);
    }

    /// <summary>ยกเลิกใบขายที่บัตร <paramref name="pass"/> ผูกอยู่ (null = ใบนี้ไม่ผูกบัตร / บัตรถูกปิดไปแล้ว)</summary>
    public static Decision OnVoidSale(PassFacts? pass, string documentNumber)
    {
        if (pass == null || pass.Status == CreditPassStatus.Cancelled) return Decision.Nothing;
        if (pass.Status == CreditPassStatus.Transferred) return TransferredBlock(pass, $"ยกเลิกเอกสาร {documentNumber}");
        if (pass.Untouched) return new(DocAction.CancelPass, null);
        return new(DocAction.Block,
            $"เอกสาร {documentNumber} เป็นใบขายของบัตรคอร์ส {pass.PassNumber} ที่ใช้ไปแล้ว (ใช้ {pass.CreditsUsed:0.##} · จองค้าง {pass.CreditsReserved:0.##} · "
            + $"รับรู้รายได้แล้ว {pass.RecognizedAmount:N2}) — ยกเลิกทั้งใบไม่ได้ (รายได้ที่รับรู้แล้วจะไม่ถูกกลับ · ค่าบริการรับล่วงหน้าติดลบ) · "
            + (pass.Deferred > 0m
                ? $"ทางไปต่อ: {(pass.CreditsReserved > 0m ? "ยกเลิก/ปิดการจองค้างก่อน แล้ว" : "")}ออกใบลดหนี้คืนเงินส่วนที่ยังไม่ใช้ ยอดสุทธิก่อน VAT {pass.Deferred:N2} บาท (ระบบปิดบัตรให้)"
                : "บัตรรับรู้รายได้ครบแล้ว — ไม่มียอดให้คืน"));
    }

    /// <summary>อนุมัติใบลดหนี้ที่อ้างใบขายของบัตร — <paramref name="creditNoteNet"/> = ยอดสุทธิก่อน VAT ของใบลดหนี้ ·
    /// <paramref name="allLinesToUnearned"/> = ทุกบรรทัดลงผังรายได้รับล่วงหน้าของบัตร (หรือไม่ระบุผัง ⇒ ลงกลับผังเดิม)</summary>
    public static Decision OnCreditNote(PassFacts? pass, string sourceNumber, decimal creditNoteNet, bool allLinesToUnearned)
    {
        if (pass == null || pass.Status == CreditPassStatus.Cancelled) return Decision.Nothing;
        if (pass.Status == CreditPassStatus.Transferred) return TransferredBlock(pass, $"ออกใบลดหนี้อ้าง {sourceNumber}");
        var head = $"ใบลดหนี้อ้าง {sourceNumber} ซึ่งเป็นใบขายของบัตรคอร์ส {pass.PassNumber} — ";
        if (pass.Deferred <= 0m)
            return new(DocAction.Block, head + "บัตรรับรู้รายได้ครบแล้ว (ไม่มีค่าบริการรับล่วงหน้าเหลือ) ลดหนี้จากใบนี้ไม่ได้");
        // รอบ 208: การจองค้างไม่บล็อกที่นี่แล้ว — RefundPreview ตัดสินเอง (คืนทั้งก้อน ⇒ บล็อก · คืนบางส่วน ⇒ ได้เมื่อคงเหลือ > ใช้แล้ว+จองไว้)
        // เดิมบล็อกทุกยอดก่อนถึงตัววางแผน ⇒ ใบร่างที่ปุ่มหน้าบัตรสร้างให้ (ตัวอย่างบอกว่าออกได้) อนุมัติไม่ผ่าน = สองด่านเล่าคนละเรื่อง
        if (!allLinesToUnearned)
            return new(DocAction.Block, head + "บรรทัดใบลดหนี้ต้องลงผังค่าบริการรับล่วงหน้าของบัตร (ไม่ใช่ผังรายได้) — แก้ผังบรรทัดแล้วอนุมัติใหม่");
        var plan = RefundPreview(pass, creditNoteNet);
        if (plan.Problem != null) return new(DocAction.Block, head + plan.Problem);
        return plan.ClosesPass ? new(DocAction.CancelPass, null) : new(DocAction.ReduceCredits, null, plan.CreditsToRemove);
    }

    /// <summary>สายโอนบัตรยาวสุดที่ตามไป (กันวน/ข้อมูลเสีย) — เกิน ⇒ หยุดที่บัตรสุดท้าย (ยัง Transferred ⇒ <see cref="TransferredBlock"/>)</summary>
    public const int MaxTransferHops = 10;

    /// <summary>รอบ 209 ฝ่ายค้าน (เงิน) ข้อ 6: ทอดถัดไปของสายโอน — บัตรโอนออกแล้วและรู้ปลายทาง ⇒ id ปลายทาง · ไม่งั้น null (หยุด) · ผู้เรียกล็อกแถวเอง</summary>
    public static Guid? NextTransferHop(CreditPassStatus status, Guid? transferredToPassId, int hopsSoFar)
        => status == CreditPassStatus.Transferred && transferredToPassId is Guid to && hopsSoFar < MaxTransferHops ? to : null;

    /// <summary>บัตรปลายทางถูกปิดแล้ว (คืนเงิน/ยกเลิก) ⇒ ไม่มีค่าบริการรับล่วงหน้าให้ลด</summary>
    public static string TransferredTargetClosedMessage(string sourceNumber, string fromPass, string toPass)
        => $"ออกใบลดหนี้อ้าง {sourceNumber} ไม่ได้ — บัตรคอร์ส {fromPass} โอนไปบัตร {toPass} ซึ่งถูกปิด (คืนเงิน/ยกเลิก) แล้ว ไม่มีค่าบริการรับล่วงหน้าเหลือให้ลด · "
           + "ถ้าต้องคืนเงินเพิ่ม ติดต่อผู้ดูแลบัญชีเพื่อปรับปรุงด้วย JE";

    /// <summary>รอบ 209 P5 (คำตัดสินข้อ 168 ฎ): บัตรที่โอนให้ลูกค้าคนอื่นแล้ว — ยอดรับล่วงหน้าย้ายไปบัตรปลายทาง (ไม่มี JE) ⇒ ยกเลิก/ลดหนี้ใบขายเดิม
    /// จะกลับ 21713 ที่บัตรปลายทางยังถือ ⇒ บล็อกเสมอ · ทางไปต่อ: จัดการที่บัตรปลายทาง (ใช้/หมดอายุตามปกติ) หรือให้ผู้ดูแลบัญชีออก JE ปรับปรุง</summary>
    private static Decision TransferredBlock(PassFacts pass, string what)
        => new(DocAction.Block,
            $"{what} ไม่ได้ — ใบนี้เป็นใบขายของบัตรคอร์ส {pass.PassNumber} ซึ่งโอนให้ลูกค้าคนอื่นแล้ว (ค่าบริการรับล่วงหน้าย้ายไปบัตรปลายทาง) · "
            + (what.StartsWith("ยกเลิก", StringComparison.Ordinal)
                // รอบ 209 ฝ่ายค้าน (เงิน) ข้อ 6: ทางไปต่อจริง — ใบลดหนี้อ้างใบนี้ตัดสินกับบัตรปลายทาง (CreditPassDocumentHook.FollowTransfersAsync)
                ? "ยกเลิกทั้งใบจะกลับยอดที่บัตรปลายทางยังถืออยู่ · ทางไปต่อ: ออกใบลดหนี้อ้างใบนี้ ยอดสุทธิก่อน VAT ไม่เกินมูลค่าคงเหลือของบัตรปลายทาง (ระบบลดเครดิต/ปิดบัตรปลายทางให้)"
                : "ตามสายโอนไปบัตรปลายทางไม่ถึง (ข้อมูลบัตรปลายทางหาย/สายโอนยาวเกิน) — ติดต่อผู้ดูแลบัญชีเพื่อปรับปรุงด้วย JE"));

    /// <summary>ผลของการคืนเงิน <paramref name="Net"/> (สุทธิก่อน VAT) จากบัตร — ปิดบัตร / ลดเครดิต / หรือเหตุที่ทำไม่ได้พร้อมยอดที่ออกได้</summary>
    public sealed record RefundPlan(decimal Net, decimal CreditsToRemove, decimal CreditsRemaining, bool ClosesPass, string? Problem);

    /// <summary>วางแผนคืนเงินจากบัตร — ตัวตัดสินเดียวของทั้งด่านอนุมัติใบลดหนี้ (<see cref="OnCreditNote"/>) และปุ่ม "ออกใบลดหนี้คืนเงิน (ร่าง)" บนหน้าบัตร ·
    /// ยอด = ส่วนที่เหลือ (±0.01) ⇒ ปิดบัตร (ต้องไม่มีการจองค้าง) · ยอดน้อยกว่า ⇒ ลดเครดิต k หน่วย โดยยอดต้อง = k × <see cref="PassFacts.RefundUnitPrice"/> (±0.01)
    /// และคงเหลือต้อง &gt; ใช้แล้ว+จองไว้ · ยอดเกิน / น้อยกว่าราคา 1 หน่วย / ไม่ลงตัวกับราคาต่อเครดิต ⇒ Problem ·
    /// <paramref name="credits"/> (รอบ 208 ฝ่ายค้าน W6 ข้อ 4) = ผู้ใช้ระบุจำนวนเครดิตตรง ๆ ⇒ ใช้ตัวนั้นเลย (ไม่แปลงเป็นบาทแล้วหารกลับ — เดิมบัตรชั่วโมงปัดสตางค์แล้วหาย 0.25) ·
    /// ยอดคิดเป็น k × ราคา (เครดิตที่ยังไม่ใช้ทั้งหมด ⇒ เท่าส่วนที่เหลือ) · <paramref name="net"/> ไม่ถูกใช้</summary>
    public static RefundPlan RefundPreview(PassFacts pass, decimal net, decimal? credits = null)
    {
        if (pass.Deferred <= 0m) return new(net, 0m, pass.CreditsTotal, false, "บัตรรับรู้รายได้ครบแล้ว (ไม่มีค่าบริการรับล่วงหน้าเหลือ) — ไม่มียอดให้คืน");
        if (credits is decimal askCredits)
        {
            var step = CourseCreditMath.CreditStep(pass.Unit);
            if (askCredits <= 0m) return new(0m, 0m, pass.CreditsTotal, false, "จำนวนเครดิตที่คืนต้องมากกว่า 0");
            if (askCredits % step != 0m)
                return new(0m, 0m, pass.CreditsTotal, false, $"จำนวนเครดิตต้องเป็นพหุคูณของ {step:0.##} (หน่วยนับเล็กสุดของบัตร)");
            if (pass.CreditsTotal <= 0m)
                return new(0m, 0m, pass.CreditsTotal, false, $"บัตรนี้ไม่รองรับการคืนเงินบางส่วน — ออกใบลดหนี้เท่าส่วนที่เหลือ {pass.Deferred:N2} บาท (ระบบปิดบัตร)");
            net = askCredits >= pass.UnusedCredits ? pass.Deferred : RefundValue(pass, askCredits);
        }
        if (net <= 0m) return new(net, 0m, pass.CreditsTotal, false, "ยอดสุทธิก่อน VAT ต้องมากกว่า 0");
        if (net > pass.Deferred + 0.01m)
            return new(net, 0m, pass.CreditsTotal, false,
                $"ยอดสุทธิก่อน VAT {net:N2} เกินส่วนที่ยังไม่ใช้ {pass.Deferred:N2} บาท — ออกได้สูงสุด {pass.Deferred:N2} (คืนทั้งส่วนที่เหลือ ⇒ ระบบปิดบัตร)");
        if (Math.Abs(net - pass.Deferred) <= 0.01m)
        {
            if (pass.CreditsReserved > 0m)
                return new(net, 0m, pass.CreditsTotal, true,
                    $"มีการจองค้าง {pass.CreditsReserved:0.##} เครดิต — ยกเลิก/ปิดการจองก่อนจึงคืนทั้งส่วนที่เหลือได้"
                    + (pass.CreditsTotal > 0m && MaxPartialAmount(pass) > 0m ? $" หรือคืนบางส่วนไม่เกิน {MaxPartialAmount(pass):N2} บาท" : ""));
            return new(net, Math.Max(0m, pass.CreditsTotal - pass.CreditsUsed), 0m, true, null);
        }
        // คืนเงินบางส่วน (R-38)
        if (pass.CreditsTotal <= 0m)
            return new(net, 0m, pass.CreditsTotal, false, $"บัตรนี้ไม่รองรับการคืนเงินบางส่วน — ออกใบลดหนี้เท่าส่วนที่เหลือ {pass.Deferred:N2} บาท (ระบบปิดบัตร)");
        var unit = CourseCreditMath.CreditStep(pass.Unit);
        // ยอดที่ลงตัว k หน่วย: ระบุเครดิตมา ⇒ ตัวนั้น · ไม่งั้นหาจากยอด (เผื่อเศษสตางค์ของการปัด ±0.01 ก่อน floor)
        var removeCredits = credits ?? CourseCreditMath.CreditsRefundable(net + RefundTolerance, pass.RefundUnitPrice, pass.Unit);
        if (removeCredits <= 0m)
            return new(net, 0m, pass.CreditsTotal, false,
                $"ยอด {net:N2} น้อยกว่าราคา {unit:0.##} เครดิต ({pass.PricePerCredit:N2} บาท/เครดิต) — ไม่มีเครดิตให้ลด · "
                + $"ขั้นต่ำ {RefundValue(pass, unit):N2} บาท หรือคืนทั้งส่วนที่เหลือ {pass.Deferred:N2} บาท");
        // รอบ 208 (ฝ่ายค้าน W6 ข้อ 3): ยอดคืนต้องเท่ามูลค่าเครดิตที่ตัด — เดิม floor ⇒ คืน 1,000 ตัดได้ 2 เครดิต (841.12) ⇒ จ่ายเกิน 158.88 จาก 21713 ของเครดิตที่ยังอยู่บนบัตร
        var value = RefundValue(pass, removeCredits);
        if (credits == null && Math.Abs(net - value) > RefundTolerance)
        {
            var nextCredits = removeCredits + unit;
            return new(net, 0m, pass.CreditsTotal, false,
                $"ยอด {net:N2} ไม่ลงตัวกับราคาต่อเครดิตคงเหลือ ({pass.PricePerCredit:N2} บาท/เครดิต) — คืนบางส่วนต้องเท่ามูลค่าเครดิตที่ลด: "
                + $"{removeCredits:0.##} เครดิต = {value:N2} บาท"
                + (nextCredits < pass.UnusedCredits ? $" หรือ {nextCredits:0.##} เครดิต = {RefundValue(pass, nextCredits):N2} บาท" : "")
                + $" · หรือคืนทั้งส่วนที่เหลือ {pass.Deferred:N2} บาท (ระบบปิดบัตร)");
        }
        var remaining = pass.CreditsTotal - removeCredits;
        if (remaining <= pass.CreditsUsed + pass.CreditsReserved)
        {
            var maxCredits = pass.MaxRemovableCredits;
            return new(net, 0m, pass.CreditsTotal, false,
                $"ลด {removeCredits:0.##} เครดิตแล้วจะเหลือ {remaining:0.##} ซึ่งไม่เกินที่ใช้แล้ว {pass.CreditsUsed:0.##} + จองไว้ {pass.CreditsReserved:0.##} — "
                + (maxCredits > 0m
                    ? $"คืนบางส่วนได้ไม่เกิน {maxCredits:0.##} เครดิต ({MaxPartialAmount(pass):N2} บาท) หรือคืนทั้งส่วนที่เหลือ {pass.Deferred:N2} บาท (ระบบปิดบัตร)"
                    : $"คืนได้เฉพาะทั้งส่วนที่เหลือ {pass.Deferred:N2} บาท (ระบบปิดบัตร)"));
        }
        return new(credits == null ? net : value, removeCredits, remaining, false, null);
    }

    /// <summary>ความคลาดเคลื่อนที่ยอมให้ระหว่างยอดใบลดหนี้กับมูลค่าเครดิต (เศษสตางค์จากการปัด)</summary>
    public const decimal RefundTolerance = 0.01m;

    /// <summary>มูลค่าบาทของ <paramref name="credits"/> เครดิตที่ราคาต่อเครดิตคงเหลือ (ปัด 2 ตำแหน่ง AwayFromZero · ไม่เกินส่วนที่เหลือ) — สูตรเดียวของยอดคืนบางส่วน</summary>
    private static decimal RefundValue(PassFacts pass, decimal credits)
        => Math.Min(pass.Deferred, Math.Round(credits * pass.RefundUnitPrice, 2, MidpointRounding.AwayFromZero));

    /// <summary>ยอดคืนบางส่วนสูงสุด (บาท) = มูลค่าของเครดิตที่ลดได้สูงสุด (ไม่เกินส่วนที่เหลือ)</summary>
    public static decimal MaxPartialAmount(PassFacts pass) => RefundValue(pass, pass.MaxRemovableCredits);

    /// <summary>ยกเลิกใบลดหนี้ — ใบนี้เป็นตัวปิดบัตรคอร์ส ⇒ บล็อก (ยอดคืนเงินออกไปแล้ว · บัตรเปิดกลับไม่ได้)</summary>
    public static Decision OnVoidCreditNote(string? cancelledPassNumber, string creditNoteNumber)
        => cancelledPassNumber == null
            ? Decision.Nothing
            : new(DocAction.Block, $"ใบลดหนี้ {creditNoteNumber} เป็นใบคืนเงินที่ปิดบัตรคอร์ส {cancelledPassNumber} แล้ว — ยกเลิกไม่ได้ "
                + "(ถ้าออกผิด ให้ออกใบขายคอร์สใหม่ให้ลูกค้า)");

    /// <summary>รอบ 208 (ฝ่ายค้าน W6 ข้อ 2 · BLOCKING): ยกเลิกใบลดหนี้ที่<b>คืนเงินบางส่วน</b> (ลดเครดิต <paramref name="creditsRemoved"/> · ยอด <paramref name="net"/>) —
    /// การกลับ JE ของใบลดหนี้คืน Cr 21713 ยอด net ⇒ บัตรต้องได้เครดิต + ยอดคืนกลับในธุรกรรมเดียวกัน มิฉะนั้น 21713 ไม่ตรงกับบัตร ·
    /// บัตรใช้งานได้/ใช้หมด ⇒ คืนเครดิต (ใช้หมด ⇒ เปิดกลับ) · บัตรปิด/หมดอายุ/ไม่พบ หรือยอดคืนบนบัตรน้อยกว่า net ⇒ บล็อกพร้อมทางไปต่อ</summary>
    public static Decision OnVoidPartialCreditNote(PassFacts? pass, string creditNoteNumber, decimal creditsRemoved, decimal net)
    {
        const string tail = " — ถ้าออกผิดและต้องคืนสิทธิให้ลูกค้า ให้ออกใบขายคอร์สใหม่ (ใบลดหนี้นี้คงไว้)";
        if (pass == null)
            return new(DocAction.Block, $"ใบลดหนี้ {creditNoteNumber} ลดเครดิตของบัตรคอร์สที่ไม่พบแล้ว — ยกเลิกไม่ได้ (ค่าบริการรับล่วงหน้าจะไม่ตรงกับบัตร){tail}");
        if (pass.Status is not (CreditPassStatus.Active or CreditPassStatus.Exhausted))
            return new(DocAction.Block,
                $"ใบลดหนี้ {creditNoteNumber} ลดเครดิตบัตรคอร์ส {pass.PassNumber} ซึ่งตอนนี้ {CourseCreditMath.StatusTh(pass.Status)} แล้ว — ยกเลิกไม่ได้ "
                + $"(คืนเครดิตเข้าบัตรที่ปิด/หมดอายุไม่ได้ · ค่าบริการรับล่วงหน้าจะไม่ตรงกับบัตร){tail}");
        if (creditsRemoved <= 0m || net <= 0m || pass.RefundedAmount + RefundTolerance < net)
            return new(DocAction.Block,
                $"ใบลดหนี้ {creditNoteNumber}: ยอดคืนที่บัตร {pass.PassNumber} บันทึกไว้ {pass.RefundedAmount:N2} ไม่พอถอน {net:N2} (เครดิต {creditsRemoved:0.##}) — "
                + $"ยกเลิกเองไม่ได้ ตรวจประวัติสมุดเครดิตของบัตรก่อน{tail}");
        return new(DocAction.RestoreCredits, null, creditsRemoved);
    }

    /// <summary>รอบ 208 (ฝ่ายค้าน W6 หมายเหตุ): ปุ่ม "ออกใบลดหนี้คืนเงิน (ร่าง)" ออก<b>ใบลดหนี้ (§86/10)</b> ได้เฉพาะเมื่อใบขายเป็นใบกำกับภาษีของผู้จด VAT —
    /// บริษัทไม่จด VAT (ใบขายเป็นใบเสร็จ) หรือบริการยกเว้น VAT §81 (ไม่มีใบกำกับให้อ้าง) ⇒ ใบลดหนี้ไม่ใช่เอกสารที่ถูก · null = ออกได้</summary>
    public static string? RefundDocumentProblem(bool companyVatRegistered, decimal saleLineVatRate)
    {
        if (!companyVatRegistered)
            return "บริษัทไม่ได้จดทะเบียนภาษีมูลค่าเพิ่ม — ใบลดหนี้ (ม.86/10) ออกได้เฉพาะผู้ประกอบการจด VAT ที่ออกใบกำกับภาษีไว้ · "
                + "ใบขายคอร์สนี้เป็นใบเสร็จ ระบบจึงไม่สร้างใบลดหนี้ให้ · คืนเงินบางส่วนของบัตรนี้ให้ผู้ทำบัญชีบันทึกจ่ายคืนเอง (ระบบยังไม่ลดเครดิตให้ในเส้นนี้)";
        if (saleLineVatRate < 0m)
            return "บริการในบัตรนี้ยกเว้นภาษีมูลค่าเพิ่ม (ม.81) — ไม่มีใบกำกับภาษีให้ใบลดหนี้ (ม.86/10) อ้าง ระบบจึงไม่สร้างใบลดหนี้ให้ · "
                + "คืนเงินบางส่วนของบัตรนี้ให้ผู้ทำบัญชีบันทึกจ่ายคืนเอง (ระบบยังไม่ลดเครดิตให้ในเส้นนี้)";
        return null;
    }

    /// <summary>เหตุที่เปิดบัตรจากเอกสารไม่ได้ · null = เปิดได้ · <paramref name="journalUnearnedCredit"/> = Σ(Cr − Dr) ของ JE ที่มีผลของใบนี้บนผังรายได้รับล่วงหน้า (บาท)</summary>
    public static string? ActivateProblem(string documentNumber, decimal journalUnearnedCredit, string? chainPassNumber)
    {
        if (chainPassNumber != null)
            return $"เอกสาร {documentNumber} อยู่ในสายเอกสารเดียวกับบัตร {chainPassNumber} ที่เปิดไปแล้ว — ห้ามเปิดซ้ำจากเงินก้อนเดียวกัน";
        if (journalUnearnedCredit <= 0m)
            return $"รายการบัญชีของเอกสาร {documentNumber} ไม่ได้ลงเครดิตผังค่าบริการรับล่วงหน้า (เช่นใบเสร็จที่ตัดลูกหนี้ หรือใบที่ลงรายได้ไปแล้ว) — "
                + "เปิดบัตรจากใบนี้ไม่ได้ (จะรับรู้รายได้ซ้ำ) · เปิดจากใบที่ตั้งค่าบริการรับล่วงหน้าจริง";
        return null;
    }
}
