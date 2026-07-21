using Accounting.Models.DTOs;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/gov")]
[Authorize]
public class GovernmentServiceController : ControllerBase
{
    private readonly IBotExchangeRateService _botRates;
    private readonly IThaiAddressService _address;
    private readonly IThaiGovIntegrationService _govService;
    private readonly IShippingTrackingService _shipping;
    private readonly IPromptPayService _promptPay;

    public GovernmentServiceController(
        IBotExchangeRateService botRates,
        IThaiAddressService address,
        IThaiGovIntegrationService govService,
        IShippingTrackingService shipping,
        IPromptPayService promptPay)
    {
        _botRates = botRates;
        _address = address;
        _govService = govService;
        _shipping = shipping;
        _promptPay = promptPay;
    }

    // ===== BOT Exchange Rates =====

    [HttpGet("bot/rates")]
    public async Task<ActionResult<ApiResponse<List<BotExchangeRate>>>> GetBotRates([FromQuery] DateTime? date = null)
    {
        var result = await _botRates.GetDailyRatesAsync(date);
        return Ok(new ApiResponse<List<BotExchangeRate>>(true, result));
    }

    [HttpGet("bot/rates/{currencyCode}")]
    public async Task<ActionResult<ApiResponse<BotExchangeRate?>>> GetBotRate(string currencyCode, [FromQuery] DateTime? date = null)
    {
        var result = await _botRates.GetRateAsync(currencyCode, date);
        return Ok(new ApiResponse<BotExchangeRate?>(true, result));
    }

    [HttpPost("bot/rates/sync/{companyId:guid}")]
    public async Task<ActionResult<ApiResponse<int>>> SyncBotRates(Guid companyId, [FromQuery] DateTime? date = null)
    {
        var count = await _botRates.SyncRatesToCompanyAsync(companyId, date);
        return Ok(new ApiResponse<int>(true, count, $"นำเข้าอัตราแลกเปลี่ยน {count} สกุลเงินจาก ธปท."));
    }

    // ===== Thai Address Autocomplete =====

    [HttpGet("address/search")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<List<ThaiAddressResult>>>> SearchAddress([FromQuery] string q, [FromQuery] int limit = 20)
    {
        var result = await _address.SearchAsync(q, limit);
        return Ok(new ApiResponse<List<ThaiAddressResult>>(true, result));
    }

    [HttpGet("address/postal/{postalCode}")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<List<ThaiAddressResult>>>> GetByPostalCode(string postalCode)
    {
        var result = await _address.GetByPostalCodeAsync(postalCode);
        return Ok(new ApiResponse<List<ThaiAddressResult>>(true, result));
    }

    [HttpGet("address/provinces")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<List<ThaiProvinceInfo>>>> GetProvinces()
    {
        var result = await _address.GetProvincesAsync();
        return Ok(new ApiResponse<List<ThaiProvinceInfo>>(true, result));
    }

    [HttpGet("address/districts/{provinceCode}")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<List<ThaiDistrictInfo>>>> GetDistricts(string provinceCode)
    {
        var result = await _address.GetDistrictsAsync(provinceCode);
        return Ok(new ApiResponse<List<ThaiDistrictInfo>>(true, result));
    }

    [HttpGet("address/subdistricts/{districtCode}")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<List<ThaiSubDistrictInfo>>>> GetSubDistricts(string districtCode)
    {
        var result = await _address.GetSubDistrictsAsync(districtCode);
        return Ok(new ApiResponse<List<ThaiSubDistrictInfo>>(true, result));
    }

    // ===== HS Code / Customs =====

    [HttpGet("customs/hs-codes")]
    public async Task<ActionResult<ApiResponse<List<HsCodeResult>>>> SearchHsCodes([FromQuery] string q, [FromQuery] int limit = 20)
    {
        var result = await _govService.SearchHsCodeAsync(q, limit);
        return Ok(new ApiResponse<List<HsCodeResult>>(true, result));
    }

    [HttpGet("customs/hs-codes/{hsCode}")]
    public async Task<ActionResult<ApiResponse<HsCodeResult?>>> GetHsCode(string hsCode)
    {
        var result = await _govService.GetHsCodeAsync(hsCode);
        return Ok(new ApiResponse<HsCodeResult?>(true, result));
    }

    [HttpGet("customs/duty/{hsCode}")]
    public async Task<ActionResult<ApiResponse<CustomsDutyInfo?>>> GetDutyRate(string hsCode)
    {
        var result = await _govService.GetImportDutyRateAsync(hsCode);
        return Ok(new ApiResponse<CustomsDutyInfo?>(true, result));
    }

    // ===== Tax Rates =====

    [HttpGet("tax/vat-rate")]
    public async Task<ActionResult<ApiResponse<RdVatRateInfo>>> GetVatRate()
    {
        var result = await _govService.GetCurrentVatRateAsync();
        return Ok(new ApiResponse<RdVatRateInfo>(true, result));
    }

    [HttpGet("tax/wht-rates")]
    public async Task<ActionResult<ApiResponse<List<RdWhtRateInfo>>>> GetWhtRates()
    {
        var result = await _govService.GetWhtRatesAsync();
        return Ok(new ApiResponse<List<RdWhtRateInfo>>(true, result));
    }

    [HttpGet("tax/sso-rate")]
    public async Task<ActionResult<ApiResponse<SsoContributionRate>>> GetSsoRate()
    {
        var result = await _govService.GetCurrentSsoRateAsync();
        return Ok(new ApiResponse<SsoContributionRate>(true, result));
    }

    [HttpGet("tax/branch/{taxId}/{branchCode}")]
    public async Task<ActionResult<ApiResponse<RdBranchInfo?>>> LookupBranch(string taxId, string branchCode)
    {
        var result = await _govService.LookupBranchAsync(taxId, branchCode);
        return Ok(new ApiResponse<RdBranchInfo?>(true, result));
    }

    // ===== ETDA e-Timestamp =====

    [HttpPost("etda/timestamp")]
    public async Task<ActionResult<ApiResponse<TimestampResponse>>> RequestTimestamp([FromBody] TimestampRequest request)
    {
        var hash = Convert.FromBase64String(request.DocumentHashBase64);
        var result = await _govService.RequestTimestampAsync(hash, request.HashAlgorithm);
        return Ok(new ApiResponse<TimestampResponse>(true, result));
    }

    [HttpPost("etda/verify-timestamp")]
    public async Task<ActionResult<ApiResponse<bool>>> VerifyTimestamp([FromBody] VerifyTimestampRequest request)
    {
        var token = Convert.FromBase64String(request.TimestampTokenBase64);
        var isValid = await _govService.VerifyTimestampAsync(token);
        return Ok(new ApiResponse<bool>(true, isValid, isValid ? "Timestamp ถูกต้อง" : "Timestamp ไม่ถูกต้อง"));
    }

    // ===== Shipping & Tracking =====

    [HttpGet("shipping/track/{trackingNumber}")]
    public async Task<ActionResult<ApiResponse<ShipmentTrackingResult?>>> Track(string trackingNumber, [FromQuery] string? carrier = null)
    {
        var result = await _shipping.TrackAsync(trackingNumber, carrier);
        return Ok(new ApiResponse<ShipmentTrackingResult?>(true, result));
    }

    [HttpPost("shipping/track/batch")]
    public async Task<ActionResult<ApiResponse<List<ShipmentTrackingResult>>>> TrackBatch([FromBody] List<string> trackingNumbers)
    {
        var result = await _shipping.TrackBatchAsync(trackingNumbers);
        return Ok(new ApiResponse<List<ShipmentTrackingResult>>(true, result));
    }

    [HttpGet("shipping/detect-carrier/{trackingNumber}")]
    public ActionResult<ApiResponse<string?>> DetectCarrier(string trackingNumber)
    {
        var result = _shipping.DetectCarrier(trackingNumber);
        return Ok(new ApiResponse<string?>(true, result));
    }

    [HttpPost("shipping/rates")]
    public async Task<ActionResult<ApiResponse<List<ShippingRateQuote>>>> GetShippingRates([FromBody] ShippingRateRequest request)
    {
        var result = await _shipping.GetShippingRatesAsync(request);
        return Ok(new ApiResponse<List<ShippingRateQuote>>(true, result));
    }

    // ===== PromptPay =====

    [HttpGet("promptpay/qr")]
    public ActionResult GeneratePromptPayQr([FromQuery] string promptPayId, [FromQuery] decimal amount, [FromQuery] int size = 300)
    {
        if (!_promptPay.ValidatePromptPayId(promptPayId))
            return BadRequest(new ApiResponse<string>(false, null, "PromptPay ID ไม่ถูกต้อง"));

        var imageBytes = _promptPay.GenerateQrImage(promptPayId, amount, size);
        return File(imageBytes, "image/png", $"promptpay-{amount:F2}.png");
    }

    [HttpGet("promptpay/payload")]
    public ActionResult<ApiResponse<string>> GetPromptPayPayload(
        [FromQuery] string promptPayId, [FromQuery] decimal amount,
        [FromQuery] string? ref1 = null, [FromQuery] string? ref2 = null)
    {
        if (!_promptPay.ValidatePromptPayId(promptPayId))
            return BadRequest(new ApiResponse<string>(false, null, "PromptPay ID ไม่ถูกต้อง"));

        var payload = _promptPay.GenerateQrPayload(promptPayId, amount, ref1, ref2);
        return Ok(new ApiResponse<string>(true, payload));
    }

    [HttpGet("promptpay/validate/{promptPayId}")]
    public ActionResult<ApiResponse<bool>> ValidatePromptPayId(string promptPayId)
    {
        var isValid = _promptPay.ValidatePromptPayId(promptPayId);
        return Ok(new ApiResponse<bool>(true, isValid, isValid ? "PromptPay ID ถูกต้อง" : "PromptPay ID ไม่ถูกต้อง"));
    }

    // ===== NSW Status =====

    [HttpGet("nsw/status")]
    public async Task<ActionResult<ApiResponse<bool>>> CheckNswStatus()
    {
        var isUp = await _govService.CheckNswConnectionAsync();
        return Ok(new ApiResponse<bool>(true, isUp, isUp ? "NSW เชื่อมต่อได้" : "NSW ไม่สามารถเชื่อมต่อได้"));
    }
}

public record TimestampRequest(string DocumentHashBase64, string HashAlgorithm = "SHA256");
public record VerifyTimestampRequest(string TimestampTokenBase64);
