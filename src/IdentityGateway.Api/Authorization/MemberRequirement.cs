using Microsoft.AspNetCore.Authorization;

namespace IdentityGateway.Api.Authorization;

/// <summary>
/// A pertença no banco (ADR-011): o ator é <c>Member</c> do tenant da rota.
/// </summary>
/// <remarks>
/// O <c>tenant_id</c> do token é forjável por quem tem a chave da Gateway (um grupo do Keycloak com o atributo dá o
/// claim a qualquer usuário posto nele). Nas rotas de governança, o token não basta: o banco da Gateway confirma. Ao
/// contrário dos outros três requirements da policy, este não é o próprio handler — precisa de um serviço, e quem o
/// atende é o <see cref="MemberRequirementHandler"/>.
/// </remarks>
internal sealed class MemberRequirement : IAuthorizationRequirement;
