using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Auth;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly IAuthService _authService;

    public AuthController(IAuthService authService)
    {
        _authService = authService;
    }

    [HttpPost("register")]
    public async Task<ActionResult<ApiResponse<LoginResponse>>> Register([FromBody] RegisterRequest request)
    {
        var result = await _authService.RegisterAsync(request);
        return StatusCode(201, new ApiResponse<LoginResponse>(true, result, "ลงทะเบียนสำเร็จ"));
    }

    [HttpPost("login")]
    public async Task<ActionResult<ApiResponse<LoginResponse>>> Login([FromBody] LoginRequest request)
    {
        var result = await _authService.LoginAsync(request);
        return Ok(new ApiResponse<LoginResponse>(true, result, "เข้าสู่ระบบสำเร็จ"));
    }

    [HttpPost("refresh")]
    public async Task<ActionResult<ApiResponse<LoginResponse>>> Refresh([FromBody] RefreshTokenRequest request)
    {
        var result = await _authService.RefreshTokenAsync(request.RefreshToken);
        return Ok(new ApiResponse<LoginResponse>(true, result));
    }

    [Authorize]
    [HttpPost("change-password")]
    public async Task<ActionResult<ApiResponse<string>>> ChangePassword([FromBody] ChangePasswordRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        await _authService.ChangePasswordAsync(userId, request);
        return Ok(new ApiResponse<string>(true, null, "เปลี่ยนรหัสผ่านสำเร็จ"));
    }

    [HttpPost("forgot-password")]
    public async Task<ActionResult<ApiResponse<string>>> ForgotPassword([FromBody] ForgotPasswordRequest request)
    {
        var message = await _authService.ForgotPasswordAsync(request.Email);
        return Ok(new ApiResponse<string>(true, null, message));
    }

    [HttpPost("reset-password")]
    public async Task<ActionResult<ApiResponse<string>>> ResetPassword([FromBody] ResetPasswordRequest request)
    {
        await _authService.ResetPasswordAsync(request.Token, request.NewPassword);
        return Ok(new ApiResponse<string>(true, null, "รีเซ็ตรหัสผ่านสำเร็จ"));
    }
}
