// Persistence/Repositories/EfVerificationCodeRepository.cs
using System.Threading.Tasks;
using Application.Services.Repositories;
using Domain.Entities;
using Persistence.Contexts;
using Microsoft.EntityFrameworkCore;

namespace Persistence.Repositories
{
    public class EfVerificationCodeRepository : IVerificationCodeRepository
    {
        private readonly AppDbContext _context;
        public EfVerificationCodeRepository(AppDbContext context) => _context = context;

        public async Task<VerificationCode> AddAsync(VerificationCode entity)
        {
            var added = await _context.VerificationCodes.AddAsync(entity);
            await _context.SaveChangesAsync();
            return added.Entity;
        }

        public async Task<VerificationCode?> GetByEmailAndCodeAsync(string email, string code)
        {
            return await _context.VerificationCodes
                .FirstOrDefaultAsync(x => x.Email == email && x.Code == code);
        }

        public async Task DeleteAsync(VerificationCode entity)
        {
            _context.VerificationCodes.Remove(entity);
            await _context.SaveChangesAsync();
        }

        public async Task DeleteByEmailAsync(string email)
        {
            _context.VerificationCodes.RemoveRange(_context.VerificationCodes.Where(x => x.Email == email));
            await _context.SaveChangesAsync();
        }
    }
}
