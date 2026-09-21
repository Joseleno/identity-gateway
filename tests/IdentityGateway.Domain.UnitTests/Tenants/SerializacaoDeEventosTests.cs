using System.Text.Json;
using IdentityGateway.Domain.Tenants;
using IdentityGateway.Domain.Tenants.Events;

namespace IdentityGateway.Domain.UnitTests.Tenants;

/// <summary>
/// Prova que os domain events do tenant sobrevivem ao round-trip do Outbox.
/// </summary>
public sealed class SerializacaoDeEventosTests
{
    private static readonly JsonSerializerOptions Opcoes = new(JsonSerializerDefaults.Web);

    [Fact]
    public void TenantRegistered_SobreviveAoRoundTrip()
    {
        TenantRegistered original = new(TenantId.New(), TenantSlug.Create("acme").Value.Value);

        string json = JsonSerializer.Serialize(original, original.GetType(), Opcoes);
        object? volta = Desserializar(json, typeof(TenantRegistered));

        volta.Should().BeOfType<TenantRegistered>()
            .Which.Slug.Should().Be("acme");
    }

    [Fact]
    public void TenantActivated_SobreviveAoRoundTrip()
    {
        TenantActivated original = new(TenantId.New());

        string json = JsonSerializer.Serialize(original, original.GetType(), Opcoes);
        object? volta = Desserializar(json, typeof(TenantActivated));

        volta.Should().BeOfType<TenantActivated>()
            .Which.TenantId.Should().Be(original.TenantId);
    }

    // Reproduz a chamada do OutboxProcessor, que desserializa por Type porque so conhece o tipo em
    // tempo de execucao, a partir do nome curto gravado na mensagem.
    private static object? Desserializar(string json, Type tipo) =>
        JsonSerializer.Deserialize(json, tipo, Opcoes);
}
