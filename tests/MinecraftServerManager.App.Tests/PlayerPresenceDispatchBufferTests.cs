using System.Collections.Specialized;
using MinecraftServerManager.App.Infrastructure;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.App.Tests;

public sealed class PlayerPresenceDispatchBufferTests
{
    [Fact]
    public void Events_AreAppliedToAnAuthoritativeSnapshotWithoutPendingHistory()
    {
        var buffer = new PlayerPresenceDispatchBuffer(2);
        var instanceId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        buffer.StartSession(instanceId, sessionId);

        Assert.True(buffer.Apply(
            instanceId, sessionId, new PlayerPresenceChange("RepeatUser", true)));
        Assert.False(buffer.Apply(
            instanceId, sessionId, new PlayerPresenceChange("RepeatUser", true)));
        Assert.True(buffer.Apply(
            instanceId, sessionId, new PlayerPresenceChange("RepeatUser", false)));

        var snapshot = Assert.IsType<PlayerPresenceSnapshot>(buffer.Capture(instanceId));
        Assert.Equal(sessionId, snapshot.SessionId);
        Assert.Equal(2, snapshot.Version);
        Assert.Empty(snapshot.OnlinePlayers);
        Assert.False(buffer.HasChangedSince(instanceId, sessionId, snapshot.Version));
    }

    [Fact]
    public void NewSession_RejectsLateEventsFromThePreviousSession()
    {
        var buffer = new PlayerPresenceDispatchBuffer(10);
        var instanceId = Guid.NewGuid();
        var oldSession = Guid.NewGuid();
        var newSession = Guid.NewGuid();
        buffer.StartSession(instanceId, oldSession);
        Assert.True(buffer.Apply(
            instanceId, oldSession, new PlayerPresenceChange("OldUser", true)));

        buffer.StartSession(instanceId, newSession);

        Assert.False(buffer.Apply(
            instanceId, oldSession, new PlayerPresenceChange("GhostUser", true)));
        Assert.True(buffer.Apply(
            instanceId, newSession, new PlayerPresenceChange("NewUser", true)));
        var snapshot = Assert.IsType<PlayerPresenceSnapshot>(buffer.Capture(instanceId));
        Assert.Equal(newSession, snapshot.SessionId);
        Assert.Equal(["NewUser"], snapshot.OnlinePlayers);
    }

    [Fact]
    public void ReverseOrderLeaveFlood_CannotLeaveFalseOnlinePlayers()
    {
        const int capacity = 4_096;
        var buffer = new PlayerPresenceDispatchBuffer(capacity);
        var instanceId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var viewModel = CreateViewModel(instanceId);
        var names = Enumerable.Range(0, 5_000)
            .Select(index => $"P{index:D4}")
            .ToArray();
        buffer.StartSession(instanceId, sessionId);

        foreach (var name in names)
        {
            buffer.Apply(instanceId, sessionId, new PlayerPresenceChange(name, true));
        }

        var onlineSnapshot = Assert.IsType<PlayerPresenceSnapshot>(buffer.Capture(instanceId));
        viewModel.UpdateOnlinePlayers(onlineSnapshot.OnlinePlayers);
        Assert.Equal(capacity, viewModel.VisiblePlayers.Count);

        foreach (var name in names.Reverse())
        {
            buffer.Apply(instanceId, sessionId, new PlayerPresenceChange(name, false));
        }

        var offlineSnapshot = Assert.IsType<PlayerPresenceSnapshot>(buffer.Capture(instanceId));
        var playerCollectionEvents = new List<NotifyCollectionChangedEventArgs>();
        var visibleCollectionEvents = new List<NotifyCollectionChangedEventArgs>();
        viewModel.Players.CollectionChanged += (_, args) => playerCollectionEvents.Add(args);
        viewModel.VisiblePlayers.CollectionChanged += (_, args) => visibleCollectionEvents.Add(args);
        viewModel.UpdateOnlinePlayers(offlineSnapshot.OnlinePlayers);
        Assert.Empty(viewModel.Players);
        Assert.Empty(viewModel.VisiblePlayers);
        Assert.Equal("0 位線上", viewModel.PlayerSummary);
        Assert.Collection(playerCollectionEvents, change =>
            Assert.Equal(NotifyCollectionChangedAction.Reset, change.Action));
        Assert.Collection(visibleCollectionEvents, change =>
            Assert.Equal(NotifyCollectionChangedAction.Reset, change.Action));
    }

    [Fact]
    public void EndSession_RemovesItsSnapshotWithoutRemovingANewerSession()
    {
        var buffer = new PlayerPresenceDispatchBuffer(10);
        var instanceId = Guid.NewGuid();
        var oldSession = Guid.NewGuid();
        var newSession = Guid.NewGuid();
        buffer.StartSession(instanceId, oldSession);
        buffer.StartSession(instanceId, newSession);

        buffer.EndSession(instanceId, oldSession);
        Assert.NotNull(buffer.Capture(instanceId));

        buffer.EndSession(instanceId, newSession);
        Assert.Null(buffer.Capture(instanceId));
    }

    private static ServerInstanceViewModel CreateViewModel(Guid instanceId) => new(
        new ServerInstance
        {
            Id = instanceId,
            Name = "Presence Flood Test",
            CoreType = CoreType.Paper
        },
        static (_, _) => Task.CompletedTask);
}
