using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Domain.Members;

namespace IdentityGateway.Infrastructure.Persistence.Repositories;

/// <summary>
/// Implementa <see cref="IMemberRepository"/> sobre o <see cref="AppDbContext"/>.
/// </summary>
internal sealed class MemberRepository(AppDbContext context) : IMemberRepository
{
    /// <inheritdoc />
    public void Add(Member member) => context.Members.Add(member);
}
