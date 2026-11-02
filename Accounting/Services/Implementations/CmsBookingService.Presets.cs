using Accounting.Helpers;
using Accounting.Models.DTOs.Cms;
using Accounting.Models.Entities;
using Accounting.Services.Implementations.Cms;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 228 (คำตัดสิน 228-6 · คำถามเจ้าของ "สั่งทำ/พรีออเดอร์ ควรใช้ประเภทธุรกิจไหน") — <b>ตั้งค่าเว็บทั้งชุดจากแม่แบบ</b>
/// (<see cref="CmsBookingServiceSeeder.Preset.Setup"/>): สร้างบริการตัวอย่างผ่านเส้นสร้างบริการเดิม (ด่านเดิมครบ) · เปิดจัดส่ง/รับเอง ·
/// หน้าต่างเปลี่ยนวิธีรับ · ช่องที่ลูกค้ากรอกตอนจอง (ผูกเฉพาะบริการของแม่แบบ) — <b>ไม่มีเครื่องใหม่</b> และ<b>ค่าที่เจ้าของตั้งไว้แล้วชนะ</b>
/// (ตั้งเฉพาะช่องที่ยังว่าง · บริการชื่อซ้ำไม่สร้างซ้ำ · ช่องคีย์/ป้ายซ้ำไม่เพิ่ม) ⇒ กดซ้ำได้ไม่เพิ่มของซ้ำ · คืนรายการที่ทำ/ข้าม/สิ่งที่ต้องทำต่อ (ไม่ประกาศว่าพร้อมขายเอง)
/// </summary>
public partial class CmsBookingService
{
    public async Task<BookingPresetApplyResult> ApplyPresetToSiteAsync(Guid companyId, Guid siteId, string presetKey, string userId)
    {
        await EnsureSiteAsync(companyId, siteId);
        // ฝ่ายค้านรอบ 228 N1: ล็อกต่อเว็บ — กดสองแท็บพร้อมกันไม่สร้างบริการ/ช่องซ้ำ (ตรวจ "มีแล้ว" กับเขียน ต้องเป็นก้อนเดียว)
        return await AdvisorySessionLock.RunAsync(_db, AdvisoryLockKey.For(companyId, AdvisoryLockKey.CmsBookingInvoice, "preset:" + siteId.ToString("N")),
            () => ApplyPresetLockedAsync(companyId, siteId, presetKey, userId),
            "กำลังตั้งค่าเว็บจากแม่แบบอยู่ — รอสักครู่แล้วลองใหม่", "CMS-BOOKING-PRESET");
    }

    private async Task<BookingPresetApplyResult> ApplyPresetLockedAsync(Guid companyId, Guid siteId, string presetKey, string userId)
    {
        var preset = CmsBookingServiceSeeder.Presets.FirstOrDefault(p => p.Key == (presetKey ?? "").Trim())
            ?? throw new KeyNotFoundException("ไม่พบแม่แบบ");
        // รอบ 228 (แพ็ก E): แม่แบบที่ไม่มีค่าตั้งระดับเว็บ (ซาลอน · ฟิตเนส ฯลฯ) ใช้จาก wizard ได้ — สร้างบริการอย่างเดียว (ข้ามขั้น 2–3)
        var setup = preset.Setup;
        var result = new BookingPresetApplyResult { PresetKey = preset.Key };

        // 1) บริการตัวอย่าง — ผ่าน CreateServiceAsync (ด่านบริการ/ชำระ/ตารางเวลาเดิม) · ชื่อซ้ำ (ยังไม่ลบ) = ใช้ตัวเดิม
        var existing = await _db.SiteBookingServices.AsNoTracking()
            .Where(s => s.CompanyId == companyId && s.SiteId == siteId && !s.IsDeleted)
            .Select(s => new { s.Id, s.Name }).ToListAsync();
        var presetServiceIds = new List<Guid>();
        foreach (var sample in preset.Samples)
        {
            var same = existing.FirstOrDefault(e => string.Equals(e.Name.Trim(), sample.Name.Trim(), StringComparison.Ordinal));
            if (same != null)
            {
                presetServiceIds.Add(same.Id);
                result.Skipped.Add($"บริการ “{sample.Name}” มีอยู่แล้ว — ไม่สร้างซ้ำ (ค่าที่ตั้งไว้คงเดิม)");
                continue;
            }
            var created = await CreateServiceAsync(companyId, siteId, CmsBookingServiceSeeder.ToRequest(sample), userId);
            presetServiceIds.Add(created.Id);
            result.Done.Add($"สร้างบริการ “{sample.Name}” (ราคา ฿{sample.Price:N2} · มัดจำ {sample.DepositPercent:0.##}% · สั่งล่วงหน้า {sample.MinAdvanceHours} ชม.)");
        }

        if (setup == null)
        {
            // บริการบันทึกแล้วใน CreateServiceAsync — ไม่มีค่าตั้งระดับเว็บให้บันทึก
            result.NextSteps.Add("ปรับราคา · เวลาทำการ · ผู้ให้บริการ ให้ตรงร้าน: CMS › บริการจอง");
            return result;
        }
        // 2) จัดส่ง/รับเอง + หน้าต่างเปลี่ยนวิธีรับ (ตั้งเฉพาะที่ยังไม่เคยตั้ง)
        var row = await _db.SiteBookingSettings.FirstOrDefaultAsync(x => x.CompanyId == companyId && x.SiteId == siteId && !x.IsDeleted);
        if (row == null)
        {
            row = new SiteBookingSettings { CompanyId = companyId, SiteId = siteId, CreatedBy = userId };
            _db.SiteBookingSettings.Add(row);
        }
        if (!row.FulfillmentDeliveryEnabled && !row.FulfillmentPickupEnabled)
        {
            row.FulfillmentDeliveryEnabled = setup.Delivery;
            row.FulfillmentPickupEnabled = setup.Pickup;
            result.Done.Add("เปิด " + string.Join(" + ", new[] { setup.Delivery ? "จัดส่ง" : null, setup.Pickup ? "รับเองที่ร้าน" : null }.Where(x => x != null)) + " ของการจอง");
        }
        else result.Skipped.Add("จัดส่ง/รับเองของเว็บตั้งไว้แล้ว — คงค่าเดิม");
        if (row.FulfillmentChangeCutoffHours == null)
        {
            row.FulfillmentChangeCutoffHours = setup.ChangeCutoffHours;
            result.Done.Add(BookingFulfillmentChange.DescribeCutoff(setup.ChangeCutoffHours));
        }
        else result.Skipped.Add("หน้าต่างเปลี่ยนวิธีรับตั้งไว้แล้ว — คงค่าเดิม (" + BookingFulfillmentChange.DescribeCutoff(row.FulfillmentChangeCutoffHours) + ")");

        // 3) ช่องที่ลูกค้ากรอก (ขนาด/รสชาติ/ข้อความ) — ผูกเฉพาะบริการของแม่แบบ · คีย์/ป้ายซ้ำ = ข้าม · ตรวจด้วยตัวตรวจเดียวของหน้าตั้งค่า
        var formWasConfigured = row.FormFieldsJson != null;
        var cfg = BookingFormFields.Parse(row.FormFieldsJson);
        var fields = (cfg.CustomFields ?? Array.Empty<BookingCustomField>()).ToList();
        foreach (var f in setup.CustomFields)
        {
            if (fields.Any(x => CustomFormFields.NormKey(x.Key) == f.Key || CustomFormFields.NormLabel(x.Label) == f.Label))
            {
                result.Skipped.Add($"ช่อง “{f.Label}” มีอยู่แล้ว — ไม่เพิ่มซ้ำ");
                continue;
            }
            fields.Add(f with { ServiceIds = presetServiceIds.Distinct().ToList() });
            result.Done.Add($"เพิ่มช่อง “{f.Label}” บนฟอร์มจอง (เฉพาะบริการของแม่แบบนี้)");
        }
        var next = cfg with { CustomFields = fields };
        var knownIds = await _db.SiteBookingServices.AsNoTracking()
            .Where(s => s.CompanyId == companyId && s.SiteId == siteId && !s.IsDeleted).Select(s => s.Id).ToListAsync();
        if (BookingFormFields.ValidateConfig(next, knownIds) is string formErr)
            throw new BusinessRuleException(formErr, BookingFormFields.RuleCode);
        row.FormFieldsJson = BookingFormFields.Serialize(next);
        if (!formWasConfigured)
            result.Done.Add("ตั้งฟอร์มจองของเว็บ (ชื่อ + เบอร์โทรบังคับ · อีเมล/หมายเหตุไม่บังคับ) — แก้ได้ที่การ์ด “ข้อมูลที่ลูกค้าต้องกรอกตอนจอง”");
        row.UpdatedBy = userId;
        await _db.SaveChangesAsync();

        // 4) สิ่งที่เจ้าของต้องทำต่อ (ระบบไม่ประกาศว่าพร้อมขายเอง — บอกช่องที่ยังขาดจริง)
        var ctx = await LoadPaymentContextAsync(companyId, siteId);
        if (!ctx.TransferReady && !ctx.GatewayReady)
            result.NextSteps.Add("ตั้งช่องทางรับมัดจำ: ตั้งค่าเว็บ › การชำระเงิน (บัญชีโอน/พร้อมเพย์ + แนบสลิป หรือชำระออนไลน์) — ก่อนตั้ง ลูกค้าเห็นยอดมัดจำแต่ต้องติดต่อร้านเพื่อชำระ");
        // รอบ 228 (แพ็ก C · Q3): ตัวตัดสินเดียวกับกลุ่ม "การจัดส่ง / รับเอง" (ส่งฟรีเงียบ)
        if ((await FreeDeliveryStateAsync(companyId, siteId)).Warning is string freeWarn)
            result.NextSteps.Add("ตั้งค่าจัดส่ง: ตั้งค่าเว็บ › การจัดส่ง / รับเอง — " + freeWarn.TrimStart('⚠', '️', ' '));
        if (row.FulfillmentPickupEnabled && string.IsNullOrWhiteSpace(row.FulfillmentPickupNote))
            result.NextSteps.Add("กรอกที่อยู่/เวลารับเอง: ตั้งค่าเว็บ › การจัดส่ง / รับเอง");
        result.NextSteps.Add("ปรับราคา · ความจุต่อรอบ (ชิ้นที่ทำได้) · เวลารับของ · ตัวเลือกขนาด/รสชาติ ให้ตรงร้าน: CMS › บริการจอง");
        result.NextSteps.Add("ต้องการให้การจองยกเลิกเองเมื่อไม่ชำระในเวลา: ตั้งโหมด “มัดจำ (บังคับ)” + “ต้องชำระภายใน” ที่ตั้งค่าเว็บ › การชำระเงิน");
        _logger.LogInformation("ตั้งค่าเว็บ {SiteId} จากแม่แบบ {Preset} โดย {User}: ทำ {Done} ข้าม {Skipped}", siteId, preset.Key, userId, result.Done.Count, result.Skipped.Count);
        return result;
    }
}
