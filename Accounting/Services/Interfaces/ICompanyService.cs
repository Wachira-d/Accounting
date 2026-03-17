using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Company;
using Accounting.Models.Enums;

namespace Accounting.Services.Interfaces;

public interface ICompanyService
{
    Task<CompanyResponse> CreateAsync(Guid userId, CreateCompanyRequest request);
    Task<CompanyResponse> GetByIdAsync(Guid companyId, Guid userId);
    Task<List<CompanyResponse>> GetUserCompaniesAsync(Guid userId);
    Task<CompanyResponse> UpdateAsync(Guid companyId, Guid userId, UpdateCompanyRequest request);
    Task AddUserAsync(Guid companyId, Guid ownerId, AddCompanyUserRequest request);
    Task RemoveUserAsync(Guid companyId, Guid ownerId, Guid targetUserId);
}
