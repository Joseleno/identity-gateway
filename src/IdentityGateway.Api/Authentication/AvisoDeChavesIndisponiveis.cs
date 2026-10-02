namespace IdentityGateway.Api.Authentication;

/// <summary>
/// Limita o aviso de "chave de assinatura não encontrada" a um por intervalo.
/// </summary>
/// <remarks>
/// <para>
/// <b>Por que limitar.</b> A falta de chave é o sintoma do provedor de identidade fora do ar, e por isso vale um
/// <c>Warning</c>. Mas qualquer pessoa a provoca sem se autenticar, com um token de <c>kid</c> inventado — e o
/// <c>401</c> não consome cota do limitador de requisições. Sem limite, um laço de pedidos encheria o log de avisos e
/// esconderia o alerta de verdade.
/// </para>
/// <para>
/// <b>Um por instância da Api, e não por processo:</b> é um serviço, e não um campo estático. Nos testes há vários
/// hosts no mesmo processo, e o aviso de um não pode calar o de outro.
/// </para>
/// </remarks>
internal sealed class AvisoDeChavesIndisponiveis
{
    private long _proximoEmMs = long.MinValue;

    /// <summary>Verdadeiro na primeira chamada de cada intervalo; falso nas demais.</summary>
    public bool PodeAvisar() => PodeAvisar(Environment.TickCount64);

    /// <summary>A mesma decisão, com o relógio dado por quem chama — para o teste.</summary>
    internal bool PodeAvisar(long agoraEmMs)
    {
        long proximo = Interlocked.Read(ref _proximoEmMs);
        long seguinte = agoraEmMs + (long)ValidacaoDoAccessToken.IntervaloDeRefresh.TotalMilliseconds;

        // CompareExchange: de dois pedidos simultâneos, só um ganha o intervalo.
        return agoraEmMs >= proximo && Interlocked.CompareExchange(ref _proximoEmMs, seguinte, proximo) == proximo;
    }
}
