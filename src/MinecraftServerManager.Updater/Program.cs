namespace MinecraftServerManager.Updater;

internal static class Program
{
    private static Task<int> Main(string[] args)
    {
        if (ProductLocalServiceRepairApplication.IsCommand(args))
        {
            return ProductLocalServiceRepairApplication.RunAsync(args);
        }

        if (ProductExplorerGuiActivationLauncher.IsCommand(args))
        {
            return Task.FromResult(ProductExplorerGuiActivationLauncher.Run(args));
        }

        if (ProductGuiActivationBroker.IsBrokerRequest(args))
        {
            return ProductGuiActivationBroker.RunAsync(args);
        }

        if (ProductGuiActivationBroker.IsActivateCurrentRequest(args))
        {
            return ProductGuiActivationBroker.ActivateCurrentAsync(args[2]);
        }

        if (args is { Length: 3 } &&
            string.Equals(args[0], "--launch-current", StringComparison.Ordinal) &&
            string.Equals(args[1], "--install-root", StringComparison.Ordinal))
        {
            return ProductGuiActivationBroker.LaunchCurrentAsync(args[2]);
        }

        return ProductUpdaterApplication.RunAsync(args);
    }
}
