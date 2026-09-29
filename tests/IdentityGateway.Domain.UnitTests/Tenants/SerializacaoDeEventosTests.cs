using System.Text.Json;
using System.Text.Json.Nodes;
using IdentityGateway.Domain.Common;
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

    // InlineData com o nome do tipo, e não MemberData com a instância: IDomainEvent não é serializável pelo xUnit, e o
    // aviso do analisador sobre isso vira erro com TreatWarningsAsErrors.
    [Theory]
    [InlineData(nameof(TenantRegistered))]
    [InlineData(nameof(TenantActivated))]
    public void OccurredOn_VoltaComOValorGravado(string tipo)
    {
        IDomainEvent evento = tipo == nameof(TenantRegistered)
            ? new TenantRegistered(TenantId.New(), "acme")
            : new TenantActivated(TenantId.New());

        // O valor é trocado no JSON antes de desserializar: comparar com o original deixaria o teste à mercê da
        // resolução do relógio — o instante da desserialização poderia coincidir com o da criação e passar com o
        // defeito presente. Com um valor fixo no passado, só um setter faz o teste passar.
        DateTimeOffset gravado = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        JsonObject json = JsonSerializer.SerializeToNode(evento, evento.GetType(), Opcoes)!.AsObject();
        json["occurredOn"] = gravado.ToString("O", System.Globalization.CultureInfo.InvariantCulture);

        object? volta = Desserializar(json.ToJsonString(), evento.GetType());

        volta.Should().BeAssignableTo<IDomainEvent>()
            .Which.OccurredOn.Should().Be(gravado);
    }

    // Reproduz a chamada do OutboxProcessor, que desserializa por Type porque so conhece o tipo em
    // tempo de execucao, a partir do nome curto gravado na mensagem.
    private static object? Desserializar(string json, Type tipo) =>
        JsonSerializer.Deserialize(json, tipo, Opcoes);
}
