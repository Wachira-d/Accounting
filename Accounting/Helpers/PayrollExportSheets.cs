using Accounting.Models.DTOs.Payroll;
using K = Accounting.Helpers.TabularCellKind;

namespace Accounting.Helpers;

/// <summary>
/// **คอลัมน์ไฟล์ส่งออกของโมดูลเงินเดือน/HR** (X4 · 2026-10-14) — ตัวตั้งตัวเดียวของหัวคอลัมน์ + ค่าที่ออกไฟล์ (ตัวเขียน = <see cref="TabularExport"/>)
///
/// <para>ทุกตัวรับ DTO ตัวเดียวกับที่หน้าจอแสดง (ไม่คิวรีซ้ำ/ไม่คำนวณยอดใหม่) ⇒ ตัวเลขในไฟล์ = ตัวเลขบนจอ ·
/// PII (เลขบัตร/เลขภาษี/เลขบัญชี/โทร/อีเมล) ปิดบังมาแล้วจาก service ตามสิทธิ์ <c>Pii.View</c> ของผู้เรียก ·
/// เงินเดือนฐานว่างเมื่อไม่มีสิทธิ์ดูเงินเดือน (DTO ส่ง 0 มา — ในไฟล์ต้องไม่อ่านว่า "เงินเดือน 0 บาท")</para>
///
/// <para><b>ไม่ใช่</b>ไฟล์ยื่นแบบ (ภ.ง.ด.1/1ก · สปส.1-10 เป็นของ <c>TaxFilingExportService</c>) — เป็นรายการปฏิบัติงานภายใน ⇒ วันที่ ISO ค.ศ.</para>
/// </summary>
public static class PayrollExportSheets
{
    private static string YesNo(bool b) => b ? "ใช่" : "ไม่ใช่";

    /// <summary>ทะเบียนพนักงาน — คอลัมน์ทั้งหมดที่หน้าพนักงานแสดง + ที่ต้องใช้กระทบยอด/นำเข้ากลับ</summary>
    public static IReadOnlyList<TabularColumn<EmployeeResponse>> Employees(bool canViewSalary) => new List<TabularColumn<EmployeeResponse>>
    {
        new("รหัสพนักงาน", e => e.EmployeeCode),
        new("คำนำหน้า", e => e.TitleTh),
        new("ชื่อ", e => e.FirstNameTh),
        new("นามสกุล", e => e.LastNameTh),
        new("ชื่อ (อังกฤษ)", e => e.FirstNameEn),
        new("นามสกุล (อังกฤษ)", e => e.LastNameEn),
        new("เลขบัตรประชาชน", e => e.CitizenId),
        new("เลขประจำตัวผู้เสียภาษี", e => e.TaxId),
        new("เลขประกันสังคม", e => e.SocialSecurityNumber),
        new("ผู้ประกันตน ม.33", e => YesNo(e.IsSubjectToSocialSecurity)),
        new("แผนก", e => e.DepartmentName ?? e.Department),
        new("ตำแหน่ง", e => e.PositionTitle ?? e.Position),
        new("หัวหน้างาน", e => e.DirectManagerName),
        new("ประเภทการจ้าง", e => e.EmploymentType),
        new("วันเริ่มงาน", e => e.StartDate, K.Date),
        new("วันสิ้นสุด", e => e.EndDate, K.Date),
        new("ฐานเงินเดือน", e => canViewSalary ? (object?)e.BaseSalary : null, K.Money),
        new("ประเภทเงินเดือน", e => e.SalaryType),
        new("Cost behavior", e => e.CostBehavior),
        new("ธนาคาร", e => e.BankName),
        new("เลขที่บัญชี", e => e.BankAccountNumber),
        new("ชื่อบัญชี", e => e.BankAccountName),
        new("โทรศัพท์", e => e.Phone),
        new("อีเมล", e => e.Email),
        new("สถานะ", e => e.IsActive ? "ทำงานอยู่" : "พ้นสภาพ/ปิดใช้"),
        new("ระบบภายนอก", e => e.ExternalSystem),
        new("รหัสภายนอก", e => e.ExternalId),
    };

    /// <summary>รายการรอบเงินเดือน — ทุกรอบ (ไม่ใช่แค่หน้า 20 รอบบนจอ)</summary>
    public static IReadOnlyList<TabularColumn<PayrollRunResponse>> Runs { get; } = new List<TabularColumn<PayrollRunResponse>>
    {
        new("เลขที่รอบ", r => r.PayrollNumber),
        new("ชื่อรอบ", r => r.Name),
        new("ปี (ค.ศ.)", r => r.Year, K.Integer),
        new("เดือน", r => r.Month, K.Integer),
        new("วันจ่าย", r => r.PayDate, K.Date),
        new("สถานะ", r => r.Status),
        new("จำนวนพนักงาน", r => r.EmployeeCount, K.Integer),
        new("รายได้รวม", r => r.TotalGrossSalary, K.Money),
        new("รายการหักรวม", r => r.TotalDeductions, K.Money),
        new("ภาษีหัก ณ ที่จ่าย", r => r.TotalWithholdingTax, K.Money),
        new("ประกันสังคมลูกจ้าง", r => r.TotalSocialSecurityEmployee, K.Money),
        new("ประกันสังคมนายจ้าง", r => r.TotalSocialSecurityEmployer, K.Money),
        new("เงินทดแทน", r => r.TotalWorkersCompensation, K.Money),
        new("ยอดจ่ายสุทธิ", r => r.TotalNetPay, K.Money),
        new("จ่ายจริงเมื่อ", r => r.PaidAt, K.Timestamp),
        new("นำส่ง ปกส. เมื่อ", r => r.SsoSettledAt, K.Timestamp),
        new("เลขรับ สปส.1-10", r => r.SsoFilingNumber),
        new("เงินเพิ่ม ปกส.", r => r.SsoLateFeeAmount, K.Money),
        new("ที่มา", r => r.ExternalSystem ?? "สร้างในระบบ"),
        new("อ้างอิงภายนอก", r => r.ExternalRunRef),
        new("สร้างเมื่อ", r => r.CreatedAt, K.Timestamp),
    };

    /// <summary>ทะเบียนเงินเดือนรายคนของรอบ (payslip register) — ทุกช่องรายได้/รายการหัก + ฝั่งนายจ้าง + เงินโอนจริง</summary>
    public static IReadOnlyList<TabularColumn<PayrollRunLineDto>> Register(PayrollRunResponse run, string? defaultSourceLabel)
        => new List<TabularColumn<PayrollRunLineDto>>
    {
        new("รหัสพนักงาน", l => l.EmployeeCode),
        new("ชื่อพนักงาน", l => l.EmployeeName),
        new("เงินเดือน", l => l.BaseSalary, K.Money),
        new("ค่าล่วงเวลา", l => l.OvertimePay, K.Money),
        new("เบี้ยเลี้ยง/ค่าครองชีพ", l => l.Allowances, K.Money),
        new("คอมมิชชั่น", l => l.Commission, K.Money),
        new("โบนัส", l => l.Bonus, K.Money),
        new("รายได้อื่น", l => l.OtherIncome, K.Money),
        new("รวมรายได้", l => l.GrossIncome, K.Money),
        new("ฐานค่าจ้าง ปกส.", l => l.SocialSecurityBase, K.Money),
        new("ประกันสังคมลูกจ้าง", l => l.SocialSecurityEmployee, K.Money),
        new("ภาษีหัก ณ ที่จ่าย", l => l.WithholdingTax, K.Money),
        new("กองทุนสำรองฯ ลูกจ้าง", l => l.ProvidentFundEmployee, K.Money),
        new("หักเงินกู้", l => l.LoanDeduction, K.Money),
        new("หักอื่น", l => l.OtherDeductions, K.Money),
        new("รวมรายการหัก", l => l.TotalDeductions, K.Money),
        new("เงินได้สุทธิ", l => l.NetPay, K.Money),
        new("หักคืนเงินทดรอง (ตอนจ่าย)", l => l.AdvanceRecovered, K.Money),
        new("เงินโอนจริง", l => PayrollBankTransfer.CashPaid(run.Status, l.NetPay, l.AdvanceRecovered), K.Money),
        new("ประกันสังคมนายจ้าง", l => l.SocialSecurityEmployer, K.Money),
        new("กองทุนสำรองฯ นายจ้าง", l => l.ProvidentFundEmployer, K.Money),
        new("เงินทดแทน (นายจ้าง)", l => l.WorkersCompensation, K.Money),
        new("แหล่งจ่าย", l => l.EmployeeId == Guid.Empty ? null : l.NetPaymentAccountLabel ?? defaultSourceLabel),
        new("คำเตือน", l => string.Join(" · ", new[] { l.SsoFlagWarning, l.NetPaymentAccountWarning }.Where(w => !string.IsNullOrWhiteSpace(w)))),
    };

    /// <summary>แถวรวมท้ายทะเบียน (แถวเทียมชนิดเดียวกัน — ผ่านคอลัมน์ชุดเดียวกับแถวจริง) · รวมจากแถวที่ออกไฟล์ (ไม่ใช่ยอดหัวรอบ)
    /// ⇒ ถ้าไม่ตรงหัวรอบจะเห็นในไฟล์ · <c>EmployeeId = Guid.Empty</c> = แถวรวม</summary>
    public static PayrollRunLineDto RegisterTotals(IReadOnlyList<PayrollRunLineDto> lines)
    {
        decimal S(Func<PayrollRunLineDto, decimal> f) => lines.Sum(f);
        return new PayrollRunLineDto(Guid.Empty, $"{lines.Count} คน", "รวม",
            S(l => l.BaseSalary), S(l => l.OvertimePay), S(l => l.Allowances), S(l => l.Commission), S(l => l.Bonus),
            S(l => l.OtherIncome), S(l => l.GrossIncome), S(l => l.SocialSecurityBase), S(l => l.SocialSecurityEmployee),
            S(l => l.SocialSecurityEmployer), S(l => l.WithholdingTax), S(l => l.ProvidentFundEmployee), S(l => l.LoanDeduction),
            S(l => l.OtherDeductions), S(l => l.TotalDeductions), S(l => l.NetPay),
            ProvidentFundEmployer: S(l => l.ProvidentFundEmployer),
            WorkersCompensation: S(l => l.WorkersCompensation),
            AdvanceRecovered: S(l => l.AdvanceRecovered));
    }

    /// <summary>คำขอลา (แท็บลาหยุดของหน้าเงินเดือน) — ทุกคำขอของปีที่เลือก</summary>
    public static IReadOnlyList<TabularColumn<LeaveResponse>> Leaves { get; } = new List<TabularColumn<LeaveResponse>>
    {
        new("พนักงาน", l => l.EmployeeName),
        new("ประเภทลา", l => l.LeaveType),
        new("วันเริ่ม", l => l.StartDate, K.Date),
        new("วันสิ้นสุด", l => l.EndDate, K.Date),
        new("ครึ่งวัน", l => l.HalfDayMarker switch { 1 => "ช่วงเช้า", 2 => "ช่วงบ่าย", _ => "" }),
        new("จำนวนวัน", l => l.TotalDays, K.Number),
        new("เหตุผล", l => l.Reason),
        new("สถานะ", l => l.Status),
        new("ผู้อนุมัติ", l => l.ApprovedBy),
        new("เหตุผลที่ปฏิเสธ", l => l.RejectionReason),
        new("ยื่นเมื่อ", l => l.CreatedAt, K.Timestamp),
    };

    /// <summary>หนึ่งแถวของไฟล์รายการโอน (ข้อมูลดิบ + ผลตัดสิน)</summary>
    public sealed record BankTransferRow(int Seq, PayrollBankTransferSheet Sheet, PayrollBankTransferLine Line, PayrollBankTransferRow Decision);

    public static IReadOnlyList<BankTransferRow> BankTransferRows(PayrollBankTransferSheet sheet)
        => sheet.Lines.Select((l, i) => new BankTransferRow(i + 1, sheet, l,
            PayrollBankTransfer.Evaluate(sheet.Status, l.NetPay, l.AdvanceRecovered, l.HasOutstandingAdvance, l.BankAccountNumber,
                l.PaymentSourceIsBank))).ToList();

    /// <summary>
    /// ไฟล์รายการโอนเงินเดือน<b>รูปแบบกลาง</b> (ไม่ใช่รูปแบบเฉพาะธนาคาร) — เลขบัญชีเป็นเซลล์ข้อความตัวเลขล้วน: ในไฟล์ <b>Excel (.xlsx)</b> 0 นำหน้าคงอยู่ ·
    /// ไฟล์ <b>CSV</b> เก็บ 0 ครบ แต่<b>ถ้าเปิดด้วย Excel ตรง ๆ Excel จะตัด 0 นำหน้า</b> (ข้อจำกัดของ Excel — ฝ่ายค้านรอบส่งออก 2026-10-15 P2-5:
    /// คอมเมนต์เดิมอ้างว่า "Excel ไม่ตัด" ซึ่งจริงเฉพาะ .xlsx) ⇒ หน้าเว็บให้ปุ่ม Excel เป็นหลัก + คำเตือนบนปุ่ม CSV ·
    /// แถวที่แหล่งจ่ายไม่ใช่ธนาคาร = "จ่ายจากแหล่งอื่น" (ไม่พร้อมโอน · P2-4) · แถวเรียงตามแหล่งจ่าย (บัญชีธนาคารที่จ่าย) แล้วตามรหัสพนักงาน ·
    /// ยอดไม่มีตัวคั่นหลักพัน · อ้างอิง = เลขที่รอบ-รหัสพนักงาน (ไม่ซ้ำในไฟล์) · วันที่ ISO ค.ศ. · ไม่มีแถวรวม (ไฟล์ใช้อัปโหลด/คัดลอก)
    /// </summary>
    public static IReadOnlyList<TabularColumn<BankTransferRow>> BankTransfer { get; } = new List<TabularColumn<BankTransferRow>>
    {
        new("ลำดับ", r => r.Seq, K.Integer),
        new("รหัสพนักงาน", r => r.Line.EmployeeCode),
        new("ชื่อพนักงาน", r => r.Line.EmployeeName),
        new("ชื่อบัญชี", r => string.IsNullOrWhiteSpace(r.Line.BankAccountName) ? r.Line.EmployeeName : r.Line.BankAccountName),
        new("ธนาคาร", r => r.Line.BankName),
        new("เลขที่บัญชี", r => r.Decision.AccountDigits),
        new("จำนวนเงิน", r => r.Decision.TransferAmount, K.Money),
        new("อ้างอิง", r => $"{r.Sheet.PayrollNumber}-{r.Line.EmployeeCode}"),
        new("วันที่จ่าย", r => r.Sheet.PayDate, K.Date),
        new("แหล่งจ่าย", r => r.Line.PaymentSourceLabel),
        new("สถานะ", r => r.Decision.Status),
        new("หมายเหตุ", r => r.Decision.Note),
    };
}
