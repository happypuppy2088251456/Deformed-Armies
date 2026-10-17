using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor.SceneManagement;
using UnityEngine;

// Isolated movement regression checks: never changes Physics.simulationMode or project settings.
public static class DragonAiVerification
{
    private const float Dt = 1f / 60f;
    private static readonly List<string> Results = new List<string>();

    public static void Run()
    {
        Results.Clear();
        try
        {
            ExplicitTargetAndStop();
            LateTargetAndRespawn();
            WallStopsAnimationSpeed();
            TurningIsNotBlocked();
            BlockedPointsCooldown();
            PatrolArrivalAndNullPoint();
            Results.Add("PASS: all isolated CharacterController movement checks.");
        }
        catch (Exception ex)
        {
            Results.Add("FAIL: " + ex);
            throw;
        }
        finally
        {
            Directory.CreateDirectory(DragonAiRepair.OutputDirectory);
            File.WriteAllLines(Path.Combine(DragonAiRepair.OutputDirectory, "verification.txt"), Results);
        }
    }

    private static DragonAI NewWorld()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
        floor.name = "Verification floor";
        floor.transform.position = new Vector3(0f, -.5f, 0f);
        floor.transform.localScale = new Vector3(200f, 1f, 200f);
        var root = new GameObject("Verification dragon");
        root.transform.position = new Vector3(0f, .1f, 0f);
        var cc = root.AddComponent<CharacterController>();
        cc.height = 2f;
        cc.radius = .4f;
        cc.center = Vector3.up;
        cc.skinWidth = .04f;
        cc.minMoveDistance = 0f;
        var ai = root.AddComponent<DragonAI>();
        ai.enabled = false; // Tick manually at a known time step.
        ai.playerTag = "";
        ai.detectRange = 30f;
        ai.loseRange = 40f;
        ai.stopDistance = 3f;
        ai.stopMargin = 1f;
        ai.waitAtPoint = .1f;
        Physics.SyncTransforms();
        return ai;
    }

    private static Transform Point(string name, Vector3 position, bool player = false)
    {
        var go = new GameObject(name);
        go.transform.position = position;
        if (player) go.tag = "Player";
        return go.transform;
    }

    private static void Step(DragonAI ai, float seconds)
    {
        int count = Mathf.CeilToInt(seconds / Dt);
        for (int i = 0; i < count; i++)
        {
            Physics.SyncTransforms();
            ai.Tick(Dt);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void ExplicitTargetAndStop()
    {
        var ai = NewWorld();
        var target = Point("Explicit untagged target", new Vector3(0f, 0f, 12f));
        ai.playerTarget = target;
        Step(ai, 5f);
        Require(ai.CurrentTarget == target && ai.CurrentState == DragonAI.State.Chase, "Explicit target was ignored.");
        Require(ai.transform.position.z > 5f && ai.transform.position.z < 10f, "Chase did not approach and stop: " + ai.transform.position);
        Require(ai.MoveSpeed < .05f, "Stopped chase is still moving.");
        Require(Mathf.Abs(ai.transform.position.y) < .2f, "Dragon hovered or sank: " + ai.transform.position);
        var stopped = ai.transform.position;
        target.position += Vector3.forward * .2f;
        Step(ai, .8f);
        Require(Vector3.Distance(stopped, ai.transform.position) < .1f, "Stop margin did not hold position.");
        target.position = new Vector3(0f, 0f, 80f);
        Step(ai, .5f);
        Require(ai.CurrentState == DragonAI.State.Idle, "Distant target did not release chase.");
        Results.Add("PASS explicit untagged target, chase, grounded stop, stop hysteresis, lose target.");
    }

    private static void LateTargetAndRespawn()
    {
        var ai = NewWorld();
        ai.playerTag = "Player";
        Step(ai, .6f);
        var target = Point("Late player", new Vector3(0f, 0f, 10f), true);
        Step(ai, 1.5f);
        Require(ai.CurrentTarget == target, "Late spawned player was not acquired.");
        UnityEngine.Object.DestroyImmediate(target.gameObject);
        var replacement = Point("Respawned player", new Vector3(5f, 0f, 10f), true);
        Step(ai, 1.5f);
        Require(ai.CurrentTarget == replacement, "Respawned player was not acquired.");
        Results.Add("PASS late player spawn and destroyed target replacement.");
    }

    private static void WallStopsAnimationSpeed()
    {
        var ai = NewWorld();
        var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
        wall.name = "Verification wall";
        wall.transform.position = new Vector3(0f, 2f, 3f);
        wall.transform.localScale = new Vector3(20f, 4f, 1f);
        ai.playerTarget = Point("Target beyond wall", new Vector3(0f, 0f, 12f));
        Step(ai, 4f);
        Require(ai.DesiredSpeed > 1f && ai.transform.position.z < 2.3f, "Wall did not block commanded chase.");
        Require(ai.MoveSpeed < .05f, "Animation receives requested rather than actual speed at wall: " + ai.MoveSpeed);
        Results.Add("PASS wall collision reports actual horizontal speed zero.");
    }

    private static void TurningIsNotBlocked()
    {
        var ai = NewWorld();
        var first = Point("Point behind", new Vector3(0f, 0f, -15f));
        ai.patrolPoints = new[] { first, Point("Second point", new Vector3(15f, 0f, 0f)) };
        ai.turnSpeed = 30f;
        ai.stuckTimeout = .15f;
        ai.maxStuckRetries = 1;
        Step(ai, 2f);
        Require(ai.CurrentPatrolIndex == 0 && ai.CurrentState == DragonAI.State.Patrol, "Intentional slow turn skipped a patrol point.");
        Require(Mathf.Abs(ai.transform.position.x) + Mathf.Abs(ai.transform.position.z) < .1f, "Dragon slid while turning in place.");
        Results.Add("PASS slow 180-degree turn does not trigger stuck handling.");
    }

    private static void BlockedPointsCooldown()
    {
        var ai = NewWorld();
        foreach (var p in new[] { new Vector3(0f, 2f, 1f), new Vector3(0f, 2f, -1f), new Vector3(1f, 2f, 0f), new Vector3(-1f, 2f, 0f) })
        {
            var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wall.transform.position = p;
            wall.transform.localScale = p.x == 0f ? new Vector3(3f, 4f, .2f) : new Vector3(.2f, 4f, 3f);
        }
        ai.patrolPoints = new[] { Point("Blocked A", new Vector3(0f, 0f, 15f)), Point("Blocked B", new Vector3(0f, 0f, -15f)) };
        ai.stuckTimeout = .3f;
        ai.maxStuckRetries = 2;
        ai.waitAtPoint = .2f;
        ai.allBlockedCooldown = 20f;
        bool retriedSame = false, triedNext = false, cooledDown = false;
        for (int i = 0; i < 900; i++)
        {
            Physics.SyncTransforms();
            ai.Tick(Dt);
            if (ai.IsWaiting && ai.CurrentPatrolIndex == 0) retriedSame = true;
            if (ai.CurrentPatrolIndex == 1) triedNext = true;
            if (triedNext && ai.CurrentState == DragonAI.State.Idle) { cooledDown = true; break; }
        }
        Require(retriedSame && triedNext && cooledDown, "Blocked patrol retry / full-round cooldown failed.");
        Results.Add("PASS blocked point retries, skips after failures, and cools down after a failed round.");
    }

    private static void PatrolArrivalAndNullPoint()
    {
        var ai = NewWorld();
        ai.patrolPoints = new[] { null, Point("Reachable A", new Vector3(0f, 0f, 6f)), Point("Reachable B", new Vector3(6f, 0f, 6f)) };
        Step(ai, 7f);
        Require(ai.transform.position.x > 1f, "Patrol did not skip null entry and advance after arrival.");
        Require(ai.CurrentState == DragonAI.State.Patrol, "Reachable patrol incorrectly entered cooldown.");
        Results.Add("PASS null patrol entries and normal arrival advance.");
    }
}
