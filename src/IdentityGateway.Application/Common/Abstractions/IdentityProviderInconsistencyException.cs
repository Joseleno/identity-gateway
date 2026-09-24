namespace IdentityGateway.Application.Common.Abstractions;

/// <summary>
/// O provedor de identidade está num estado que repetir a operação não corrige.
/// </summary>
/// <remarks>
/// <para>
/// <b>Exception e não <c>Result</c>.</b> O repositório reserva <c>Result</c> para resposta de negócio e exception para
/// falha de infraestrutura — e isto é infraestrutura: um slug tomado por uma Organization criada fora da Gateway, ou
/// duas Organizations apontando para o mesmo tenant.
/// </para>
/// <para>
/// <b>Contrato com quem consome:</b> o retry do consumidor do provisionamento precisa IGNORAR este tipo. Repeti-lo só
/// adiaria o <c>ProvisioningFailed</c>, por configuração e não por decisão.
/// </para>
/// </remarks>
public sealed class IdentityProviderInconsistencyException : Exception
{
    public IdentityProviderInconsistencyException()
    {
    }

    public IdentityProviderInconsistencyException(string message)
        : base(message)
    {
    }

    public IdentityProviderInconsistencyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
