namespace Accounting.Helpers;

/// <summary>รอบ 259-3 (ทีม E 4.2 · F2 #5) — ป้ายสถานะ + "เกินกำหนด" ของเอกสารในพอร์ทัลลูกค้า (ตัวตั้งเดียว · เดิม portal.html ตัดสินเองใน JS
/// <c>docStatusText</c> + KPI เกินกำหนด เทียบ <c>new Date(dueDate) &lt; new Date()</c> ตามนาฬิกาเครื่องลูกค้า ⇒ ใบที่ครบกำหนด "วันนี้" ขึ้นเกินกำหนดตั้งแต่เที่ยงคืน)
/// <para>เกินกำหนด = ยังไม่ยกเลิก · ยอดค้าง &gt; 0 · วันที่ไทยวันนี้เลยวันครบกำหนดแล้ว (ครบกำหนดวันนี้ = ยังไม่เกิน)</para></summary>
public static class PortalDocumentStatus
{
    public const string Voided = "Voided";

    public static bool IsOverdue(string status, decimal balanceDue, DateTime? dueDate, DateTime nowUtc)
        => status != Voided && balanceDue > 0 && dueDate is DateTime due && nowUtc.AddHours(7).Date > due.Date;

    public static string Label(string status, decimal balanceDue, DateTime? dueDate, DateTime nowUtc)
        => status == Voided ? "ยกเลิก"
            : balanceDue <= 0 ? "ชำระแล้ว"
            : IsOverdue(status, balanceDue, dueDate, nowUtc) ? "เกินกำหนด" : "รอชำระ";
}
