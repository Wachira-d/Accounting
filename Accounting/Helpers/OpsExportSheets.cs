using System.Globalization;
using Accounting.Models.DTOs.Lodging;
using Accounting.Models.DTOs.Pos;
using Accounting.Models.DTOs.Settlement;
using K = Accounting.Helpers.TabularCellKind;

namespace Accounting.Helpers;

/// <summary>
/// **คอลัมน์ไฟล์ส่งออกของงานหน้าร้าน/ช่องทางรับเงิน** (ที่พัก · รอบโอนจากแพลตฟอร์ม · ออเดอร์ POS) — X4 · 2026-10-14 (ตัวเขียน = <see cref="TabularExport"/>)
/// <para>รับ DTO ตัวเดียวกับรายการบนจอ · ยอด/ป้ายที่เซิร์ฟเวอร์ตัดสินแล้ว (BalanceDue · RefundPending · OverdueLabel · LineTypeLabel)
/// ออกตามนั้น ไม่คำนวณใหม่ · enum ออกเป็นชื่อ (รอบโอนใช้ป้ายไทยจาก catalog ตัวเดียวกับหน้าเว็บ) · วันที่ ISO ค.ศ. · เวลาประทับเป็นเวลาไทย</para>
/// </summary>
public static class OpsExportSheets
{
    /// <summary>ป้ายไทยของสถานะรอบโอน/การจับคู่ — ตัวเดียวกับที่หน้า settlements.html แสดง (<see cref="SettlementReferenceCatalog"/>) · ไม่พบ = ชื่อ enum</summary>
    private static readonly SettlementReferenceData Cat = SettlementReferenceCatalog.Build();
    private static string Label(IReadOnlyList<SettlementEnumOption> options, string value)
        => options.FirstOrDefault(o => o.Value == value)?.Label ?? value;
    private static string YesNo(bool b) => b ? "ใช่" : "ไม่ใช่";

    public static IReadOnlyList<TabularColumn<LodgingReservationListItem>> LodgingReservations { get; } = new List<TabularColumn<LodgingReservationListItem>>
    {
        new("เลขที่การจอง", r => r.ReservationNumber),
        new("สถานะ", r => r.Status.ToString()),
        new("ช่องทาง", r => r.Source.ToString()),
        new("ชื่อแขก", r => r.GuestName),
        new("โทรศัพท์", r => r.GuestPhone),
        new("เช็คอิน", r => r.CheckInDate, K.Date),
        new("เช็คเอาต์", r => r.CheckOutDate, K.Date),
        new("จำนวนคืน", r => r.Nights, K.Integer),
        new("จำนวนห้อง", r => r.RoomCount, K.Integer),
        new("ประเภทห้อง", r => r.RoomSummary),
        new("เลขห้อง", r => r.UnitNumbers),
        new("ยอดค่าห้อง", r => r.TotalAmount, K.Money),
        new("ยอดบริการเพิ่ม (folio)", r => r.FolioTotal, K.Money),
        new("รับชำระแล้ว", r => r.PaidAmount, K.Money),
        new("ยอดค้างชำระ", r => r.BalanceDue, K.Money),
        new("มัดจำที่ต้องชำระ", r => r.DepositRequired, K.Money),
        new("ยอดต้องคืนแขก", r => r.RefundPending, K.Money),
        new("หมดเวลากันห้อง", r => r.HoldExpiresAt, K.Timestamp),
        new("มีสลิป", r => YesNo(r.HasSlip)),
        new("สถานะสลิป", r => r.SlipStateLabel),
        new("ค้างปิด", r => r.OverdueLabel),
        new("เงินออนไลน์มีปัญหา", r => YesNo(r.HasPaymentProblem)),
        new("สร้างเมื่อ", r => r.CreatedAt, K.Timestamp),
    };

    public static IReadOnlyList<TabularColumn<SettlementBatchView>> SettlementBatches { get; } = new List<TabularColumn<SettlementBatchView>>
    {
        new("ช่องทาง", b => b.ChannelName),
        new("เลขอ้างอิงรอบโอน", b => b.PayoutRef),
        new("วันที่โอน", b => b.PayoutDate, K.Date),
        new("งวดตั้งแต่", b => b.PeriodFrom, K.Date),
        new("งวดถึง", b => b.PeriodTo, K.Date),
        new("สกุลเงิน", b => b.Currency),
        new("ยอดโอนสุทธิ", b => b.NetPayout, K.Money),
        new("ยอดรวมบรรทัด", b => b.LinesTotal, K.Money),
        new("ยอดกระเป๋าต้นงวด", b => b.OpeningWalletBalance, K.Money),
        new("ยอดกระเป๋าปลายงวด", b => b.ClosingWalletBalance, K.Money),
        new("สถานะ", b => Label(Cat.BatchStatuses, b.Status.ToString())),
        new("ที่มา", b => Label(Cat.SourceKinds, b.SourceKind.ToString())),
        new("จำนวนบรรทัด", b => b.LineCount, K.Integer),
        new("ยังไม่จัดประเภท", b => b.UnclassifiedCount, K.Integer),
        new("รอตัดสินจับคู่", b => b.NeedsMatchDecisionCount, K.Integer),
        new("หมายเหตุ", b => b.Note),
        new("นำเข้าเมื่อ", b => b.CreatedAt, K.Timestamp),
    };

    /// <summary>หนึ่งแถวของไฟล์บรรทัดรอบโอน (แบนแล้ว + หัวรอบ — รวมหลายรอบในชีตเดียวแล้วยังรู้ว่ามาจากรอบไหน) · แถวรวมใช้ชนิดเดียวกัน</summary>
    public sealed record SettlementLineRow(string Channel, string PayoutRef, int? Seq, string? LineType, string? RawType, string? Description,
        DateTime? TxnDate, string? OrderId, string? TxnId, decimal Amount, decimal? Vat, decimal? Wht,
        string? ClassifiedBy, string? UsedAi, string? MatchStatus, string? MatchNote, string? AdjustmentReason)
    {
        public static SettlementLineRow From(SettlementBatchView b, SettlementLineView l) => new(
            b.ChannelName, b.PayoutRef, l.Seq, l.LineTypeLabel, l.RawTypeLabel, l.Description, l.TxnDate, l.ExternalOrderId, l.ExternalTxnId,
            l.Amount, l.VatAmount, l.WhtAmount, Label(Cat.ClassifiedBy, l.ClassifiedBy.ToString()), YesNo(l.ClassifyUsedAi),
            Label(Cat.MatchStatuses, l.MatchStatus.ToString()), l.MatchNote, l.AdjustmentReason);

        /// <summary>แถวรวมท้ายไฟล์ — รวมจากบรรทัดที่ออกไฟล์ + ยอดโอนสุทธิของหัวรอบไว้เทียบ</summary>
        public static SettlementLineRow Totals(SettlementBatchView b) => new(
            "รวม", b.PayoutRef, null, $"{b.Lines.Count} บรรทัด", null,
            $"ยอดโอนสุทธิหัวรอบ {b.NetPayout.ToString("0.00", CultureInfo.InvariantCulture)}", null, null, null,
            b.Lines.Sum(l => l.Amount), b.Lines.Sum(l => l.VatAmount ?? 0m), b.Lines.Sum(l => l.WhtAmount ?? 0m),
            null, null, null, null, null);
    }

    public static IReadOnlyList<TabularColumn<SettlementLineRow>> SettlementLines { get; } = new List<TabularColumn<SettlementLineRow>>
    {
        new("ช่องทาง", r => r.Channel),
        new("เลขอ้างอิงรอบโอน", r => r.PayoutRef),
        new("ลำดับ", r => r.Seq, K.Integer),
        new("ประเภท", r => r.LineType),
        new("ประเภทในไฟล์", r => r.RawType),
        new("รายละเอียด", r => r.Description),
        new("วันที่รายการ", r => r.TxnDate, K.Date),
        new("เลขคำสั่งซื้อ", r => r.OrderId),
        new("เลขธุรกรรม", r => r.TxnId),
        new("จำนวนเงิน", r => r.Amount, K.Money),
        new("VAT", r => r.Vat, K.Money),
        new("หัก ณ ที่จ่าย", r => r.Wht, K.Money),
        new("จัดประเภทโดย", r => r.ClassifiedBy),
        new("ใช้ AI", r => r.UsedAi),
        new("สถานะจับคู่", r => r.MatchStatus),
        new("หมายเหตุจับคู่", r => r.MatchNote),
        new("เหตุผลปรับปรุง", r => r.AdjustmentReason),
    };

    /// <summary>ออเดอร์ POS หนึ่งแถวต่อบิล — ยอดทุกชั้น (ก่อนส่วนลด → สุทธิ) + วิธีชำระรวมเป็นข้อความ · ใบกำกับอย่างย่อ/ใบเต็มที่ออกแล้ว</summary>
    public static IReadOnlyList<TabularColumn<OrderResponse>> PosOrders { get; } = new List<TabularColumn<OrderResponse>>
    {
        new("เลขที่ออเดอร์", o => o.OrderNumber),
        new("วันเวลา", o => o.CreatedAt, K.Timestamp),
        new("ปิดบิลเมื่อ", o => o.CompletedAt, K.Timestamp),
        new("ประเภท", o => o.OrderType.ToString()),
        new("สถานะ", o => o.Status.ToString()),
        new("ลูกค้า", o => o.CustomerName),
        new("โต๊ะ", o => o.TableNumber),
        new("จำนวนรายการ", o => o.Items.Count, K.Integer),
        new("ยอดก่อนส่วนลด", o => o.SubTotal, K.Money),
        new("ส่วนลด", o => o.DiscountAmount, K.Money),
        new("ส่วนลดคูปอง", o => o.CouponDiscountAmount, K.Money),
        new("ค่าบริการ", o => o.ServiceChargeAmount, K.Money),
        new("VAT", o => o.VatAmount, K.Money),
        new("ยอดรวม", o => o.TotalAmount, K.Money),
        new("ปัดเศษ", o => o.RoundingAmount, K.Money),
        new("ยอดสุทธิ", o => o.NetAmount, K.Money),
        new("ทิป", o => o.TipAmount, K.Money),
        new("ชำระโดย", o => string.Join(" | ", o.Payments.Select(p =>
            $"{p.PaymentMethod} {p.Amount.ToString("0.00", CultureInfo.InvariantCulture)}"))),
        new("เลขใบกำกับอย่างย่อ", o => o.AbbreviatedInvoiceNumber),
        new("สาขาผู้ออก", o => o.IssuerBranchLabel),
        new("เลขเอกสาร (ใบกำกับเต็ม)", o => o.DocumentNumber),
        new("คูปอง", o => o.CouponCode),
        new("อ้างอิง", o => o.Reference),
        new("หมายเหตุ", o => o.Notes),
    };
}
