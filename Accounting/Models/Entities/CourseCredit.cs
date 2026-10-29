using Accounting.Models.Enums;

namespace Accounting.Models.Enums
{
    /// <summary>หน่วยเครดิตของแพ็กเกจ — ครั้ง (Class Credits) หรือ ชั่วโมง (Hour Pass)</summary>
    public enum CreditUnit { Session = 0, Hour = 1 }

    /// <summary>รูปแบบคลาส — ตัวต่อตัว / กลุ่ม (capacity ต่อรอบ) / ใช้ได้ทั้งสองแบบ</summary>
    public enum CreditClassFormat { Any = 0, OneOnOne = 1, Group = 2 }

    /// <summary>วิธีกำหนดครูฝึก — ลูกค้าเลือกเอง / ระบบกำหนดให้ (ภาระน้อยสุดที่ว่าง)</summary>
    public enum TrainerAssignmentMode { CustomerChooses = 0, SystemAssigns = 1 }

    /// <summary>Transferred (รอบ 209 · P5 · คำตัดสินข้อ 168 ฎ) = โอนให้ลูกค้าคนอื่นแล้ว — ยอดที่เหลือย้ายไปบัตรปลายทาง (<c>TransferredToPassId</c>)</summary>
    public enum CreditPassStatus { Active = 0, Exhausted = 1, Expired = 2, Cancelled = 3, Transferred = 4 }

    /// <summary>ชนิดแพ็กเกจ (รอบ 209 · P5 · R-209-C5) — Credits = นับครั้ง/ชั่วโมง (เดิม) · UnlimitedPeriod = ไม่จำกัดครั้งตามช่วงเวลา
    /// (รับรู้รายได้เส้นตรงรายวัน — <c>CourseCreditMath.PeriodRecognitionPlan</c>) · ค่าเริ่มต้น 0 = พฤติกรรมเดิม</summary>
    public enum CreditPackageKind { Credits = 0, UnlimitedPeriod = 1 }

    /// <summary>ช่องทางที่ขายคอร์ส (ร่องรอย · ไม่มีผลต่อการลงบัญชี — บัญชีตามเอกสารขาย)</summary>
    public enum CreditSaleChannel { BackOffice = 0, Pos = 1, Online = 2, ExistingDocument = 3 }

    public enum CreditSessionStatus { Scheduled = 0, Cancelled = 1, Completed = 2 }

    /// <summary>สถานะการจองด้วยเครดิต — Booked = กันเครดิตไว้ (ยังไม่รับรู้รายได้) · Attended/NoShow/LateCancelled = ใช้เครดิตแล้ว
    /// (รับรู้รายได้) · Cancelled = คืนเครดิต</summary>
    public enum CreditBookingStatus { Booked = 0, Attended = 1, NoShow = 2, Cancelled = 3, LateCancelled = 4 }

    /// <summary>Adjust (รอบ 205 W6 · R-38) = ลดเครดิตจากใบลดหนี้คืนเงินบางส่วน — บัตรยัง Active</summary>
    /// <summary>AdjustReversal (รอบ 208) = ยกเลิกใบลดหนี้คืนเงินบางส่วน ⇒ คืนเครดิตที่ลดไป + ถอนยอดคืน</summary>
    /// <summary>รอบ 209 (P5): PeriodRecognize = รับรู้รายได้สมาชิกไม่จำกัดตามวัน · Freeze = พักบัตร · Extend = ขยายอายุ (ไม่มีเงิน) ·
    /// TransferOut / TransferIn = โอนบัตร (ไม่มี JE — หนี้สินย้ายเจ้าของ ไม่ย้ายผัง)</summary>
    public enum CreditLedgerKind { Purchase = 0, Reserve = 1, Release = 2, Consume = 3, Expire = 4, Cancel = 5, Adjust = 6, AdjustReversal = 7,
        PeriodRecognize = 8, Freeze = 9, Extend = 10, TransferOut = 11, TransferIn = 12 }

    public enum CreditCancelOutcome { Refund = 0, Forfeit = 1 }
}

namespace Accounting.Models.Entities
{
    /// <summary>แพ็กเกจคอร์ส/เครดิต (นิยาม) — เช่น "พิลาทิส 10 ครั้ง 60 วัน 4,500฿" · ค่าที่มีผลต่อบัตรที่ขายแล้ว
    /// (จำนวน/อายุ/นโยบายยกเลิก/ผังบัญชี) ถูก<b>ตรึงลงบัตร</b>ตอนขาย ⇒ แก้แพ็กเกจภายหลังไม่ย้อนเปลี่ยนสัญญาเดิม</summary>
    public class CreditPackage : TenantEntity
    {
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Description { get; set; }
        public CreditUnit Unit { get; set; } = CreditUnit.Session;
        /// <summary>จำนวนเครดิต (ครั้ง หรือ ชั่วโมง)</summary>
        public decimal CreditQuantity { get; set; }
        /// <summary>อายุใช้งานเป็นวันนับจากวันซื้อ · 0 = ไม่หมดอายุ</summary>
        public int ValidityDays { get; set; }
        public decimal Price { get; set; }
        public bool PriceIncludesVat { get; set; } = true;
        /// <summary>7 · 0 (§80/1) · -1 (ยกเว้น §81 — ออกใบเสร็จ ไม่ออกใบกำกับ)</summary>
        public decimal VatRate { get; set; } = 7m;
        public CreditClassFormat ClassFormat { get; set; } = CreditClassFormat.Any;
        public TrainerAssignmentMode TrainerMode { get; set; } = TrainerAssignmentMode.CustomerChooses;
        /// <summary>บริการที่ใช้ได้ (Guid ของ <c>SiteBookingService</c> คั่นด้วย ,) · ว่าง = ทุกบริการ</summary>
        public string? AllowedServiceIds { get; set; }
        /// <summary>ครูฝึกที่สอนแพ็กเกจนี้ (Guid ของ <c>Employee</c> คั่นด้วย , ตามลำดับความชอบ)</summary>
        public string? TrainerEmployeeIds { get; set; }
        /// <summary>ยกเลิกก่อนเวลาเรียนอย่างน้อยกี่ชั่วโมงจึงคืนเครดิต</summary>
        public int FreeCancelHours { get; set; } = 24;
        public Guid? UnearnedAccountId { get; set; }
        public Guid? RevenueAccountId { get; set; }
        public bool IsActive { get; set; } = true;
        public int SortOrder { get; set; }

        // ── รอบ 209 · P5 (R-209-C5 · คำตัดสินข้อ 168 ฎ) — ค่าเริ่มต้น = พฤติกรรมเดิม ──
        /// <summary>ชนิดแพ็กเกจ · UnlimitedPeriod ⇒ <see cref="ValidityDays"/> = จำนวนวันของสมาชิก (ต้อง &gt; 0) · <see cref="CreditQuantity"/> ไม่มีผล</summary>
        public CreditPackageKind Kind { get; set; } = CreditPackageKind.Credits;
        /// <summary>สมาชิกไม่จำกัด: จองได้สูงสุดกี่ครั้งต่อวัน (ตั้งต้น 1)</summary>
        public int MaxBookingsPerDay { get; set; } = 1;
        /// <summary>สมาชิกไม่จำกัด: จองค้างพร้อมกันได้สูงสุดกี่รายการ · 0 = ไม่จำกัด</summary>
        public int MaxActiveBookings { get; set; }
        /// <summary>พักบัตร (freeze) ได้รวมสูงสุดกี่วันต่อบัตร · 0 = ไม่อนุญาตพักบัตร</summary>
        public int MaxFreezeDays { get; set; }
        /// <summary>แจ้งเตือนลูกค้าก่อนบัตรหมดอายุกี่วัน (อีเมล → LINE ผ่าน <c>IBookingNotifier</c>) · 0 = ไม่แจ้ง (เดิม)</summary>
        public int ExpiryReminderDays { get; set; }
    }

    /// <summary>บัตรคอร์สของลูกค้า 1 ใบ (1 การซื้อ) — ยอดคงเหลือ = ทั้งหมด − ใช้แล้ว − ถูกจอง ·
    /// ฝั่งเงิน: <see cref="ContractAmount"/> (สุทธิก่อน VAT ที่ลงหนี้สินจริงจากใบขาย) − <see cref="RecognizedAmount"/> = ยอดค้างในหนี้สิน</summary>
    public class CreditPass : TenantEntity
    {
        public string PassNumber { get; set; } = "";
        public Guid PackageId { get; set; }
        public CreditPackage Package { get; set; } = null!;
        public Guid ContactId { get; set; }
        public string PackageName { get; set; } = "";
        public CreditUnit Unit { get; set; }
        public CreditClassFormat ClassFormat { get; set; }
        public string? AllowedServiceIds { get; set; }
        public int FreeCancelHours { get; set; }
        public DateTime PurchasedAt { get; set; } = DateTime.UtcNow;
        /// <summary>วันสุดท้ายที่ใช้ได้ (วันปฏิทินไทย · <c>ThaiDate.CalendarDateUtc</c>) · null = ไม่หมดอายุ</summary>
        public DateTime? ExpiresOn { get; set; }
        public decimal CreditsTotal { get; set; }
        public decimal CreditsUsed { get; set; }
        public decimal CreditsReserved { get; set; }
        public decimal ContractAmount { get; set; }
        public decimal RecognizedAmount { get; set; }
        /// <summary>ยอดสุทธิที่คืนลูกค้าผ่านใบลดหนี้ (ปิดบัตร) — ค้างในหนี้สิน = สัญญา − รับรู้ − คืน</summary>
        public decimal RefundedAmount { get; set; }
        /// <summary>ใบลดหนี้/การยกเลิกใบขายที่ปิดบัตรนี้ (ด่าน <c>CreditPassDocumentRule</c>)</summary>
        public Guid? CancelledByDocumentId { get; set; }
        public Guid UnearnedAccountId { get; set; }
        public Guid RevenueAccountId { get; set; }
        public CreditPassStatus Status { get; set; } = CreditPassStatus.Active;
        public CreditSaleChannel SaleChannel { get; set; }
        public Guid? SaleDocumentId { get; set; }
        public string? SaleDocumentNumber { get; set; }
        public DateTime? ClosedAt { get; set; }
        public string? Notes { get; set; }

        // ── รอบ 209 · P5 (R-209-C5) — ตรึงจากแพ็กเกจตอนเปิดบัตร · บัตรเดิม = ค่าเริ่มต้น (Credits) ทุกตัว ──
        public CreditPackageKind Kind { get; set; } = CreditPackageKind.Credits;
        public int MaxBookingsPerDay { get; set; } = 1;
        public int MaxActiveBookings { get; set; }
        public int MaxFreezeDays { get; set; }
        /// <summary>สมาชิกไม่จำกัด: จำนวน "วันใช้งาน" ทั้งหมดที่รับรู้รายได้ (วันพักไม่นับ · ขยายอายุ = บวกเพิ่ม)</summary>
        public int PeriodDays { get; set; }
        /// <summary>วันแรกของช่วงสมาชิก (วันปฏิทินไทย)</summary>
        public DateTime? PeriodStartsOn { get; set; }
        /// <summary>จุดตั้งฐานของสูตรเส้นตรง (หลังขยายอายุ) — รับรู้แล้ว ณ ฐาน · วันใช้งานที่ผ่านไปแล้ว ณ ฐาน (บัตรที่ไม่เคยขยาย = 0/0)</summary>
        public decimal PeriodBaseRecognized { get; set; }
        public int PeriodBaseActiveDays { get; set; }
        /// <summary>รับรู้รายได้ถึงวันที่ (รวมวันนั้น) · null = ยังไม่เคยรับรู้</summary>
        public DateTime? RecognizedThroughDate { get; set; }
        /// <summary>วันพักรวมที่ใช้ไปแล้ว (เทียบเพดาน <see cref="MaxFreezeDays"/>)</summary>
        public int FrozenDays { get; set; }
        public Guid? TransferredFromPassId { get; set; }
        public Guid? TransferredToPassId { get; set; }
        /// <summary>ยอดค่าบริการรับล่วงหน้าที่ย้ายไปบัตรปลายทางตอนโอน (บัตรเดิม) — ค้างในหนี้สินของบัตรนี้ = สัญญา − รับรู้ − คืน − โอนออก</summary>
        public decimal TransferredOutAmount { get; set; }
        /// <summary>แจ้งเตือนใกล้หมดอายุ "ส่งถึงจริง" เมื่อไร · null = ยังไม่ส่ง (F2 #3 — ประทับหลังช่องทางตอบสำเร็จเท่านั้น)</summary>
        public DateTime? ExpiryNotifiedAt { get; set; }
        public DateTime? ExpiryNotifyAttemptedAt { get; set; }
        /// <summary>ร่องรอยส่งล้มครั้งล่าสุด (พนักงานเห็นบนหน้าบัตร) · null = สำเร็จ/ยังไม่เคยส่ง</summary>
        public string? ExpiryNotifyError { get; set; }
    }

    /// <summary>พักบัตร (freeze · รอบ 209 P5 · คำตัดสินข้อ 168 ฎ) — ช่วงวันรวมหัวท้าย · ระหว่างพัก: จองไม่ได้ · สมาชิกไม่จำกัดไม่รับรู้รายได้ ·
    /// วันหมดอายุเลื่อนออกเท่าจำนวนวัน · ไม่มีเงิน (21713 คงเดิม) · ยกเลิกพักที่ยังไม่เริ่มได้ (เลื่อนวันหมดอายุกลับ)</summary>
    public class CreditPassFreeze : TenantEntity
    {
        public Guid PassId { get; set; }
        public DateTime FromDate { get; set; }
        public DateTime ToDate { get; set; }
        public int Days { get; set; }
        public string Reason { get; set; } = "";
        public DateTime? CancelledAt { get; set; }
    }

    /// <summary>สมุดเครดิต (append-only) — ทุกการขยับเครดิต/รายได้ของบัตร · JE ที่ผูก = ใบสำคัญรับรู้รายได้ของแถวนั้น</summary>
    public class CreditLedgerEntry : TenantEntity
    {
        public Guid PassId { get; set; }
        public CreditLedgerKind Kind { get; set; }
        /// <summary>เครดิตที่ขยับ (บวกเสมอ — ทิศดูจาก Kind)</summary>
        public decimal Credits { get; set; }
        /// <summary>รายได้ที่รับรู้ในแถวนี้ (Consume/Expire) · 0 = ไม่มีผลทางบัญชี</summary>
        public decimal RecognizedAmount { get; set; }
        public Guid? BookingId { get; set; }
        public Guid? JournalEntryId { get; set; }
        /// <summary>รอบ 208 (ฝ่ายค้าน W6 ข้อ 2) — เอกสารที่ทำให้เกิดแถวนี้ (ใบลดหนี้คืนเงินบางส่วนของแถว Adjust) · ยกเลิกใบลดหนี้ ⇒ หาแถวนี้เพื่อคืนเครดิต/ยอดคืน ·
        /// null = แถวที่ไม่ได้มาจากเอกสาร / แถวเก่าก่อนรอบ 208</summary>
        public Guid? DocumentId { get; set; }
        public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
        public string? Note { get; set; }
    }

    /// <summary>รอบเรียน (คลาส) — กลุ่ม: ร้านตั้งรอบล่วงหน้า + capacity · ตัวต่อตัว: capacity 1 (สร้างตอนจองได้) ·
    /// ผูก <c>SiteBookingService</c> ได้ (บริการเดียวกับเครื่องจองของ CMS) แต่<b>ไม่ได้ใช้ตารางช่องเวลาของเครื่องนั้น</b> —
    /// เครื่องจองเรียก <c>ICreditBookingLedger</c> แทน (ดู COURSE_CREDIT_DESIGN §4)</summary>
    public class CreditClassSession : TenantEntity
    {
        public string Title { get; set; } = "";
        public Guid? BookingServiceId { get; set; }
        public CreditClassFormat Format { get; set; } = CreditClassFormat.Group;
        public DateTime StartAt { get; set; }
        public DateTime EndAt { get; set; }
        public int Capacity { get; set; } = 1;
        public Guid? TrainerEmployeeId { get; set; }
        /// <summary>เครดิตต่อคน (หน่วย "ครั้ง") — หน่วยชั่วโมงคิดจากระยะเวลารอบ</summary>
        public decimal CreditCost { get; set; } = 1m;
        public CreditSessionStatus Status { get; set; } = CreditSessionStatus.Scheduled;
        public string? Notes { get; set; }
    }

    /// <summary>การจองด้วยเครดิต — ผูกรอบเรียนของโมดูลนี้ (<see cref="SessionId"/>) หรือการจองจากเครื่องจองภายนอก
    /// (<see cref="ExternalRef"/> เช่น "SiteBooking:{id}") อย่างใดอย่างหนึ่ง</summary>
    public class CreditClassBooking : TenantEntity
    {
        public Guid? SessionId { get; set; }
        public string? ExternalRef { get; set; }
        public Guid PassId { get; set; }
        public Guid ContactId { get; set; }
        public Guid? TrainerEmployeeId { get; set; }
        public DateTime StartAt { get; set; }
        public decimal Credits { get; set; }
        public CreditBookingStatus Status { get; set; } = CreditBookingStatus.Booked;
        public DateTime BookedAt { get; set; } = DateTime.UtcNow;
        public DateTime? SettledAt { get; set; }
        public decimal RecognizedAmount { get; set; }
        public string? Note { get; set; }
    }
}
