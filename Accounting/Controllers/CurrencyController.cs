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
}
