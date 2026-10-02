namespace IdentityGateway.Testing.Keycloak;

/// <summary>
/// Falha de uma peça de suporte de teste: o mailpit, um formulário do Keycloak, o device flow, a API ou um prazo.
/// </summary>
/// <remarks>
/// <b>Sem segredo, por construção de quem a lança:</b> a mensagem leva a etapa e o diagnóstico — o título da página, o
/// id do formulário, os NOMES dos campos, o código de erro OAuth —, nunca token, senha, código de dispositivo, link de
/// ação nem HTML. Ela vai para o log da CI.
/// </remarks>
public sealed class FalhaDoHarnessException : Exception
{
    public FalhaDoHarnessException()
    {
    }

    public FalhaDoHarnessException(string message)
        : base(message)
    {
    }

    public FalhaDoHarnessException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public FalhaDoHarnessException(FamiliaDeFalha familia, string etapa, string message)
        : base($"{etapa}: {message}")
    {
        Familia = familia;
        Etapa = etapa;
    }

    public FalhaDoHarnessException(FamiliaDeFalha familia, string etapa, string message, Exception innerException)
        : base($"{etapa}: {message}", innerException)
    {
        Familia = familia;
        Etapa = etapa;
    }

    /// <summary>A peça que falhou.</summary>
    public FamiliaDeFalha Familia { get; } = FamiliaDeFalha.Formulario;

    /// <summary>O passo da jornada em que a falha aconteceu, em texto.</summary>
    public string Etapa { get; } = string.Empty;
}
