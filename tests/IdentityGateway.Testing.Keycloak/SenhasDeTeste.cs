using System.Security.Cryptography;

namespace IdentityGateway.Testing.Keycloak;

/// <summary>Senhas descartáveis, geradas em memória: nenhuma senha de teste fica escrita no repositório.</summary>
public static class SenhasDeTeste
{
    /// <summary>32 caracteres hexadecimais aleatórios, com um prefixo que satisfaz políticas de composição.</summary>
    public static string Gerar() => $"Aa1!{Convert.ToHexString(RandomNumberGenerator.GetBytes(16))}";
}
