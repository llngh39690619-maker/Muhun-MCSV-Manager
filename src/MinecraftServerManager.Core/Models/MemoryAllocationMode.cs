namespace MinecraftServerManager.Core.Models;

/// <summary>How one server obtains its effective Java heap limits.</summary>
public enum MemoryAllocationMode
{
    /// <summary>
    /// Keep zero as Legacy so manager.json files written before this setting existed retain their
    /// exact launch behavior. Legacy JAR servers use the saved values; legacy argument-file packs
    /// continue to use their original user_jvm_args.txt untouched.
    /// </summary>
    Legacy = 0,

    /// <summary>Resolve the mode and values from <see cref="NewServerDefaultsSettings"/>.</summary>
    UseManagerDefault = 1,

    /// <summary>Estimate a bounded heap from the server pack and currently available RAM.</summary>
    Automatic = 2,

    /// <summary>Use the explicitly persisted minimum and maximum values.</summary>
    Manual = 3,
}
