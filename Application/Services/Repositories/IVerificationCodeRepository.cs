// Application/Services/Repositories/IVerificationCodeRepository.cs
using Domain.Entities;
using System.Threading.Tasks;

namespace Application.Services.Repositories
{
    public interface IVerificationCodeRepository
    {
        Task<VerificationCode> AddAsync(VerificationCode entity);
        Task<VerificationCode?> GetByEmailAndCodeAsync(string email, string code);
        Task DeleteAsync(VerificationCode entity);
        Task DeleteByEmailAsync(string email);
    }
}
