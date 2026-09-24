namespace IdentityGateway.ArchitectureTests;

/// <summary>Localiza a raiz do repositório subindo a partir da pasta do binário de teste.</summary>
internal static class RaizDoRepositorio
{
    public static string Caminho(params string[] partes)
    {
        DirectoryInfo? pasta = new(AppContext.BaseDirectory);

        while (pasta is not null && !File.Exists(Path.Combine(pasta.FullName, "IdentityGateway.slnx")))
        {
            pasta = pasta.Parent;
        }

        if (pasta is null)
        {
            throw new InvalidOperationException("Raiz do repositório (IdentityGateway.slnx) não encontrada.");
        }

        return Path.Combine([pasta.FullName, .. partes]);
    }
}
