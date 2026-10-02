using System.Text.Json;
using IdentityGateway.Domain.Tenants;

namespace IdentityGateway.Infrastructure.IntegrationTests.Identity.Keycloak;

/// <summary>
/// O harness contra as páginas de verdade do Keycloak 26.7.4: o link de ações, o login e a sessão.
/// </summary>
/// <remarks>
/// <b>O login em um passo não é afirmado aqui.</b> Sem nenhuma Organization no realm, o Keycloak mostra usuário e
/// senha numa página só; com uma, vira identity-first em duas. O realm do fixture é compartilhado, e a existência de
/// Organization depende da ordem dos testes — o um-passo é determinístico só no job da CI, onde o platform-admin entra
/// antes de qualquer tenant existir.
/// </remarks>
public sealed class HarnessContraKeycloakTests(KeycloakFixture keycloak)
{
    private static readonly string[] SoTenantAdmin = ["tenant-admin"];

    private static readonly string[] AcoesDoConvite = ["UPDATE_PASSWORD", "VERIFY_EMAIL"];

    [Fact]
    public async Task LinkDeAcoes_DefineASenhaZeraAsAcoesEVerificaOEmail()
    {
        // O link do e-mail resolve o VERIFY_EMAIL ao ser aberto; a senha e o perfil (nome e sobrenome, exigidos pelo
        // User Profile) completam as ações. Leitura crua pelo master, nunca pelo que o harness "acha" que fez.
        CancellationToken ct = TestContext.Current.CancellationToken;

        UsuarioDeTeste usuario = await keycloak.NovoUsuarioAsync(SoTenantAdmin, TenantId.New().Value.ToString(), ct);

        JsonElement cru = await keycloak.LerUsuarioCruAsync(usuario.Id, ct);
        cru.GetProperty("emailVerified").GetBoolean().Should().BeTrue();
        cru.GetProperty("requiredActions").GetArrayLength().Should().Be(0);
        cru.GetProperty("firstName").GetString().Should().NotBeNullOrEmpty();
        (await keycloak.LerComoMasterAsync($"users/{usuario.Id}/credentials", ct)).GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task LinkJaConcluido_FalhaNaHoraComAPaginaDeErro()
    {
        // Foco de revisão 1: a pessoa clica duas vezes no link do e-mail. O token de ação morre no próprio uso, e o
        // Keycloak responde 400 com a página de erro. O harness precisa dizer isso na hora — sem laço, sem esperar
        // prazo — e a senha definida na primeira vez continua valendo.
        CancellationToken ct = TestContext.Current.CancellationToken;
        string email = KeycloakFixture.EmailUnico();
        string senha = SenhasDeTeste.Gerar();
        string id = await keycloak.CriarUsuarioComoMasterAsync(
            new { username = email, email, enabled = true, emailVerified = false, requiredActions = AcoesDoConvite }, ct);
        await keycloak.EnviarEmailDeAcoesComoMasterAsync(id, ct);
        Uri link = await keycloak.LinkDoConviteAsync(email, ct);

        using (HarnessDeLogin primeiro = keycloak.CriarHarness())
        {
            await primeiro.ConcluirLinkDeAcoesAsync(link, senha, ct);
        }

        using HarnessDeLogin segundo = keycloak.CriarHarness();
        Func<Task> deNovo = () => segundo.ConcluirLinkDeAcoesAsync(link, SenhasDeTeste.Gerar(), ct);

        FalhaDoHarnessException falha = (await deNovo.Should().ThrowAsync<FalhaDoHarnessException>()).Which;
        falha.Familia.Should().Be(FamiliaDeFalha.Formulario);
        falha.Message.Should().Contain("página de erro").And.NotContain("key=");

        using HarnessDeLogin login = keycloak.CriarHarness();
        TokensDeUsuario tokens = await login.TokenPorDispositivoAsync(email, senha, ct);
        tokens.AccessToken.Should().NotBeNullOrEmpty("a senha da primeira conclusão continua valendo");
    }

    [Fact]
    public async Task LoginComOrganizationNoRealm_LevaDoisPassos()
    {
        // Identity-first: havendo ao menos uma Organization, o Keycloak pede o usuário numa página e a senha em outra.
        // O harness preenche "o que houver" em cada kc-form-login.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await keycloak.CriarOrganizacaoComoMasterAsync(KeycloakFixture.SlugUnico().Value, tenantId: null, ct);
        UsuarioDeTeste usuario = await keycloak.NovoUsuarioAsync(SoTenantAdmin, TenantId.New().Value.ToString(), ct);
        using HarnessDeLogin harness = keycloak.CriarHarness();

        await harness.TokenPorDispositivoAsync(usuario.Email, usuario.Senha, ct);

        harness.PassosDoUltimoLogin.Should().Be(2);
    }

    [Fact]
    public async Task SegundoLoginNaMesmaInstancia_ReaproveitaASessaoSemPedirLogin()
    {
        // Uma instância do harness é uma sessão de navegador. É também o motivo do aviso do README: no mesmo
        // navegador, o segundo device flow sai como o usuário que já está logado.
        CancellationToken ct = TestContext.Current.CancellationToken;
        UsuarioDeTeste usuario = await keycloak.NovoPlatformAdminAsync(ct);
        using HarnessDeLogin harness = keycloak.CriarHarness();
        TokensDeUsuario primeiro = await harness.TokenPorDispositivoAsync(usuario.Email, usuario.Senha, ct);

        TokensDeUsuario segundo = await harness.TokenPorDispositivoAsync(usuario.Email, usuario.Senha, ct);

        harness.PassosDoUltimoLogin.Should().Be(0);
        PayloadDoJwt.Ler(segundo.AccessToken).GetProperty("sub").GetString()
            .Should().Be(PayloadDoJwt.Ler(primeiro.AccessToken).GetProperty("sub").GetString());
    }

    [Fact]
    public async Task LinkDeAcoes_AbreSemSerConsumido()
    {
        // É como o job da CI confere o convite do admin do tenant sem concluí-lo: abrir a primeira página do link não o
        // gasta, e o convidado ainda consegue usá-lo. Concluído, o link passa a abrir a página de erro.
        CancellationToken ct = TestContext.Current.CancellationToken;
        string email = KeycloakFixture.EmailUnico();
        string id = await keycloak.CriarUsuarioComoMasterAsync(
            new { username = email, email, enabled = true, emailVerified = false, requiredActions = AcoesDoConvite }, ct);
        await keycloak.EnviarEmailDeAcoesComoMasterAsync(id, ct);
        Uri link = await keycloak.LinkDoConviteAsync(email, ct);

        using (HarnessDeLogin curioso = keycloak.CriarHarness())
        {
            (await curioso.LinkDeAcoesAbreAsync(link, ct)).Should().BeTrue();
            (await curioso.LinkDeAcoesAbreAsync(link, ct)).Should().BeTrue("abrir duas vezes também não consome");
        }

        using HarnessDeLogin convidado = keycloak.CriarHarness();
        await convidado.ConcluirLinkDeAcoesAsync(link, SenhasDeTeste.Gerar(), ct);

        using HarnessDeLogin depois = keycloak.CriarHarness();
        (await depois.LinkDeAcoesAbreAsync(link, ct)).Should().BeFalse("concluído, o link abre a página de erro");
    }

    [Fact]
    public async Task SenhaErrada_FalhaSemInsistirEAContaContinuaEntrando()
    {
        // O realm tem bruteForceProtected. Um harness que reenviasse o formulário bloquearia a conta; com uma tentativa
        // só, o login certo logo em seguida passa.
        CancellationToken ct = TestContext.Current.CancellationToken;
        UsuarioDeTeste usuario = await keycloak.NovoPlatformAdminAsync(ct);

        using (HarnessDeLogin errado = keycloak.CriarHarness())
        {
            Func<Task> entrar = () => errado.TokenPorDispositivoAsync(usuario.Email, SenhasDeTeste.Gerar(), ct);

            (await entrar.Should().ThrowAsync<FalhaDoHarnessException>()).Which.Message.Should().Contain("recusados");
        }

        using HarnessDeLogin certo = keycloak.CriarHarness();
        TokensDeUsuario tokens = await certo.TokenPorDispositivoAsync(usuario.Email, usuario.Senha, ct);
        tokens.AccessToken.Should().NotBeNullOrEmpty();
    }
}
