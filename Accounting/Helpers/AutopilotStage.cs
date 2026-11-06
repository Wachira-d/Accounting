using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// **"ใบนี้อยู่ขั้นไหน" ของหน้า Autopilot — ตัวตัดสินตัวเดียว** (รอบ 252 WP-12A · AUTOPILOT §11.4 · A-6) · pure
/// <para>เป็น <b>read model</b>: ข้อเท็จจริงทุกตัวอ่านจากแถวจริง (Document · DocumentEmailLogs · Payments/PaymentIntent · PostPaymentAutomationRuns ·
/// BankTransactions) ตอนเปิดหน้า — ไม่มีตารางสถานะซ้ำ (หลักการ #3/#4) · ป้ายทั้งหมดประกอบที่นี่ หน้าเว็บแสดงอย่างเดียว (F2 ข้อ 5)</para>
/// </summary>
public static class AutopilotStage
{
    public enum StepState { Done, Waiting, Problem, NotApplicable }

    public sealed record Step(string Key, string Label, StepState State, string? Note);

    /// <param name="PaidVia">ช่องทางของการชำระล่าสุด: <c>Gateway</c> · <c>Bank</c> (ปิดหนี้จากเงินโอน) · <c>Manual</c> · null = ยังไม่มีการชำระ</param>
    /// <param name="PostPayStatus">สถานะงานหลังรับชำระล่าสุดของใบ (<c>PostPaymentAutomationPolicy.Status*</c>) · null = ไม่มีแถว</param>
    /// <param name="AwaitingBankConfirm">มีเงินโอนเข้าที่ระบบเสนอใบนี้ รอคนยืนยัน</param>
    public sealed record DocFacts(DocumentStatus Status, bool Emailed, bool EmailFailed, decimal BalanceDue, bool AnyPayment, string? PaidVia,
        string? PostPayStatus, string? PostPayMessage, string? ReceiptNumber, bool AwaitingBankConfirm);

    /// <param name="Overall"><c>Done</c> · <c>Running</c> · <c>Stuck</c> · <c>Voided</c></param>
    public sealed record Row(string Overall, string OverallLabel, IReadOnlyList<Step> Steps, string? Problem);

    public const string OverallDone = "Done";
    public const string OverallRunning = "Running";
    public const string OverallStuck = "Stuck";
    public const string OverallVoided = "Voided";

    private static string ViaLabel(string? via) => via switch
    {
        "Gateway" => "ออนไลน์",
        "Bank" => "เงินโอน (ระบบจับคู่)",
        "Manual" => "บันทึกโดยพนักงาน",
        _ => "",
    };

    public static Row Of(DocFacts f)
    {
        var steps = new List<Step> { new("created", "สร้าง", StepState.Done, null) };
        if (f.Status is DocumentStatus.Voided)
        {
            steps.Add(new Step("approved", "อนุมัติ", StepState.NotApplicable, "ยกเลิกแล้ว"));
            return new Row(OverallVoided, "ยกเลิกแล้ว", steps, null);
        }

        var approved = f.Status is not (DocumentStatus.Draft or DocumentStatus.WaitingApproval or DocumentStatus.Rejected);
        steps.Add(f.Status == DocumentStatus.Rejected
            ? new Step("approved", "อนุมัติ", StepState.Problem, "ถูกปฏิเสธ")
            : new Step("approved", "อนุมัติ", approved ? StepState.Done : StepState.Waiting, approved ? null : "รออนุมัติ"));

        steps.Add(f.Emailed ? new Step("sent", "ส่งถึงลูกค้า", StepState.Done, null)
            : f.EmailFailed ? new Step("sent", "ส่งถึงลูกค้า", StepState.Problem, "ส่งอีเมลไม่สำเร็จ")
            : new Step("sent", "ส่งถึงลูกค้า", approved ? StepState.Waiting : StepState.NotApplicable, approved ? "ยังไม่ได้ส่ง" : null));

        var fullyPaid = f.BalanceDue <= 0.005m && (f.AnyPayment || f.Status == DocumentStatus.Paid);
        steps.Add(fullyPaid ? new Step("paid", "ชำระ", StepState.Done, ViaLabel(f.PaidVia) is { Length: > 0 } v ? v : null)
            : f.AwaitingBankConfirm ? new Step("paid", "ชำระ", StepState.Waiting, "เงินโอนเข้ารอยืนยัน")
            : f.AnyPayment ? new Step("paid", "ชำระ", StepState.Waiting, "ชำระบางส่วน")
            : new Step("paid", "ชำระ", approved ? StepState.Waiting : StepState.NotApplicable, approved ? "รอชำระ" : null));

        Step receipt = f.PostPayStatus switch
        {
            PostPaymentAutomationPolicy.StatusDone => new Step("receipt", "ใบเสร็จ", StepState.Done, f.ReceiptNumber),
            PostPaymentAutomationPolicy.StatusBlocked or PostPaymentAutomationPolicy.StatusFailed =>
                new Step("receipt", "ใบเสร็จ", StepState.Problem, string.IsNullOrWhiteSpace(f.PostPayMessage) ? "งานหลังรับชำระหยุด" : f.PostPayMessage),
            PostPaymentAutomationPolicy.StatusSkipped => new Step("receipt", "ใบเสร็จ", StepState.NotApplicable, f.PostPayMessage),
            PostPaymentAutomationPolicy.StatusPending => new Step("receipt", "ใบเสร็จ", StepState.Waiting, "กำลังออก/ส่ง"),
            _ => f.AnyPayment
                ? new Step("receipt", "ใบเสร็จ", StepState.NotApplicable, "ไม่ได้ตั้งออก/ส่งใบเสร็จอัตโนมัติ")
                : new Step("receipt", "ใบเสร็จ", StepState.NotApplicable, null),
        };
        steps.Add(receipt);

        var problem = steps.FirstOrDefault(s => s.State == StepState.Problem);
        if (problem != null)
            return new Row(OverallStuck, "ติด — ระบบทำต่อไม่ได้", steps, $"{problem.Label}: {problem.Note}");
        var receiptOpen = receipt.State == StepState.Waiting;
        if (fullyPaid && !receiptOpen)
            return new Row(OverallDone, "จบแล้ว", steps, null);
        return new Row(OverallRunning, "กำลังเดิน", steps, null);
    }
}
