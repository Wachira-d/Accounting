using Accounting.Models.Enums;
using Accounting.Services.Implementations;

namespace Accounting.Helpers;

/// <summary>ข้อเท็จจริงของใบเพิ่มหนี้หนึ่งใบที่ใช้ตัดสิน "ลงเงินซ้ำ" — ผู้เรียก (service) หาจากฐาน แล้วให้ที่นี่ตัดสิน</summary>
/// <param name="ApprovalMoneyLeg">ยอดขาเงิน (ผัง 111xx/ผังที่ผูกธนาคาร) ใน JE อนุมัติที่ยังมีผล — ฝั่งขาย = Dr · ฝั่งซื้อ = Cr</param>
/// <param name="LivePaymentJournals">JE รับ/จ่ายชำระของใบนี้ที่ยังมีผล (Posted · ไม่ใช่ตัวกลับ · ยังไม่ถูกกลับ) พร้อมยอดขาเงิน</param>
public sealed record DebitNoteCashFacts(
    Guid DocumentId, string DocumentNumber, DateTime DocumentDate, bool IsPurchaseSide, PaymentType? PaymentType,
    decimal TotalAmount, decimal ApprovalMoneyLeg, IReadOnlyList<DebitNotePaymentJournal> LivePaymentJournals);

/// <summary>JE รับ/จ่ายชำระหนึ่งใบของใบเพิ่มหนี้</summary>
public sealed record DebitNotePaymentJournal(Guid JournalEntryId, string EntryNumber, decimal MoneyLeg);

/// <summary>
/// **ตาข่ายซ่อมข้อมูลเก่าของ V-DN-01** (ทีม V 2026-10-26 · `docs/JE_POSTING_AUDIT.md` §3 · คำตัดสินข้อ 160 · F2 ข้อ 9 "persist แล้วต้องมี migration") —
/// ใบเพิ่มหนี้เงินเชื่อของใบที่ชำระครบ ซึ่ง AutoPost รุ่นก่อนลง<b>ขาเงิน</b>ตอนอนุมัติ (ยืมกติกาใบลดหนี้) แล้วตอนรับ/จ่ายชำระจริง
/// `CreatePaymentJournalAsync` ลงขาเงินอีกรอบ ⇒ เงินใน GL เกิน (ฝั่งขาย) / ขาด (ฝั่งซื้อ) เท่ายอดขาเงินของ JE ชำระ · ลูกหนี้/เจ้าหนี้ติดทิศผิด
///
/// <para>รูปเดียวกับ <c>StockLedger.FindProductTotalMismatchesAsync</c> → <c>RepairProductTotalsAsync</c>: <b>รายงานอ่านอย่างเดียวก่อน</b> (รายใบ · ยอดเกิน ·
/// JE ที่เกี่ยว) → ปุ่มซ่อม<b>เฉพาะใบที่ผู้ใช้เลือก</b> = กลับรายการ JE รับ/จ่ายชำระที่ซ้ำด้วย <c>ReverseJournalEntryAsync</c> เดิม (JE อนุมัติที่มีขาเงินคงไว้ =
/// เงินเข้า/ออกครั้งเดียว · ลูกหนี้/เจ้าหนี้ที่ไม่เคยถูกตั้งกลับเป็นศูนย์) · <b>ห้ามซ่อมอัตโนมัติ</b> · ใบที่ยังไม่ชำระ = ไม่มีปุ่ม
/// (ทางไปต่อ: ยกเลิกแล้วออกใหม่ — JE ใหม่ลงลูกหนี้/เจ้าหนี้ตามตัวตัดสินใหม่)</para>
/// </summary>
public static class DebitNoteCashRepair
{
    public const string Kind = "DebitNoteCashPostedTwice";
    public const string RuleCode = "JE-DN-CASH-TWICE";
    public const decimal Tolerance = 0.01m;

    /// <summary>ใบนี้เข้าข่ายไหม: DN เงินเชื่อ (ไม่ใช่จ่ายทันที) ที่ JE อนุมัติมีขาเงิน — ถ้า DN จ่ายทันทีเอง ขาเงินตอนอนุมัติถูกต้อง</summary>
    public static bool IsSuspect(DebitNoteCashFacts f)
        => f.PaymentType != Models.Enums.PaymentType.Cash && f.ApprovalMoneyLeg > Tolerance;

    /// <summary>ยอดเงินที่ลงซ้ำใน GL = Σ ขาเงินของ JE ชำระที่ยังมีผล (0 = ยังไม่ชำระ — ยังไม่เกิน แต่จะเกินทันทีที่ชำระ)</summary>
    public static decimal DuplicatedMoney(DebitNoteCashFacts f) => f.LivePaymentJournals.Sum(j => j.MoneyLeg);

    /// <summary>JE ที่ปุ่มซ่อมจะกลับรายการ (= JE ชำระที่ยังมีผลทุกใบ) — ว่าง = ไม่มีอะไรให้ซ่อม</summary>
    public static IReadOnlyList<Guid> JournalsToReverse(DebitNoteCashFacts f)
        => IsSuspect(f) ? f.LivePaymentJournals.Select(j => j.JournalEntryId).ToList() : Array.Empty<Guid>();

    public static string FixPath(Guid documentId) => $"data-integrity/debit-note-cash/{documentId}/reverse-payment-journals";

    public static string ReversalDescription(string documentNumber, string paymentEntryNumber)
        => $"กลับรายการชำระซ้ำ {paymentEntryNumber} ของใบเพิ่มหนี้ {documentNumber} (JE อนุมัติลงขาเงินไว้แล้ว · {RuleCode})";

    /// <summary>แถวรายงาน (null = ไม่เข้าข่าย) · ซ้าย = เงินที่ลงไว้ (อนุมัติ + ชำระ) · ขวา = ที่ควรเป็น (ครั้งเดียว) ·
    /// ปุ่ม "แก้" เฉพาะเมื่อมี JE ชำระให้กลับ</summary>
    public static DataIntegritySuspect? ToSuspect(DebitNoteCashFacts f)
    {
        if (!IsSuspect(f)) return null;
        var dup = DuplicatedMoney(f);
        var side = f.IsPurchaseSide ? "เจ้าหนี้" : "ลูกหนี้";
        var money = f.IsPurchaseSide ? "เงินออก" : "เงินเข้า";
        var detail = $"ใบเพิ่มหนี้เงินเชื่อที่ JE อนุมัติ (รุ่นก่อน 2026-10-26) ลงขา{money} {f.ApprovalMoneyLeg:N2} ทั้งที่ยังไม่ชำระ · "
            + (f.LivePaymentJournals.Count == 0
                ? $"ยังไม่มีการชำระ — ถ้าชำระจะ{money}ซ้ำ · ทางไปต่อ: ยกเลิกแล้วออกใบใหม่ (JE ใหม่ลง{side})"
                : $"ชำระแล้ว {f.LivePaymentJournals.Count} รายการ ({string.Join(", ", f.LivePaymentJournals.Select(j => j.EntryNumber))}) ⇒ {money}ซ้ำ {dup:N2} · "
                  + $"{side}ติดทิศผิด {dup:N2} · ปุ่ม \"แก้\" = กลับรายการ JE ชำระ (คง JE อนุมัติที่มีขาเงิน) · ตรวจกระทบยอดธนาคารก่อนกด");
        return new DataIntegritySuspect(Kind, f.DocumentId, f.DocumentNumber, f.DocumentDate, detail,
            f.ApprovalMoneyLeg + dup, f.TotalAmount,
            $"/pages/documents.html?openDoc={f.DocumentId}", "เปิดเอกสาร",
            f.LivePaymentJournals.Count > 0 ? FixPath(f.DocumentId) : null,
            f.LivePaymentJournals.Count > 0
                ? $"กลับรายการ JE ชำระ {f.LivePaymentJournals.Count} รายการของ {f.DocumentNumber} ({dup:N2} บาท) — JE อนุมัติที่ลงขาเงินไว้คงเดิม · ย้อนกลับได้ด้วยการกลับรายการอีกครั้ง"
                : null);
    }
}
