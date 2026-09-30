namespace IdentityGateway.Infrastructure.Configuration;

/// <summary>
/// Política do convite de membros.
/// </summary>
/// <remarks>
/// <c>TimeSpan</c>, e não um inteiro com a unidade no nome como as demais options: a spec da fatia C fixa o formato
/// <c>7.00:00:00</c>. Escreva sempre o dia explícito — <c>"24:00:00"</c> não é 24 horas para o <c>TimeSpan.Parse</c>.
/// </remarks>
public sealed class InvitationOptions
{
    /// <summary>Seção correspondente no arquivo de configuração.</summary>
    public const string SectionName = "Invitations";

    /// <summary>Teto do prazo do link.</summary>
    /// <remarks>
    /// Arbitrário, e é o ponto: sem teto, um erro de digitação faria o link valer, na prática, para sempre — e um link
    /// não usado continua trocando a senha do admin até expirar (spec §3.4).
    /// </remarks>
    internal static readonly TimeSpan PrazoMaximo = TimeSpan.FromDays(30);

    /// <summary>Por quanto tempo o link do e-mail de convite vale. Positivo, em segundos inteiros, até 30 dias.</summary>
    public TimeSpan LinkLifetime { get; init; } = TimeSpan.FromDays(7);

    /// <summary>A regra que a subida confere.</summary>
    internal static bool EhValido(InvitationOptions opcoes) =>
        opcoes.LinkLifetime > TimeSpan.Zero
        && opcoes.LinkLifetime.Ticks % TimeSpan.TicksPerSecond == 0
        && opcoes.LinkLifetime <= PrazoMaximo;
}
