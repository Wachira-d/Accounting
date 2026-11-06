using System.Globalization;
using System.Net;
using System.Text;
using Accounting.Models.DTOs.Recurring;

namespace Accounting.Helpers;

/// <summary>
/// **อีเมลทดสอบของรายการประจำ ("ส่งทดสอบถึงอีเมลฉัน") — ตัวประกอบเดียว** (รอบ 251 · WP-07 ข้อ 4 · AUTOPILOT_BILLING_DESIGN §4.5)
///
/// <para>เนื้อหา = สรุปผลทดลองรันรอบถัดไป (ตัวเลขชุดเดียวกับ <c>RecurringAutopilotService.BuildAsync</c>) · หัวเรื่องขึ้นต้น <see cref="SubjectPrefix"/> ·
/// <b>ไม่มีลิงก์ชำระจริง ไม่มีเลขเอกสารจริง ไม่แนบ PDF</b> · ทุกค่าที่ผู้ใช้คุมได้หนีก่อนต่อเข้า HTML (กฎ #4 C) · ส่งถึงผู้ใช้ที่กดเท่านั้น (ผู้เรียกบังคับ) · G6: pure</para>
/// </summary>
public static class RecurringTestEmail
{
    public const string RuleCode = "RECURRING-SEND-TEST";
    public const string SubjectPrefix = "[ทดสอบ] ";
    /// <summary>เพดานต่อผู้ใช้ต่อบริษัทต่อวัน (เวลาไทย) — อีเมลทดสอบไม่นับเพดานอีเมลเอกสาร จึงต้องมีเพดานของตัวเอง (กันใช้ปุ่มนี้ยิงอีเมลถี่)</summary>
    public const int MaxPerUserPerDay = 10;
    /// <summary>ป้ายใน audit (NewValues.action) — ตัวนับเพดานอ่านป้ายนี้</summary>
    public const string AuditActionName = "SendRecurringTestEmail";

    public const string NotCountedNote = "อีเมลทดสอบไม่นับเพดานอีเมลเอกสารของบริษัท และไม่ถูกบันทึกเป็นการส่งเอกสาร (บันทึกตรวจสอบว่าใครกดส่งทดสอบ)";

    public sealed record Message(string Subject, string Html, string Text);

    public static string? LimitRefusal(int sentToday)
        => sentToday >= MaxPerUserPerDay
            ? $"ส่งอีเมลทดสอบครบ {MaxPerUserPerDay} ฉบับของวันนี้แล้ว — ลองใหม่พรุ่งนี้ หรือใช้ปุ่ม \"ทดลองรัน\" (ไม่ส่งอีเมล)"
            : null;

    public static Message Compose(string recurringName, string? companyName, RecurringDryRunResponse d)
    {
        var subject = SignRequestEmailText.SafeSubject(SubjectPrefix + $"{d.DocumentTypeLabel} รอบถัดไป — {recurringName}");
        static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        static string M(decimal v) => v.ToString("N2", CultureInfo.InvariantCulture);
        var next = d.NextRunDates.Count > 0 ? ThaiDate.ToThaiDisplayString(d.NextRunDates[0]) : "-";

        var html = new StringBuilder();
        html.Append("<div style=\"font-family:sans-serif;font-size:14px;line-height:1.6;color:#0f172a\">");
        html.Append("<div style=\"padding:10px 12px;border:2px dashed #f59e0b;background:#fffbeb;border-radius:8px;margin-bottom:12px\"><b>อีเมลทดสอบ</b> — ตัวอย่างรอบถัดไปของรายการประจำ “")
            .Append(E(recurringName)).Append("” (").Append(E(companyName)).Append(")<br>")
            .Append("ไม่ใช่เอกสารจริง · ไม่มีเลขเอกสารจริง · ไม่มีลิงก์ชำระเงิน · ลูกค้าไม่ได้รับอีเมลนี้</div>");
        html.Append("<div>").Append(E(d.DocumentTypeLabel)).Append(" · รอบถัดไป ").Append(E(next)).Append("</div>");
        html.Append("<table style=\"border-collapse:collapse;margin:8px 0;min-width:320px\"><tr><th style=\"text-align:left;border-bottom:1px solid #cbd5e1;padding:4px\">รายการ</th>")
            .Append("<th style=\"text-align:right;border-bottom:1px solid #cbd5e1;padding:4px\">ยอด</th><th style=\"text-align:right;border-bottom:1px solid #cbd5e1;padding:4px\">VAT</th></tr>");
        foreach (var l in d.Lines)
            html.Append("<tr><td style=\"padding:4px\">").Append(E(l.Description)).Append("</td><td style=\"text-align:right;padding:4px\">").Append(M(l.Amount))
                .Append("</td><td style=\"text-align:right;padding:4px\">").Append(M(l.VatAmount)).Append("</td></tr>");
        html.Append("</table>");
        html.Append("<div>รวม ").Append(M(d.TotalAmount)).Append(" · หัก ณ ที่จ่าย ").Append(M(d.WithholdingTaxAmount))
            .Append(" · ยอดรับสุทธิ ").Append(M(d.ExpectedNetReceipt)).Append("</div>");
        html.Append("<div style=\"margin-top:8px\"><b>อนุมัติ:</b> ").Append(E(d.ApprovalOutcome)).Append("</div>");
        html.Append("<div><b>อีเมลถึงลูกค้า:</b> ").Append(E(d.EmailPlan)).Append("</div>");
        if (!string.IsNullOrWhiteSpace(d.EtaxPlan)) html.Append("<div><b>e-Tax:</b> ").Append(E(d.EtaxPlan)).Append("</div>");
        html.Append("<div><b>ลิงก์ชำระ:</b> ").Append(E(d.PayLinkPlan)).Append(" (อีเมลทดสอบไม่มีลิงก์จริง)</div>");
        html.Append("<div style=\"margin-top:8px\"><b>ความพร้อม:</b> ").Append(E(d.Readiness.LevelLabel)).Append("</div><ul style=\"margin:4px 0 0 18px\">");
        foreach (var it in d.Readiness.Items.Where(i => i.Verdict is AutopilotReadiness.Fail or AutopilotReadiness.Warn))
            html.Append("<li>").Append(E(it.Title)).Append("</li>");
        html.Append("</ul><div style=\"margin-top:12px;font-size:12px;color:#64748b\">").Append(E(NotCountedNote)).Append("</div></div>");

        var text = new StringBuilder();
        text.AppendLine($"อีเมลทดสอบ — ตัวอย่างรอบถัดไปของรายการประจำ \"{recurringName}\" (ไม่ใช่เอกสารจริง · ไม่มีลิงก์ชำระ · ลูกค้าไม่ได้รับอีเมลนี้)");
        text.AppendLine($"{d.DocumentTypeLabel} · รอบถัดไป {next}");
        foreach (var l in d.Lines) text.AppendLine($"- {l.Description}  {M(l.Amount)}  VAT {M(l.VatAmount)}");
        text.AppendLine($"รวม {M(d.TotalAmount)} · หัก ณ ที่จ่าย {M(d.WithholdingTaxAmount)} · ยอดรับสุทธิ {M(d.ExpectedNetReceipt)}");
        text.AppendLine("อนุมัติ: " + d.ApprovalOutcome);
        text.AppendLine("อีเมลถึงลูกค้า: " + d.EmailPlan);
        if (!string.IsNullOrWhiteSpace(d.EtaxPlan)) text.AppendLine("e-Tax: " + d.EtaxPlan);
        text.AppendLine("ลิงก์ชำระ: " + d.PayLinkPlan);
        text.AppendLine("ความพร้อม: " + d.Readiness.LevelLabel);
        return new Message(subject, html.ToString(), text.ToString());
    }
}
