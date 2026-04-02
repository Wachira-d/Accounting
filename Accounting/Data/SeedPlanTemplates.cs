using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Data;

/// <summary>
/// Seed ข้อมูลแพ็กเกจราคาเริ่มต้น (Plan Templates)
/// ตั้งค่าแพ็กเกจ 4 ระดับ พร้อมรายละเอียดการทดลองใช้
/// </summary>
public static class SeedPlanTemplates
{
    public static async Task SeedAsync(AccountingDbContext db)
    {
        if (await db.PlanTemplates.AnyAsync())
            return; // มีข้อมูลแล้ว ไม่ต้อง seed

        var plans = new List<PlanTemplate>
        {
            // ===== 1. Starter (Basic) =====
            new PlanTemplate
            {
                Id = Guid.NewGuid(),
                Name = "Starter",
                Description = "สำหรับฟรีแลนซ์และธุรกิจขนาดเล็กที่เพิ่งเริ่มต้น เหมาะกับร้านค้าออนไลน์ ร้านอาหาร หรือธุรกิจบริการที่ต้องการระบบบัญชีพื้นฐาน ออกใบกำกับภาษี จัดการภาษีมูลค่าเพิ่ม และดูรายงานทางการเงินเบื้องต้น",
                Plan = SubscriptionPlan.Basic,
                IsActive = true,
                Currency = "THB",

                // ราคา
                MonthlyPrice = 590m,
                QuarterlyPrice = 1_680m,       // 560/เดือน (ประหยัด 5%)
                SemiAnnualPrice = 3_186m,      // 531/เดือน (ประหยัด 10%)
                AnnualPrice = 6_018m,           // 501.50/เดือน (ประหยัด 15%)

                // ลิมิต
                MaxUsers = 2,
                MaxCompanies = 1,
                MaxDocumentsPerMonth = 100,
                MaxJournalEntriesPerMonth = 200,
                MaxStorageBytes = 500L * 1024 * 1024, // 500 MB

                // ฟีเจอร์
                EnabledFeatures = FeatureFlags.BasicFeatures,

                // ตั้งค่าทดลองใช้
                TrialDurationDays = 14,
                TrialMaxExtensions = 1,
                TrialExtensionDays = 7,
                TrialFeatures = FeatureFlags.BasicFeatures,
                TrialMaxUsers = 2,
                TrialMaxDocumentsPerMonth = 30,
                TrialMaxJournalEntriesPerMonth = 50,
                TrialBlockOnExpiry = false,
                TrialGracePeriodDays = 7,

                CreatedAt = DateTime.UtcNow,
            },

            // ===== 2. Professional (Pro) =====
            new PlanTemplate
            {
                Id = Guid.NewGuid(),
                Name = "Professional",
                Description = "สำหรับ SME ที่กำลังเติบโต ต้องการระบบบัญชีครบวงจร รองรับหลายผู้ใช้งาน ระบบสต็อกสินค้า กระทบยอดธนาคาร รายงานขั้นสูง งบประมาณ ภาษีครบวงจร ระบบอนุมัติ และเชื่อมต่อ API สำหรับธุรกิจที่ต้องการความยืดหยุ่นสูง",
                Plan = SubscriptionPlan.Pro,
                IsActive = true,
                Currency = "THB",

                // ราคา
                MonthlyPrice = 1_490m,
                QuarterlyPrice = 4_248m,       // 1,416/เดือน (ประหยัด 5%)
                SemiAnnualPrice = 8_046m,      // 1,341/เดือน (ประหยัด 10%)
                AnnualPrice = 15_198m,          // 1,266.50/เดือน (ประหยัด 15%)

                // ลิมิต
                MaxUsers = 10,
                MaxCompanies = 3,
                MaxDocumentsPerMonth = 1_000,
                MaxJournalEntriesPerMonth = 2_000,
                MaxStorageBytes = 5L * 1024 * 1024 * 1024, // 5 GB

                // ฟีเจอร์
                EnabledFeatures = FeatureFlags.ProFeatures,

                // ตั้งค่าทดลองใช้
                TrialDurationDays = 14,
                TrialMaxExtensions = 2,
                TrialExtensionDays = 7,
                TrialFeatures = FeatureFlags.ProFeatures,
                TrialMaxUsers = 3,
                TrialMaxDocumentsPerMonth = 50,
                TrialMaxJournalEntriesPerMonth = 100,
                TrialBlockOnExpiry = false,
                TrialGracePeriodDays = 14,

                CreatedAt = DateTime.UtcNow,
            },

            // ===== 3. Enterprise =====
            new PlanTemplate
            {
                Id = Guid.NewGuid(),
                Name = "Enterprise",
                Description = "สำหรับองค์กรขนาดใหญ่และกลุ่มบริษัท ครบทุกฟีเจอร์ รองรับ Multi-branch, Consolidation, Intercompany, ระบบเงินเดือน, AI อัจฉริยะ, e-Tax, Webhook, Open Banking, ระบบคลังสินค้า, สินเชื่อ, คอมมิชชั่น และรายงานขั้นสูง พร้อม Priority Support",
                Plan = SubscriptionPlan.Enterprise,
                IsActive = true,
                Currency = "THB",

                // ราคา
                MonthlyPrice = 4_990m,
                QuarterlyPrice = 14_222m,      // 4,740.67/เดือน (ประหยัด 5%)
                SemiAnnualPrice = 26_946m,     // 4,491/เดือน (ประหยัด 10%)
                AnnualPrice = 50_898m,          // 4,241.50/เดือน (ประหยัด 15%)

                // ลิมิต
                MaxUsers = 999,  // ไม่จำกัดในทางปฏิบัติ
                MaxCompanies = 99,
                MaxDocumentsPerMonth = 99_999,
                MaxJournalEntriesPerMonth = 99_999,
                MaxStorageBytes = 50L * 1024 * 1024 * 1024, // 50 GB

                // ฟีเจอร์
                EnabledFeatures = FeatureFlags.EnterpriseFeatures,

                // ตั้งค่าทดลองใช้
                TrialDurationDays = 30,
                TrialMaxExtensions = 3,
                TrialExtensionDays = 15,
                TrialFeatures = FeatureFlags.EnterpriseFeatures,
                TrialMaxUsers = 5,
                TrialMaxDocumentsPerMonth = 100,
                TrialMaxJournalEntriesPerMonth = 200,
                TrialBlockOnExpiry = false,
                TrialGracePeriodDays = 30,

                CreatedAt = DateTime.UtcNow,
            },

            // ===== 4. Free Trial (เฉพาะทดลองใช้) =====
            new PlanTemplate
            {
                Id = Guid.NewGuid(),
                Name = "ทดลองใช้ฟรี",
                Description = "ทดลองใช้งานระบบบัญชี Next Acc ฟรี 14 วัน ไม่ต้องผูกบัตรเครดิต ใช้งานฟีเจอร์พื้นฐานครบถ้วน ออกเอกสาร ดูรายงาน จัดการภาษี ยกเลิกได้ตลอดเวลา",
                Plan = SubscriptionPlan.FreeTrial,
                IsActive = true,
                Currency = "THB",

                // ราคา (ฟรี)
                MonthlyPrice = 0m,
                QuarterlyPrice = 0m,
                SemiAnnualPrice = 0m,
                AnnualPrice = 0m,

                // ลิมิต
                MaxUsers = 2,
                MaxCompanies = 1,
                MaxDocumentsPerMonth = 30,
                MaxJournalEntriesPerMonth = 50,
                MaxStorageBytes = 100L * 1024 * 1024, // 100 MB

                // ฟีเจอร์
                EnabledFeatures = FeatureFlags.TrialFeatures,

                // ตั้งค่าทดลองใช้
                TrialDurationDays = 14,
                TrialMaxExtensions = 1,
                TrialExtensionDays = 7,
                TrialFeatures = FeatureFlags.TrialFeatures,
                TrialMaxUsers = 2,
                TrialMaxDocumentsPerMonth = 30,
                TrialMaxJournalEntriesPerMonth = 50,
                TrialBlockOnExpiry = false,
                TrialGracePeriodDays = 7,

                CreatedAt = DateTime.UtcNow,
            },
        };

        db.PlanTemplates.AddRange(plans);
        await db.SaveChangesAsync();
    }
}
