using MinecraftServerManager.Contracts;

namespace MinecraftServerManager.ProviderHost;

internal static class Program
{
    public static int Main(string[] args)
    {
        if (args is ["--version"])
        {
            Console.WriteLine($"Muhun MCSV Provider Host {ProductApiProtocol.CurrentVersion}");
            return 0;
        }

        Console.Error.WriteLine(
            "This component is managed by Muhun MCSV Service and does not accept interactive commands.");
        return 64;
    }
}
