namespace IdentityGateway.Testing.Keycloak;

/// <summary>
/// Família da falha do harness. O valor é o código de saída do app de CI: quem lê o log do job sabe em que peça olhar
/// antes de abrir o detalhe.
/// </summary>
public enum FamiliaDeFalha
{
    Mailpit = 10,
    Formulario = 20,
    DeviceFlow = 30,
    Api = 40,
    Prazo = 50,
}
