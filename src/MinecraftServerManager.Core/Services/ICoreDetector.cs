using MinecraftServerManager.Core.Models;

namespace MinecraftServerManager.Core.Services;

public interface ICoreDetector
{
    DetectionResult Detect(string jarPath);

    Task<DetectionResult> DetectAsync(
        string jarPath,
        CancellationToken cancellationToken = default);
}
