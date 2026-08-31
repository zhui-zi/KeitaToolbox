using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using DalamudStatusFlags = Dalamud.Game.ClientState.Objects.Enums.StatusFlags;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using OmenTools;
using OmenTools.Dalamud;
using OmenTools.OmenService;
using static OmenTools.Global.Globals;
using OmenBattleChara = OmenTools.Dalamud.Services.Game.Object.Abstractions.ObjectKinds.IBattleChara;

namespace KeitaToolbox;

internal sealed partial class OccultPotFeature
{
    private const uint NorthHornCommonMobMinimumNameID = 14857;
    private const uint NorthHornCommonMobMaximumNameID = 14923;
    private const float NorthHornAggroEdgeRange = 10f;
    private const float NorthHornAggroSafetyMargin = 2f;
    private const float NorthHornAggroVerticalTolerance = 6f;
    private const float NorthHornAggroMaximumDistance = 120f;
    private const long NorthHornAggroReplanIntervalMS = 1_250;
    private const long NorthHornAggroStallTimeoutMS = 6_000;

    private bool northHornAggroAvoidanceActive;
    private bool northHornAggroRouteAdjusted;
    private bool northHornAggroRouteBlockedLogged;
    private bool northHornAggroUnavailableLogged;
    private Vector3 northHornAggroDestination;
    private Vector3 northHornAggroLastProgressPosition;
    private long northHornAggroNextUpdateAt;
    private long northHornAggroLastProgressAt;
    private List<Vector3>? northHornAggroFallbackPath;
    private List<Vector3>? northHornAggroBlockedPath;

    private void BeginNorthHornAggroAvoidance(Vector3 destination)
    {
        if (GameState.TerritoryType != OccultNorthTerritory ||
            DangerZoneHandling != DangerZoneHandlingMode.Ground ||
            undergroundDangerActive)
        {
            StopNorthHornAggroAvoidance();
            return;
        }

        northHornAggroAvoidanceActive = true;
        northHornAggroRouteAdjusted = false;
        northHornAggroRouteBlockedLogged = false;
        northHornAggroUnavailableLogged = false;
        northHornAggroDestination = destination;
        northHornAggroLastProgressPosition =
            DService.Instance().ObjectTable.LocalPlayer?.Position ?? destination;
        northHornAggroNextUpdateAt = Environment.TickCount64 + NorthHornAggroReplanIntervalMS;
        northHornAggroLastProgressAt = Environment.TickCount64;
        northHornAggroFallbackPath = null;
        northHornAggroBlockedPath = null;
    }

    private void StopNorthHornAggroAvoidance()
    {
        northHornAggroAvoidanceActive = false;
        northHornAggroRouteAdjusted = false;
        northHornAggroRouteBlockedLogged = false;
        northHornAggroUnavailableLogged = false;
        northHornAggroDestination = Vector3.Zero;
        northHornAggroLastProgressPosition = Vector3.Zero;
        northHornAggroNextUpdateAt = 0;
        northHornAggroLastProgressAt = 0;
        northHornAggroFallbackPath = null;
        northHornAggroBlockedPath = null;
    }

    private void UpdateNorthHornAggroAvoidance()
    {
        if (!northHornAggroAvoidanceActive)
            return;

        if (!autoDigActive ||
            GameState.TerritoryType != OccultNorthTerritory ||
            DangerZoneHandling != DangerZoneHandlingMode.Ground ||
            undergroundDangerActive ||
            DService.Instance().ObjectTable.LocalPlayer is not { } localPlayer)
        {
            StopNorthHornAggroAvoidance();
            return;
        }

        var now = Environment.TickCount64;
        if (northHornAggroBlockedPath != null)
        {
            RetryBlockedNorthHornAggroRoute(localPlayer, now);
            return;
        }

        bool pathRunning;
        try
        {
            pathRunning = Plugin.PluginInterface
                                .GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning")
                                .InvokeFunc();
        }
        catch (Exception ex)
        {
            LogNorthHornAggroUnavailable(ex);
            northHornAggroNextUpdateAt = now + 5_000;
            return;
        }

        if (!pathRunning)
            return;

        if (northHornAggroRouteAdjusted)
        {
            if (DistanceSquaredXZ(localPlayer.Position, northHornAggroLastProgressPosition) >= 1f)
            {
                northHornAggroLastProgressPosition = localPlayer.Position;
                northHornAggroLastProgressAt = now;
            }
            else if (now - northHornAggroLastProgressAt >= NorthHornAggroStallTimeoutMS)
            {
                PauseNorthHornAggroRouteForRetry(
                    northHornAggroFallbackPath,
                    now,
                    "Monster-avoidance route stalled; movement paused while a safe route is rebuilt");
                return;
            }
        }

        if (now < northHornAggroNextUpdateAt)
            return;
        northHornAggroNextUpdateAt = now + NorthHornAggroReplanIntervalMS;

        try
        {
            var waypoints = Plugin.PluginInterface
                                  .GetIpcSubscriber<List<Vector3>>("vnavmesh.Path.ListWaypoints")
                                  .InvokeFunc();
            if (waypoints == null || waypoints.Count == 0)
                return;

            var zones = CaptureNorthHornAggroZones(localPlayer);
            if (zones.Count == 0)
                return;

            var sourcePath = new List<Vector3>(waypoints.Count + 1) { localPlayer.Position };
            sourcePath.AddRange(waypoints);
            if (!AggroAvoidancePolicy.TryBuild(
                    sourcePath,
                    zones,
                    NorthHornAggroVerticalTolerance,
                    ProjectNorthHornAggroWaypoint,
                    out var safePath))
            {
                PauseNorthHornAggroRouteForRetry(
                    waypoints,
                    now,
                    "No fully projected monster-avoidance route was available; movement paused while a safe route is rebuilt");
                return;
            }

            if (PathsEquivalent(sourcePath, safePath))
                return;

            var submittedPath = RemoveCurrentPosition(safePath, localPlayer.Position);
            if (submittedPath.Count == 0)
                return;

            northHornAggroFallbackPath = waypoints.ToList();
            Plugin.PluginInterface
                  .GetIpcSubscriber<List<Vector3>, bool, object?>("vnavmesh.Path.MoveTo")
                  .InvokeAction(submittedPath, false);
            northHornAggroRouteAdjusted = true;
            northHornAggroRouteBlockedLogged = false;
            northHornAggroLastProgressPosition = localPlayer.Position;
            northHornAggroLastProgressAt = now;
            northHornAggroUnavailableLogged = false;
            DService.Instance().Log.Information(
                $"[KeitaToolbox.MagicPot] North Horn treasure route avoids {zones.Count} live monster aggro zones with {submittedPath.Count} waypoints");
        }
        catch (Exception ex)
        {
            LogNorthHornAggroUnavailable(ex);
            northHornAggroNextUpdateAt = now + 5_000;
        }
    }

    private unsafe List<AggroAvoidanceZone> CaptureNorthHornAggroZones(OmenBattleChara localPlayer)
    {
        var zones = new List<AggroAvoidanceZone>();
        var playerLevel = localPlayer.ForayLevel;
        var maximumDistanceSquared = NorthHornAggroMaximumDistance * NorthHornAggroMaximumDistance;

        foreach (var obj in DService.Instance().ObjectTable)
        {
            if (obj is not OmenBattleChara enemy ||
                enemy.Address == 0 ||
                enemy.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc ||
                enemy.NameID is < NorthHornCommonMobMinimumNameID or > NorthHornCommonMobMaximumNameID ||
                (enemy.StatusFlags & DalamudStatusFlags.Hostile) == 0 ||
                enemy.IsDead ||
                enemy.CurrentHp == 0 ||
                !enemy.IsTargetable ||
                enemy.TargetObject != null ||
                DistanceSquaredXZ(localPlayer.Position, enemy.Position) > maximumDistanceSquared ||
                !ShouldAvoidNorthHornMob(playerLevel, enemy.ForayLevel))
                continue;

            var gameObject = (GameObject*)enemy.Address;
            if (gameObject == null ||
                gameObject->BattleNpcSubKind != BattleNpcSubKind.Combatant ||
                gameObject->FateId != 0)
                continue;

            var radius = Math.Max(
                0.5f,
                NorthHornAggroEdgeRange +
                Math.Max(0f, enemy.HitboxRadius) +
                NorthHornAggroSafetyMargin);
            zones.Add(new AggroAvoidanceZone(enemy.Position, radius));
        }

        return zones;
    }

    private Vector3? ProjectNorthHornAggroWaypoint(Vector3 point)
    {
        var projected = Plugin.PluginInterface
                              .GetIpcSubscriber<Vector3, float, float, Vector3?>(
                                  "vnavmesh.Query.Mesh.NearestPoint")
                              .InvokeFunc(point, 8f, 12f);
        return projected is { } result && DistanceSquaredXZ(result, point) <= 64f
                   ? result
                   : null;
    }

    private void PauseNorthHornAggroRouteForRetry(
        IReadOnlyList<Vector3>? retryPath,
        long now,
        string message)
    {
        try
        {
            VnavStop();
            northHornAggroBlockedPath = retryPath?.ToList() ?? [];
            northHornAggroRouteAdjusted = false;
            northHornAggroFallbackPath = null;
            northHornAggroNextUpdateAt = now + NorthHornAggroReplanIntervalMS;
            northHornAggroLastProgressPosition =
                DService.Instance().ObjectTable.LocalPlayer?.Position ?? Vector3.Zero;
            northHornAggroLastProgressAt = now;
            if (!northHornAggroRouteBlockedLogged)
            {
                northHornAggroRouteBlockedLogged = true;
                DService.Instance().Log.Warning($"[KeitaToolbox.MagicPot] {message}");
            }
        }
        catch (Exception ex)
        {
            northHornAggroLastProgressAt = now;
            LogNorthHornAggroUnavailable(ex);
        }
    }

    private void RetryBlockedNorthHornAggroRoute(OmenBattleChara localPlayer, long now)
    {
        if (now < northHornAggroNextUpdateAt)
            return;
        northHornAggroNextUpdateAt = now + NorthHornAggroReplanIntervalMS;

        try
        {
            var remainingPath = PrepareNorthHornAggroFallback(
                northHornAggroBlockedPath,
                localPlayer.Position);
            if (remainingPath.Count == 0)
                remainingPath.Add(northHornAggroDestination);

            var sourcePath = new List<Vector3>(remainingPath.Count + 1) { localPlayer.Position };
            sourcePath.AddRange(remainingPath);
            var zones = CaptureNorthHornAggroZones(localPlayer);
            List<Vector3> safePath;
            if (zones.Count == 0)
                safePath = sourcePath;
            else if (!AggroAvoidancePolicy.TryBuild(
                         sourcePath,
                         zones,
                         NorthHornAggroVerticalTolerance,
                         ProjectNorthHornAggroWaypoint,
                         out safePath))
                return;

            var submittedPath = RemoveCurrentPosition(safePath, localPlayer.Position);
            if (submittedPath.Count == 0)
                return;

            northHornAggroFallbackPath = remainingPath;
            northHornAggroBlockedPath = null;
            Plugin.PluginInterface
                  .GetIpcSubscriber<List<Vector3>, bool, object?>("vnavmesh.Path.MoveTo")
                  .InvokeAction(submittedPath, false);
            northHornAggroRouteAdjusted = zones.Count != 0;
            northHornAggroRouteBlockedLogged = false;
            northHornAggroUnavailableLogged = false;
            northHornAggroLastProgressPosition = localPlayer.Position;
            northHornAggroLastProgressAt = now;
            DService.Instance().Log.Information(
                $"[KeitaToolbox.MagicPot] North Horn treasure movement resumed with a safe route and {submittedPath.Count} waypoints");
        }
        catch (Exception ex)
        {
            LogNorthHornAggroUnavailable(ex);
            northHornAggroNextUpdateAt = now + 5_000;
        }
    }

    private void LogNorthHornAggroUnavailable(Exception ex)
    {
        if (northHornAggroUnavailableLogged)
            return;

        northHornAggroUnavailableLogged = true;
        DService.Instance().Log.Warning(
            ex,
            "[KeitaToolbox.MagicPot] North Horn monster avoidance is unavailable; normal vnavmesh movement remains active");
    }

    private static bool ShouldAvoidNorthHornMob(uint playerLevel, uint mobLevel) =>
        playerLevel == 0 || mobLevel == 0 || mobLevel >= playerLevel;

    private static List<Vector3> RemoveCurrentPosition(
        IReadOnlyList<Vector3> path,
        Vector3 playerPosition)
    {
        var result = path.ToList();
        while (result.Count > 1 && Vector3.DistanceSquared(result[0], playerPosition) <= 1f)
            result.RemoveAt(0);
        return result;
    }

    private static List<Vector3> PrepareNorthHornAggroFallback(
        IReadOnlyList<Vector3>? fallbackPath,
        Vector3 playerPosition)
    {
        if (fallbackPath == null || fallbackPath.Count == 0)
            return [];

        var result = fallbackPath.ToList();
        while (result.Count > 1 &&
               DistanceSquaredXZ(playerPosition, result[1]) + 0.25f <
               DistanceSquaredXZ(playerPosition, result[0]))
            result.RemoveAt(0);
        while (result.Count > 1 && DistanceSquaredXZ(playerPosition, result[0]) <= 1f)
            result.RemoveAt(0);
        return result;
    }

    private static bool PathsEquivalent(
        IReadOnlyList<Vector3> left,
        IReadOnlyList<Vector3> right)
    {
        if (left.Count != right.Count)
            return false;

        for (var i = 0; i < left.Count; i++)
            if (Vector3.DistanceSquared(left[i], right[i]) > 0.01f)
                return false;
        return true;
    }

    private static float DistanceSquaredXZ(Vector3 left, Vector3 right)
    {
        var deltaX = left.X - right.X;
        var deltaZ = left.Z - right.Z;
        return (deltaX * deltaX) + (deltaZ * deltaZ);
    }
}
