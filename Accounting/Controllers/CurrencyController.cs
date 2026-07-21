using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Currency;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/[controller]")]
[Authorize]
public class CurrencyController : ControllerBase
{
    private readonly ICurrencyService _currencyService;

    public CurrencyController(ICurrencyService currencyService)
    {
        _currencyService = currencyService;
    }

    // ===== Company Currencies =====

    [HttpGet]
    public async Task<ActionResult<ApiResponse<List<CompanyCurrencyResponse>>>> GetCurrencies(Guid companyId)
    {
        var result = await _currencyService.GetCurrenciesAsync(companyId);
        return Ok(new ApiResponse<List<CompanyCurrencyResponse>>(true, result));
    }

    [HttpGet("{currencyId:guid}")]
    public async Task<ActionResult<ApiResponse<CompanyCurrencyResponse>>> GetCurrency(Guid companyId, Guid currencyId)
    {
        var result = await _currencyService.GetCurrencyByIdAsync(companyId, currencyId);
        return Ok(new ApiResponse<CompanyCurrencyResponse>(true, result));
    }

    [HttpPost]
    public async Task<ActionResult<ApiResponse<CompanyCurrencyResponse>>> AddCurrency(
        Guid companyId, [FromBody] CreateCompanyCurrencyRequest request)
    {
        var result = await _currencyService.AddCurrencyAsync(companyId, request);
        return StatusCode(201, new ApiResponse<CompanyCurrencyResponse>(true, result, "เพิ่มสกุลเงินสำเร็จ"));
    }

    [HttpPut("{currencyId:guid}")]
    public async Task<ActionResult<ApiResponse<CompanyCurrencyResponse>>> UpdateCurrency(
        Guid companyId, Guid currencyId, [FromBody] UpdateCompanyCurrencyRequest request)
    {
        var result = await _currencyService.UpdateCurrencyAsync(companyId, currencyId, request);
        return Ok(new ApiResponse<CompanyCurrencyResponse>(true, result));
    }

    // ===== Exchange Rates =====

    [HttpGet("rates")]
    public async Task<ActionResult<ApiResponse<List<CurrencyRateResponse>>>> GetRates(
        Guid companyId, [FromQuery] string? from = null, [FromQuery] string? to = null)
    {
        var result = await _currencyService.GetRatesAsync(companyId, from, to);
        return Ok(new ApiResponse<List<CurrencyRateResponse>>(true, result));
    }

    [HttpPost("rates")]
    public async Task<ActionResult<ApiResponse<CurrencyRateResponse>>> AddRate(
        Guid companyId, [FromBody] CreateCurrencyRateRequest request)
    {
        var result = await _currencyService.AddRateAsync(companyId, request);
        return StatusCode(201, new ApiResponse<CurrencyRateResponse>(true, result, "เพิ่มอัตราแลกเปลี่ยนสำเร็จ"));
    }

    [HttpGet("rates/latest")]
    public async Task<ActionResult<ApiResponse<CurrencyRateResponse?>>> GetLatestRate(
        Guid companyId, [FromQuery] string from, [FromQuery] string to)
    {
        var result = await _currencyService.GetLatestRateAsync(companyId, from, to);
        return Ok(new ApiResponse<CurrencyRateResponse?>(true, result));
    }

    [HttpDelete("{currencyId:guid}")]
    public async Task<ActionResult<ApiResponse<bool>>> DeleteCurrency(Guid companyId, Guid currencyId)
    {
        await _currencyService.DeleteCurrencyAsync(companyId, currencyId);
        return Ok(new ApiResponse<bool>(true, true, "ลบสกุลเงินสำเร็จ"));
    }

    // ===== Conversion & Gain/Loss =====

    [HttpGet("convert")]
    public async Task<ActionResult<ApiResponse<decimal>>> Convert(
        Guid companyId, [FromQuery] string from, [FromQuery] string to,
        [FromQuery] decimal amount, [FromQuery] DateTime? asOfDate = null, [FromQuery] string? direction = null)
    {
        var result = await _currencyService.ConvertAsync(companyId, from, to, amount, asOfDate, direction);
        return Ok(new ApiResponse<decimal>(true, result));
    }

    [HttpGet("unrealized-gain-loss")]
    public async Task<ActionResult<ApiResponse<List<UnrealizedGainLossItem>>>> GetUnrealizedGainLoss(
        Guid companyId, [FromQuery] string baseCurrency, [FromQuery] DateTime asOfDate)
    {
        var result = await _currencyService.CalculateUnrealizedGainLossAsync(companyId, baseCurrency, asOfDate);
        return Ok(new ApiResponse<List<UnrealizedGainLossItem>>(true, result));
    }
}
