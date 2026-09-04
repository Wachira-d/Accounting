using Accounting.Models.DTOs.Auth;
using Accounting.Models.DTOs.Budget;
using Accounting.Models.DTOs.Expense;
using Accounting.Models.DTOs.FixedAsset;
using Accounting.Models.DTOs.Bank;
using Accounting.Models.DTOs.Import;
using FluentValidation;

namespace Accounting.Validators;

// ===== Auth =====

public class RegisterRequestValidator : AbstractValidator<RegisterRequest>
{
    public RegisterRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().WithMessage("รูปแบบอีเมลไม่ถูกต้อง");
        RuleFor(x => x.Password).NotEmpty().MinimumLength(8).WithMessage("รหัสผ่านต้องมีอย่างน้อย 8 ตัวอักษร");

        // ชื่อจากหน้าสมัคร (ทางเข้าที่ **ไม่ต้องล็อกอิน**) ไหลไปโผล่ในหน้าจอของ
        // SystemAdmin · PDF · อีเมล · XML e-Tax — ที่ที่ตัวหนีของหน้าเว็บไปไม่ถึง
        // (ผลตรวจ F-01) ดูเหตุผลเต็มใน Helpers/DisplayText
        RuleFor(x => x.FullName).SafeDisplayName("ชื่อ-นามสกุล");
        RuleFor(x => x.FirstName).SafeDisplayName("ชื่อ");
        RuleFor(x => x.LastName).SafeDisplayName("นามสกุล");
        RuleFor(x => x.CompanyName).SafeDisplayName("ชื่อบริษัท");
    }
}

/// <summary>ชื่อที่คนอื่น (รวม SystemAdmin) ต้องมองเห็น — กติกาเดียวทั้งระบบ
/// เพื่อไม่ให้แต่ละฟอร์มไปเขียนเงื่อนไขเอง (สำเนามือ = drift รอบหน้า)</summary>
public static class DisplayNameRules
{
    public static IRuleBuilderOptions<T, string?> SafeDisplayName<T>(
        this IRuleBuilder<T, string?> rule, string fieldLabel,
        int maxLength = Helpers.DisplayText.MaxNameLength)
        => rule.Must(v => Helpers.DisplayText.IsSafeName(v, maxLength))
               // ข้อความต้องบอกว่าติดตรงไหน (ยาวเกิน vs อักขระต้องห้าม) จึงต้องเห็นค่าจริง
               .WithMessage((_, v) => Helpers.DisplayText.RejectReason(fieldLabel, v, maxLength));
}

public class CreateCompanyRequestValidator : AbstractValidator<Models.DTOs.Company.CreateCompanyRequest>
{
    public CreateCompanyRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithMessage("กรุณากรอกชื่อบริษัท")
                            .SafeDisplayName("ชื่อบริษัท");
        RuleFor(x => x.NameEn).SafeDisplayName("ชื่อบริษัท (อังกฤษ)");
        RuleFor(x => x.BranchName).SafeDisplayName("ชื่อสาขา");
    }
}

public class UpdateCompanyRequestValidator : AbstractValidator<Models.DTOs.Company.UpdateCompanyRequest>
{
    public UpdateCompanyRequestValidator()
    {
        RuleFor(x => x.Name).SafeDisplayName("ชื่อบริษัท");
        RuleFor(x => x.NameEn).SafeDisplayName("ชื่อบริษัท (อังกฤษ)");
        RuleFor(x => x.BranchName).SafeDisplayName("ชื่อสาขา");
    }
}

public class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().WithMessage("กรุณากรอกอีเมล");
        RuleFor(x => x.Password).NotEmpty().WithMessage("กรุณากรอกรหัสผ่าน");
    }
}

public class ChangePasswordRequestValidator : AbstractValidator<ChangePasswordRequest>
{
    public ChangePasswordRequestValidator()
    {
        RuleFor(x => x.CurrentPassword).NotEmpty().WithMessage("กรุณากรอกรหัสผ่านเดิม");
        RuleFor(x => x.NewPassword).NotEmpty().MinimumLength(8).WithMessage("รหัสผ่านใหม่ต้องมีอย่างน้อย 8 ตัวอักษร");
    }
}

public class ForgotPasswordRequestValidator : AbstractValidator<ForgotPasswordRequest>
{
    public ForgotPasswordRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().WithMessage("กรุณากรอกอีเมลที่ถูกต้อง");
    }
}

public class ResetPasswordRequestValidator : AbstractValidator<ResetPasswordRequest>
{
    public ResetPasswordRequestValidator()
    {
        RuleFor(x => x.Token).NotEmpty().WithMessage("Token ไม่ถูกต้อง");
        RuleFor(x => x.NewPassword).NotEmpty().MinimumLength(8).WithMessage("รหัสผ่านใหม่ต้องมีอย่างน้อย 8 ตัวอักษร");
    }
}

// ===== Budget =====

public class CreateBudgetRequestValidator : AbstractValidator<CreateBudgetRequest>
{
    public CreateBudgetRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200).WithMessage("กรุณากรอกชื่องบประมาณ");
        RuleFor(x => x.FiscalYear).InclusiveBetween(2020, 2100).WithMessage("ปีงบประมาณต้องอยู่ระหว่าง 2020-2100");
        RuleFor(x => x.Lines).NotEmpty().WithMessage("ต้องมีรายการอย่างน้อย 1 รายการ");
        RuleForEach(x => x.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.AccountId).NotEmpty().WithMessage("กรุณาเลือกบัญชี");
        });
    }
}

// ===== Expense Claim =====

public class CreateExpenseClaimRequestValidator : AbstractValidator<CreateExpenseClaimRequest>
{
    public CreateExpenseClaimRequestValidator()
    {
        RuleFor(x => x.Title).NotEmpty().MaximumLength(300).WithMessage("กรุณากรอกหัวข้อ");
        RuleFor(x => x.ExpenseDate).NotEmpty().WithMessage("กรุณาระบุวันที่");
        RuleFor(x => x.Lines).NotEmpty().WithMessage("ต้องมีรายการอย่างน้อย 1 รายการ");
        RuleForEach(x => x.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.Description).NotEmpty().WithMessage("กรุณากรอกรายละเอียด");
            line.RuleFor(l => l.Amount).GreaterThan(0).WithMessage("จำนวนเงินต้องมากกว่า 0");
        });
    }
}

// ===== Fixed Asset =====

public class CreateFixedAssetRequestValidator : AbstractValidator<CreateFixedAssetRequest>
{
    public CreateFixedAssetRequestValidator()
    {
        RuleFor(x => x.AssetCode).NotEmpty().MaximumLength(50).WithMessage("กรุณากรอกรหัสสินทรัพย์");
        RuleFor(x => x.Name).NotEmpty().MaximumLength(300).WithMessage("กรุณากรอกชื่อสินทรัพย์");
        RuleFor(x => x.PurchaseCost).GreaterThan(0).WithMessage("ราคาซื้อต้องมากกว่า 0");
        RuleFor(x => x.SalvageValue).GreaterThanOrEqualTo(0).WithMessage("มูลค่าซาก​ต้องไม่น้อยกว่า 0");
        RuleFor(x => x.UsefulLifeMonths).GreaterThan(0).WithMessage("อายุการใช้งานต้องมากกว่า 0 เดือน");
        RuleFor(x => x.SalvageValue).LessThan(x => x.PurchaseCost).WithMessage("มูลค่าซากต้องน้อยกว่าราคาซื้อ");
    }
}

public class RevalueAssetRequestValidator : AbstractValidator<RevalueAssetRequest>
{
    public RevalueAssetRequestValidator()
    {
        RuleFor(x => x.NewFairValue).GreaterThan(0).WithMessage("มูลค่ายุติธรรมต้องมากกว่า 0");
        RuleFor(x => x.RevaluationDate).NotEmpty().WithMessage("กรุณาระบุวันที่ตีราคา");
    }
}

// ===== Bank =====

public class CreateBankAccountRequestValidator : AbstractValidator<CreateBankAccountRequest>
{
    public CreateBankAccountRequestValidator()
    {
        RuleFor(x => x.AccountName).NotEmpty().MaximumLength(200).WithMessage("กรุณากรอกชื่อบัญชี");
        RuleFor(x => x.AccountNumber).NotEmpty().MaximumLength(50).WithMessage("กรุณากรอกเลขที่บัญชี");
        RuleFor(x => x.BankName).NotEmpty().MaximumLength(100).WithMessage("กรุณากรอกชื่อธนาคาร");
    }
}

public class ImportBankStatementRequestValidator : AbstractValidator<ImportBankStatementRequest>
{
    public ImportBankStatementRequestValidator()
    {
        RuleFor(x => x.BankAccountId).NotEmpty().WithMessage("กรุณาเลือกบัญชีธนาคาร");
        // ตรงกับที่ BankService รองรับจริง: CSV และ Excel เท่านั้น (OFX/QIF/MT940
        // ไม่เคยมี parser — เดิม validator รับแล้วไปตายที่ service ทำให้ผู้ใช้งง)
        RuleFor(x => x.FileFormat).NotEmpty()
            .Must(f => f is "CSV" or "EXCEL" or "XLSX")
            .WithMessage("รูปแบบไฟล์ต้องเป็น CSV หรือ Excel (.xlsx)");
        RuleFor(x => x.Base64Content).NotEmpty().WithMessage("กรุณาอัพโหลดไฟล์");
    }
}

// ===== Import =====

public class ImportRequestValidator : AbstractValidator<ImportRequest>
{
    public ImportRequestValidator()
    {
        RuleFor(x => x.EntityType).NotEmpty().WithMessage("กรุณาระบุประเภทข้อมูล");
        RuleFor(x => x.Data).NotEmpty().WithMessage("ไม่มีข้อมูลสำหรับนำเข้า");
    }
}
