using System.Text.RegularExpressions;
using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;
using Policy = Accounting.Helpers.PostPaymentAutomationPolicy;

namespace Accounting.Tests;

/// <summary>
/// รอบ 267 แพ็ก P4 — "หลังจ่าย" (pure · ทั้งสองทิศ): ชั้นค่าตั้งต่อเอกสารของงานหลังรับชำระ (<see cref="PostPaymentAutomationPolicy"/> — ตัวต่อชั้นเดียว)
/// + ส่วนหลังจ่ายของหน้าเอกสารสำหรับลูกค้า (<see cref="CustomerDocPageAfterPay"/>) — DOCUMENT_FLOW §2.12n-P4 · คำตัดสิน 267-1-8 · 267-1-44…50 ·
/// สถานการณ์ S16 S17 S18 S19 S20 S24 ของ team-267-doc-pages-design.md §17 (เส้นจริงบนฐาน: <c>PostPayRound267DbTests</c>)
/// </summary>
public class PostPayRound267Tests
{
    private static readonly Policy.Settings CompanyOn = new(true, true);
    private static readonly Policy.Settings CompanyOff = new(false, false);
    private static Policy.Overrides O(bool? email, bool? partial = null) => new(email, partial);

    // ───────── S17: ชั้นเอกสารอยู่บนสุด (ทั้งสองทิศ) ─────────

    [Fact]
    public void PostPay267_DocLayerWins_ใบตั้งเปิด_ผู้ติดต่อตั้งปิด_ใบชนะ()
    {
        var r = Policy.Resolve(CompanyOff, O(true), schedule: O(false), recurring: O(false), contact: O(false));
        Assert.True(r.Settings.EmailReceipt);
        Assert.Equal(Policy.LayerDocument, r.EmailReceiptLayer);
        Assert.True(Policy.EffectiveLayers(CompanyOff, O(true), null, null, O(false)).EmailReceipt);
    }

    [Fact]
    public void PostPay267_DocLayerOffWins_ใบตั้งปิด_ผู้ติดต่อและบริษัทเปิด_ใบชนะ()
    {
        var r = Policy.Resolve(CompanyOn, O(false), schedule: O(true), recurring: O(true), contact: O(true));
        Assert.False(r.Settings.EmailReceipt);
        Assert.Equal(Policy.LayerDocument, r.EmailReceiptLayer);
        // ปิดที่ใบ ⇒ ทางเข้ารับชำระไม่สร้างงาน (พฤติกรรมเดียวกับปิดทั้งบริษัท)
        Assert.Equal(Policy.EnqueueKind.None, Policy.ForEnqueue(r.Settings, PaymentEntrySource.Manual).Kind);
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData(true, null, null)]
    [InlineData(false, true, null)]
    [InlineData(null, false, true)]
    [InlineData(null, null, false)]
    [InlineData(true, false, true)]
    public void PostPay267_NullDocLayerFallsThrough_ใบไม่ตั้ง_ผลเท่าสี่ชั้นเดิมเป๊ะ(bool? schedule, bool? recurring, bool? contact)
    {
        foreach (var company in new[] { CompanyOn, CompanyOff })
        {
            var four = Policy.EffectiveLayers(company, O(schedule, schedule), O(recurring, recurring), O(contact, contact));
            Assert.Equal(four, Policy.EffectiveLayers(company, null, O(schedule, schedule), O(recurring, recurring), O(contact, contact)));
            Assert.Equal(four, Policy.EffectiveLayers(company, O(null, null), O(schedule, schedule), O(recurring, recurring), O(contact, contact)));
            // ชั้นที่ตัดสิน ≠ เอกสาร เมื่อใบไม่ตั้ง
            Assert.NotEqual(Policy.LayerDocument,
                Policy.Resolve(company, O(null, null), O(schedule), O(recurring), O(contact)).EmailReceiptLayer);
        }
    }

    [Fact]
    public void PostPay267_LayerOrder_ชั้นที่ตัดสินเรียงใบ_ตาราง_รายการประจำ_ผู้ติดต่อ_บริษัท()
    {
        Assert.Equal(Policy.LayerSchedule, Policy.Resolve(CompanyOff, null, O(true), O(false), O(false)).EmailReceiptLayer);
        Assert.Equal(Policy.LayerRecurring, Policy.Resolve(CompanyOff, null, null, O(true), O(false)).EmailReceiptLayer);
        Assert.Equal(Policy.LayerContact, Policy.Resolve(CompanyOff, null, null, null, O(true)).EmailReceiptLayer);
        Assert.Equal(Policy.LayerCompany, Policy.Resolve(CompanyOn, null, null, null, null).EmailReceiptLayer);
    }

    [Fact]
    public void PostPay267_PerField_ใบตั้งแค่ช่องงวด_ช่องออกใบเสร็จตามชั้นถัดไป()
    {
        var r = Policy.Resolve(CompanyOff, O(null, true), null, null, O(true, false));
        Assert.True(r.Settings.EmailReceipt);            // จากผู้ติดต่อ
        Assert.Equal(Policy.LayerContact, r.EmailReceiptLayer);
        Assert.True(r.Settings.IncludePartial);          // จากใบ (ทับผู้ติดต่อที่ตั้ง false)
        var opposite = Policy.Resolve(CompanyOn, O(null, false), null, null, null);
        Assert.True(opposite.Settings.EmailReceipt);
        Assert.False(opposite.Settings.IncludePartial);
    }

    [Fact]
    public void PostPay267_EffectiveLabel_บอกผลและชั้นที่ตัดสิน()
    {
        var on = Policy.EffectiveLabel(Policy.Resolve(CompanyOff, O(true, true), null, null, O(false)));
        Assert.Contains("ออกและส่งใบเสร็จ", on);
        Assert.Contains("ทุกงวดที่รับ", on);
        Assert.Contains("ตั้งที่เอกสารใบนี้", on);
        var off = Policy.EffectiveLabel(Policy.Resolve(CompanyOn, null, null, null, O(false)));
        Assert.Contains("ไม่ออก/ไม่ส่งใบเสร็จอัตโนมัติ", off);
        Assert.Contains("ผู้ติดต่อ", off);
        Assert.DoesNotContain("เอกสารใบนี้", off);
    }

    // ───────── รหัสสามสถานะ + ตัวใช้ค่าตั้งต่อใบ ─────────

    [Theory]
    [InlineData(null, false, null)]
    [InlineData("", true, null)]
    [InlineData("Inherit", true, null)]
    [InlineData(" inherit ", true, null)]
    [InlineData("On", true, true)]
    [InlineData("off", true, false)]
    public void PostPay267_ParseMode_สามสถานะ(string? mode, bool touched, bool? value)
    {
        var p = Policy.ParseMode(mode);
        Assert.Null(p.Problem);
        Assert.Equal(touched, p.Touched);
        Assert.Equal(value, p.Value);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("1")]
    [InlineData("เปิด")]
    public void PostPay267_ParseMode_รหัสไม่รู้จัก_ปฏิเสธไม่เดา(string mode)
    {
        var p = Policy.ParseMode(mode);
        Assert.NotNull(p.Problem);
        var applied = Policy.ApplyDocumentLayer(DocumentType.Invoice, true, false, mode, null);
        Assert.NotNull(applied.Problem);
        Assert.True(applied.EmailReceipt);          // ค่าเดิมไม่ถูกแตะ
        Assert.False(applied.IncludePartial);
    }

    [Theory]
    [InlineData(null, "Inherit")]
    [InlineData(true, "On")]
    [InlineData(false, "Off")]
    public void PostPay267_ModeCode_ไปกลับได้(bool? value, string code)
    {
        Assert.Equal(code, Policy.ModeCode(value));
        Assert.Equal(value, Policy.ParseMode(code).Value);
    }

    [Fact]
    public void PostPay267_ApplyDocumentLayer_ไม่ส่งมาคงค่าเดิม_inheritล้าง_onoffตั้ง()
    {
        // ไม่ส่งมา (null) = คงค่าเดิมทั้งสองช่อง
        var keep = Policy.ApplyDocumentLayer(DocumentType.Invoice, true, false, null, null);
        Assert.Null(keep.Problem); Assert.True(keep.EmailReceipt); Assert.False(keep.IncludePartial);
        // ส่งช่องเดียว = อีกช่องคงเดิม
        var one = Policy.ApplyDocumentLayer(DocumentType.Invoice, true, false, "Off", null);
        Assert.Null(one.Problem); Assert.False(one.EmailReceipt); Assert.False(one.IncludePartial);
        // "" / Inherit = ล้างกลับไปตามค่าตั้ง
        var cleared = Policy.ApplyDocumentLayer(DocumentType.TaxInvoice, true, true, "", "Inherit");
        Assert.Null(cleared.Problem); Assert.Null(cleared.EmailReceipt); Assert.Null(cleared.IncludePartial);
        var set = Policy.ApplyDocumentLayer(DocumentType.DebitNote, null, null, "On", "on");
        Assert.Null(set.Problem); Assert.True(set.EmailReceipt); Assert.True(set.IncludePartial);
    }

    [Theory]
    [InlineData(DocumentType.Quotation)]
    [InlineData(DocumentType.Receipt)]
    [InlineData(DocumentType.PurchaseInvoice)]
    [InlineData(DocumentType.BillingNote)]
    [InlineData(DocumentType.CreditNote)]
    public void PostPay267_ApplyDocumentLayer_ชนิดที่ไม่รับชำระ_เปิดปิดถูกปฏิเสธ_ตามค่าตั้งผ่าน(DocumentType type)
    {
        Assert.False(Policy.DocumentLayerAllowed(type));
        var refused = Policy.ApplyDocumentLayer(type, null, null, "On", null);
        Assert.Equal(Policy.DocumentLayerNotAllowedMessage, refused.Problem);
        Assert.Null(refused.EmailReceipt);
        Assert.NotNull(Policy.ApplyDocumentLayer(type, null, null, null, "Off").Problem);
        // ทิศตรงข้าม: ฟอร์มส่ง "ตามค่าตั้ง" ให้ทุกชนิดได้ (ไม่มีอะไรเปลี่ยน) — ใบชนิดอื่นไม่ถูกกันจากการบันทึก
        Assert.Null(Policy.ApplyDocumentLayer(type, null, null, "Inherit", "").Problem);
        Assert.Null(Policy.ApplyDocumentLayer(type, null, null, null, null).Problem);
    }

    [Fact]
    public void PostPay267_DocumentLayerAllowed_เท่าชุดชนิดที่รับชำระทางลิงก์()
    {
        foreach (DocumentType t in Enum.GetValues(typeof(DocumentType)))
            Assert.Equal(Array.IndexOf(DocumentPayLinkPolicy.PayableTypes, t) >= 0, Policy.DocumentLayerAllowed(t));
        Assert.True(Policy.DocumentLayerAllowed(DocumentType.Invoice));
        Assert.True(Policy.DocumentLayerAllowed(DocumentType.TaxInvoice));
    }

    // ───────── S18: ไม่สืบทอดไปใบลูก/โคลน (ใบแทนจากการยกเลิก-ออกใหม่ = พาไป — ฝ่ายค้าน MED-3) ─────────

    /// <summary>ค่าตั้งต่อใบเป็นเจตนาของใบนั้น — ทางสร้างใบลูก (แปลง · โคลน · ใบเสร็จของการชำระ · CN อัตโนมัติ · รายการประจำ) ต้องไม่คัดลอก ·
    /// ใบแทน (ยกเลิก-ออกใหม่) พาไปผ่าน <c>SettlementPaidReissue.CopyDocumentForReissue</c> เท่านั้น (เทสต์ PostPay267_MED3_*) ·
    /// ผู้เขียนช่องนี้มีได้เฉพาะ: ตัวสร้างใบจากคำขอ (<c>postPayLayer</c>) · ตัวใช้ค่าตั้งต่อใบ · ปุ่มบนหน้าเอกสาร</summary>
    [Fact]
    public void PostPay267_DocLayerNotInherited_ไม่มีทางสร้างใบลูกที่คัดลอกค่าตั้งต่อใบ()
    {
        var root = Path.Combine(RepoRoot(), "Accounting");
        var copy = new Regex(@"PostPay(EmailReceipt|IncludePartial)\s*[:=]\s*[A-Za-z_][A-Za-z0-9_]*\??\.PostPay(EmailReceipt|IncludePartial)\b");
        var offenders = new List<string>();
        foreach (var dir in new[] { "Services", "Controllers" })
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, dir), "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            if (!text.Contains("PostPayEmailReceipt") && !text.Contains("PostPayIncludePartial")) continue;
            foreach (Match m in copy.Matches(text))
            {
                // ยอมเฉพาะ: ค่าตั้งบริษัท → DTO (s?.) · ตารางส่ง ↔ คำขอ/DTO (rule. / req.) · ร่างรายการประจำ (d.PostPay… == null ? … — ไม่ใช่เอกสาร) —
                // การคัดลอก "จากใบหนึ่งไปอีกใบ" ไม่มีในชุดนี้ (เพิ่มผู้อ่านใหม่ = ต้องมาเติมที่นี่อย่างตั้งใจ)
                var v = m.Value;
                var name = Path.GetFileName(file);
                if ((name == "DocumentPayLinkService.cs" && v.Contains("s?."))
                    || (name == "EmailScheduleService.cs" && v.Contains("rule."))
                    || (name == "EmailScheduleController.cs" && v.Contains("req."))
                    || (name == "RecurringAutopilotService.cs" && v.Contains("= d.PostPay")))
                    continue;
                offenders.Add(Path.GetFileName(file) + ": " + v);
            }
        }
        Assert.True(offenders.Count == 0, "พบการคัดลอกค่าตั้งหลังรับชำระต่อใบไปอีกใบ: " + string.Join(" | ", offenders));

        // ตัวสร้างใบจากคำขออ่านจากคำขอเท่านั้น (ใบลูกที่ไม่ส่งช่องนี้ = ตามค่าตั้ง)
        var svc = File.ReadAllText(Path.Combine(root, "Services", "Implementations", "DocumentService.cs"));
        Assert.Contains("PostPayDocumentLayerOrThrow(request.DocumentType, null, null, request.PostPayReceiptMode, request.PostPayPartialMode)", svc);
        Assert.Contains("PostPayEmailReceipt = postPayLayer.EmailReceipt", svc);
        // ฝ่ายค้าน MED-3 ทิศตรงข้าม: โคลน/แปลง ไม่ส่งค่าตั้งต่อใบเข้าคำขอสร้างใบใหม่ (ใบใหม่ = ตามค่าตั้ง)
        var clone = File.ReadAllText(Path.Combine(root, "Controllers", "DocumentCloneController.cs"));
        Assert.DoesNotContain("PostPayReceiptMode", clone);
        Assert.DoesNotContain("PostPayEmailReceipt", clone);
        var convertStart = svc.IndexOf("public async Task<DocumentResponse> ConvertDocumentAsync(", StringComparison.Ordinal);
        Assert.True(convertStart > 0);
        var convertBody = svc.Substring(convertStart, Math.Min(20_000, svc.Length - convertStart));
        var convertEnd = convertBody.IndexOf("\n    public ", 10, StringComparison.Ordinal);
        if (convertEnd > 0) convertBody = convertBody[..convertEnd];
        Assert.DoesNotContain("PostPayEmailReceipt", convertBody);
        Assert.DoesNotContain("PostPayReceiptMode", convertBody);
    }

    // ───────── S19: สถานะสลิปที่ลูกค้าเห็น ─────────

    [Fact]
    public void PostPay267_SlipState_รอตรวจ_ยืนยัน_ปฏิเสธพร้อมเหตุ()
    {
        foreach (var open in new[] { PaymentIntentStatus.Created, PaymentIntentStatus.Pending })
        {
            var pending = CustomerDocPageAfterPay.SlipState(false, open, CustomerDocPageAfterPay.SlipRejectedCode, null);
            Assert.Equal(CustomerDocPageAfterPay.SlipPending, pending.State);
            Assert.Contains("รอผู้ขายตรวจ", pending.Label);
            Assert.Contains("ผู้ขายยืนยันเท่านั้น", pending.Note);     // สลิปอย่างเดียวไม่ใช่ "ชำระแล้ว"
        }
        var ok = CustomerDocPageAfterPay.SlipState(false, PaymentIntentStatus.Succeeded, CustomerDocPageAfterPay.SlipRejectedCode, "ไม่ควรแสดง");
        Assert.Equal(CustomerDocPageAfterPay.SlipConfirmed, ok.State);
        Assert.Null(ok.Note);

        var typed = CustomerDocPageAfterPay.SlipState(false, PaymentIntentStatus.Failed, CustomerDocPageAfterPay.SlipRejectedCode, "  ยอดโอนขาด 70 บาท กรุณาโอนเพิ่ม  ");
        Assert.Equal(CustomerDocPageAfterPay.SlipRejected, typed.State);
        Assert.Equal("ยอดโอนขาด 70 บาท กรุณาโอนเพิ่ม", typed.Note);

        // ทิศตรงข้าม: พนักงานไม่พิมพ์ถึงลูกค้า ⇒ ข้อความกลาง (ไม่ว่าง · มีทางไปต่อ)
        foreach (var blank in new[] { null, "", "   " })
        {
            var d = CustomerDocPageAfterPay.SlipState(false, PaymentIntentStatus.Failed, CustomerDocPageAfterPay.SlipRejectedCode, blank);
            Assert.Equal(CustomerDocPageAfterPay.SlipState(false, PaymentIntentStatus.Failed, CustomerDocPageAfterPay.SlipRejectedCode, null).Note, d.Note);
            Assert.Contains("แนบใหม่", d.Note);
        }
        Assert.Equal(CustomerDocPageAfterPay.SlipState(true, PaymentIntentStatus.Failed, CustomerDocPageAfterPay.SlipRejectedCode, "  ").Note, CustomerDocPageAfterPay.SlipState(true, PaymentIntentStatus.Failed, CustomerDocPageAfterPay.SlipRejectedCode, null).Note);
        foreach (var closed in new[] { PaymentIntentStatus.Expired, PaymentIntentStatus.Refunded, PaymentIntentStatus.PartiallyRefunded })
            Assert.Equal(CustomerDocPageAfterPay.SlipClosed, CustomerDocPageAfterPay.SlipState(false, closed, CustomerDocPageAfterPay.SlipRejectedCode, null).State);
    }

    [Fact]
    public void PostPay267_NormalizeRejectReason_ตัดความยาวและอักขระควบคุม()
    {
        Assert.Null(CustomerDocPageAfterPay.NormalizeRejectReason(null));
        Assert.Null(CustomerDocPageAfterPay.NormalizeRejectReason(" \t "));
        Assert.Equal("ก\nข", CustomerDocPageAfterPay.NormalizeRejectReason("ก\u0007\nข\r"));
        var longText = new string('ก', 400);
        Assert.Equal(CustomerDocPageAfterPay.RejectReasonMaxLength, CustomerDocPageAfterPay.NormalizeRejectReason(longText)!.Length);
    }

    // ───────── S16: ไม่แต่งใบเสร็จ ─────────

    [Fact]
    public void CDP267_NoAutoReceiptNoFakeReceipt_ชำระแล้วยังไม่มีใบเสร็จ_บอกตามจริง()
    {
        var note = CustomerDocPageAfterPay.ReceiptPendingNote(false, DocumentType.Invoice, DocumentStatus.Paid, 1_070m, receiptCount: 0);
        Assert.NotNull(note);
        Assert.Contains("ยังไม่ได้ออกใบเสร็จ", note);
        Assert.NotNull(CustomerDocPageAfterPay.ReceiptPendingNote(true, DocumentType.Invoice, DocumentStatus.PartiallyPaid, 500m, 0));

        // ทิศตรงข้าม: มีใบเสร็จแล้ว · ยังไม่รับเงิน · ใบยกเลิก · ใบที่อาจเป็นใบเสร็จในตัว ⇒ ไม่มีข้อความ
        Assert.Null(CustomerDocPageAfterPay.ReceiptPendingNote(false, DocumentType.Invoice, DocumentStatus.Paid, 1_070m, receiptCount: 1));
        Assert.Null(CustomerDocPageAfterPay.ReceiptPendingNote(false, DocumentType.Invoice, DocumentStatus.Approved, 0m, 0));
        Assert.Null(CustomerDocPageAfterPay.ReceiptPendingNote(false, DocumentType.Invoice, DocumentStatus.Voided, 1_070m, 0));
        Assert.Null(CustomerDocPageAfterPay.ReceiptPendingNote(false, DocumentType.TaxInvoice, DocumentStatus.Paid, 1_070m, 0));
        Assert.Null(CustomerDocPageAfterPay.ReceiptPendingNote(false, DocumentType.Quotation, DocumentStatus.Approved, 1_070m, 0));
    }

    // ───────── S20: ป้าย e-Tax เฉพาะเมื่อส่งแล้วจริง ─────────

    [Fact]
    public void CDP267_EtaxBadgeOnlyWhenSent_ยังไม่ส่งไม่มีป้าย_ส่งแล้วมีวันที่ไทย()
    {
        Assert.Null(CustomerDocPageAfterPay.EtaxSentNote(false, null));
        Assert.Null(CustomerDocPageAfterPay.EtaxSentNote(true, null));
        // 2026-11-12 18:30 UTC = 13 พ.ย. 2569 เวลาไทย
        var th = CustomerDocPageAfterPay.EtaxSentNote(false, new DateTime(2026, 11, 12, 18, 30, 0, DateTimeKind.Utc));
        Assert.Contains("13/11/2569", th);
        Assert.Contains("ส่งทางอีเมลแล้ว", th);
        Assert.Contains("สำเนา", th);                    // ไม่อ้างว่าไฟล์บนหน้าเป็นฉบับที่ประทับเวลา
        Assert.Contains("13 Nov 2026", CustomerDocPageAfterPay.EtaxSentNote(true, new DateTime(2026, 11, 12, 18, 30, 0, DateTimeKind.Utc)));
    }

    // ───────── S24: ปุ่มจ่ายรายใบของใบที่เกี่ยวข้อง ─────────

    [Fact]
    public void PostPay267_RelatedPayOffered_เฉพาะโหมดลิงก์_ชนิดรับชำระ_ยังค้าง_ยังมีผล()
    {
        Assert.True(CustomerDocPageAfterPay.RelatedPayOffered(CustomerDocPage.Mode.Link, DocumentType.Invoice, DocumentStatus.Approved, 1_070m));
        Assert.True(CustomerDocPageAfterPay.RelatedPayOffered(CustomerDocPage.Mode.Link, DocumentType.TaxInvoice, DocumentStatus.PartiallyPaid, 0.5m));
        // ทิศตรงข้าม
        Assert.False(CustomerDocPageAfterPay.RelatedPayOffered(CustomerDocPage.Mode.Link, DocumentType.Invoice, DocumentStatus.Paid, 0m));        // จ่ายแล้ว
        Assert.False(CustomerDocPageAfterPay.RelatedPayOffered(CustomerDocPage.Mode.Link, DocumentType.Invoice, DocumentStatus.Voided, 1_070m)); // ยกเลิก
        Assert.False(CustomerDocPageAfterPay.RelatedPayOffered(CustomerDocPage.Mode.Link, DocumentType.Invoice, DocumentStatus.Draft, 1_070m));
        Assert.False(CustomerDocPageAfterPay.RelatedPayOffered(CustomerDocPage.Mode.Link, DocumentType.Quotation, DocumentStatus.Approved, 1_070m));
        Assert.False(CustomerDocPageAfterPay.RelatedPayOffered(CustomerDocPage.Mode.Link, DocumentType.Receipt, DocumentStatus.Paid, 1_070m));
        foreach (var mode in new[] { CustomerDocPage.Mode.Portal, CustomerDocPage.Mode.LineView, CustomerDocPage.Mode.Preview })
            Assert.False(CustomerDocPageAfterPay.RelatedPayOffered(mode, DocumentType.Invoice, DocumentStatus.Approved, 1_070m));
    }

    // ───────── 50 ทวิ ─────────

    [Fact]
    public void PostPay267_WhtCert_รอใบมีฟอร์ม_ส่งแล้วไม่ชวนส่งซ้ำ_ไม่มีแถวไม่มีการ์ด()
    {
        Assert.Null(CustomerDocPageAfterPay.WhtCert(false, hasOpenItem: false, submitted: false, missing: 0m));
        var awaiting = CustomerDocPageAfterPay.WhtCert(false, true, false, 30m)!;
        Assert.Equal(CustomerDocPageAfterPay.WhtAwaiting, awaiting.State);
        Assert.True(awaiting.CanUpload);
        Assert.Equal(30m, awaiting.Missing);
        var submitted = CustomerDocPageAfterPay.WhtCert(false, true, true, 30m)!;
        Assert.Equal(CustomerDocPageAfterPay.WhtSubmitted, submitted.State);
        Assert.False(submitted.CanUpload);
        Assert.Contains("ไม่ต้องส่งซ้ำ", submitted.Note);
    }

    [Fact]
    public void PostPay267_Labels_ไทยอังกฤษครบชุดเดียวกัน()
    {
        var th = CustomerDocPageAfterPay.Labels(false);
        var en = CustomerDocPageAfterPay.Labels(true);
        Assert.Equal(th.Keys.OrderBy(k => k), en.Keys.OrderBy(k => k));
        Assert.All(th.Values.Concat(en.Values), v => Assert.False(string.IsNullOrWhiteSpace(v)));
    }

    // ───────── หน้าเว็บ: ไม่มีตารางสถานะ/ข้อความของตัวเอง · พอร์ทัลเปิดหน้าเดียวกัน ─────────

    [Fact]
    public void PostPay267_Pages_หน้าลูกค้าแสดงจากเซิร์ฟเวอร์_พอร์ทัลฝังหน้าเดียวกัน_โทเคนไม่อยู่ในURL()
    {
        var root = Path.Combine(RepoRoot(), "Accounting", "wwwroot");
        var page = File.ReadAllText(Path.Combine(root, "pages", "pay-document.html"));
        Assert.Contains("afterPayHtml(p, L)", page);
        Assert.Contains("p.afterPay", page);
        Assert.Contains("docpage-portal-token", page);
        Assert.Contains("e.source !== window.parent", page);          // รับโทเคนจากหน้าแม่เท่านั้น
        foreach (var serverText in new[] { "รอผู้ขายตรวจสลิป", "ผู้ขายไม่รับสลิปนี้", "ผู้ขายยังไม่ได้ออกใบเสร็จ", "ชำระใบนี้" })
            Assert.DoesNotContain(serverText, page);                  // ข้อความสถานะอยู่ที่ CustomerDocPageAfterPay ที่เดียว

        var portal = File.ReadAllText(Path.Combine(root, "portal.html"));
        Assert.Contains("pay-document.html?c=", portal);
        Assert.Contains("&portal=", portal);
        Assert.Contains("docpage-portal-ready", portal);
        Assert.Contains("e.source !== frame.contentWindow", portal);  // ส่งโทเคนให้กรอบที่เปิดเองเท่านั้น
        Assert.DoesNotContain("accessToken)}&", portal);
        Assert.DoesNotMatch(new Regex(@"pay-document\.html\?[^`'""]*(token|jwt|access)", RegexOptions.IgnoreCase), portal);
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "Accounting.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("ไม่พบ Accounting.sln");
    }

    // ───────── ฝ่ายค้าน P4 (review-267-P4.md) ─────────

    [Fact]
    public void PostPay267_MED1_ReceiptBelongsTo_ผูกใบนี้_หรือใบเสร็จของการชำระที่ไม่กระจาย_ใบเสร็จของใบอื่นไม่ใช่ของใบนี้()
    {
        Guid x = Guid.NewGuid(), y = Guid.NewGuid();
        Assert.True(CustomerDocPageAfterPay.ReceiptBelongsTo(x, x, false));            // ใบเสร็จรายใบของ X
        Assert.True(CustomerDocPageAfterPay.ReceiptBelongsTo(x, null, true));          // ใบเสร็จของการชำระของ X ที่ไม่ได้จัดสรรไปใบอื่น (ข้อมูลเก่าไม่ผูก)
        // ทิศตรงข้าม: ใบเสร็จของ Y ในการชำระเดียวกัน · ใบเสร็จรวมของการชำระที่กระจาย · ใบเสร็จที่ผูกใบอื่นแม้เป็น ReceiptDocumentId ของการชำระนี้
        Assert.False(CustomerDocPageAfterPay.ReceiptBelongsTo(x, y, false));
        Assert.False(CustomerDocPageAfterPay.ReceiptBelongsTo(x, null, false));
        Assert.False(CustomerDocPageAfterPay.ReceiptBelongsTo(x, y, true));
    }

    [Fact]
    public void PostPay267_LOW1_SlipFailedBySystem_ไม่บอกว่าผู้ขายปฏิเสธ_เฉพาะรหัสปฏิเสธของพนักงาน()
    {
        var sys = CustomerDocPageAfterPay.SlipState(false, PaymentIntentStatus.Failed, "create_charge_failed", "ไม่ควรแสดง");
        Assert.Equal(CustomerDocPageAfterPay.SlipFailed, sys.State);
        Assert.DoesNotContain("ผู้ขาย", sys.Label);
        Assert.DoesNotContain("ไม่ควรแสดง", sys.Note);
        Assert.Equal(CustomerDocPageAfterPay.SlipFailed, CustomerDocPageAfterPay.SlipState(true, PaymentIntentStatus.Failed, null, null).State);
        // ทิศตรงข้าม: พนักงานปฏิเสธจริง ⇒ ป้ายปฏิเสธ + ข้อความที่พิมพ์ถึงลูกค้า
        var rej = CustomerDocPageAfterPay.SlipState(false, PaymentIntentStatus.Failed, CustomerDocPageAfterPay.SlipRejectedCode, "ยอดขาด 70 บาท");
        Assert.Equal(CustomerDocPageAfterPay.SlipRejected, rej.State);
        Assert.Equal("ยอดขาด 70 บาท", rej.Note);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, null)]
    [InlineData(null, true)]
    [InlineData(null, null)]
    public void PostPay267_MED3_ReissueCarriesDocLayer_ใบแทนพาค่าตั้งของใบเดิม_nullเป็นnull(bool? email, bool? partial)
    {
        var from = new Accounting.Models.Entities.Document { DocumentType = DocumentType.TaxInvoice, PostPayEmailReceipt = email, PostPayIncludePartial = partial };
        var to = new Accounting.Models.Entities.Document { PostPayEmailReceipt = true, PostPayIncludePartial = true };
        SettlementPaidReissue.CopyDocumentForReissue(from, to);
        Assert.Equal(email, to.PostPayEmailReceipt);
        Assert.Equal(partial, to.PostPayIncludePartial);
        Assert.Contains(nameof(Accounting.Models.Entities.Document.PostPayEmailReceipt), SettlementPaidReissue.DocumentCarriedFields);
        Assert.DoesNotContain(nameof(Accounting.Models.Entities.Document.PostPayEmailReceipt), SettlementPaidReissue.DocumentNotCarriedFields);
    }
}
