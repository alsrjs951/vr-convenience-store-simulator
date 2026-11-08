using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

public enum CustomerState { Entering, Browsing, Queued, ToCounter, AtCounter, Leaving, Wandering }

/// <summary>What one customer did, for the HUD-free checks in P2DayVerification. Times are Time.time.</summary>
public class CustomerRecord
{
    public string Name, Avatar;
    public bool ForOrder;
    public int Sequence;
    public float SpawnedAt, QueuedAt = -1f, CalledAt = -1f, ArrivedAt = -1f, DoneAt = -1f, GoneAt = -1f;
    public int BrowseVisits, QueueSlotsTaken;
    public string CalledFrom = "";
    public float RemainingAtArrival = -1f;
    public float LeaveAnimationSeconds;
    /// <summary>Days 2 and 3: when the half-time hurry came, and whether the time ran out and the customer left.</summary>
    public float HurriedAt = -1f;
    public bool TimedOut;
}

/// <summary>
/// One customer on a NavMeshAgent (doc 6.1, D4): walks with the walk clip, stands with idle, looks at shelves with the
/// browse clip, and waves before leaving. CustomerDirector decides where it goes; this only moves and animates it.
///
/// Standing customers switch the agent off and carve a hole in the NavMesh instead, so the paths of those walking go
/// around the line and around people at the shelves. Local avoidance alone let a customer try to squeeze through the
/// line and get stuck there.
/// </summary>
[RequireComponent(typeof(NavMeshAgent), typeof(NavMeshObstacle))]
public class CustomerCharacter : MonoBehaviour
{
    public const string IdleState = "Idle", BrowseState = "Browse", WalkState = "Walk", LeaveState = "Leave";
    const float CrossFadeSeconds = 0.25f;
    const float MovingSpeed = 0.08f;
    const float TurnDegreesPerSecond = 300f;
    const float ArriveSlack = 0.04f;
    const int MaxReplans = 4;
    const float ReplanDistance = 0.2f;
    // Two walkers face to face (at a gondola corner, in the doorway) can each wait for the other, or slide round each
    // other for many seconds, under local avoidance. Getting less than BlockedProgress closer along the path and moving
    // less than BlockedMove in BlockedSeconds counts as blocked; the walker then keeps right, as people do, for up to
    // StepAsideSeconds, and goes on. Blocked again after MaxStepAsides of those is stuck for real: warp. Free walking
    // covers about 2.2 m in that time. A walker that keeps moving is never warped for being slow (a detour once took a
    // customer the long way round): only past a generous cap, four times the planned walk plus WalkCapSlack.
    const float BlockedSeconds = 2f;
    const float BlockedProgress = 0.4f;
    const float BlockedMove = 0.6f;
    const float TrailSeconds = 1f;
    const float WalkCapSlack = 20f;
    /// <summary>When people standing still close the way (a full line, a spot's last visitor not yet gone), wait this long before planning again.</summary>
    const float ClosedWaySeconds = 1.5f;
    const int TrailLength = 24;
    const float StepAsideDistance = 0.55f;
    const float StepAsideSeconds = 1.2f;
    const int MaxStepAsides = 4;

    public Animator Animator;
    public float LeaveSeconds = 2.8f;

    /// <summary>Counts walks that did not arrive in time and were finished with a warp. The checks expect zero.</summary>
    public static int StuckWarps;
    /// <summary>Counts walks that ended more than ReplanDistance short of an exact target after every replan. The checks expect zero.</summary>
    public static int ShortArrivals;
    /// <summary>Counts small hops back onto the NavMesh after a hole (the standing clerk, Day 2) was cut where the customer stood.</summary>
    public static int Nudges;
    /// <summary>Walks that ended short because the clerk stood within ClerkNear of the target (Day 2). Not in ShortArrivals.</summary>
    public static int ShortArrivalsAtClerk;
    /// <summary>Times a blocked walker stepped aside (see BlockedSeconds). Not a failure; the checks only report it.</summary>
    public static int StepAsides;
    const float ClerkNear = 0.9f;
    static readonly List<CustomerCharacter> everyone = new List<CustomerCharacter>();

    public NavMeshAgent Agent { get; private set; }
    public NavMeshObstacle Obstacle { get; private set; }
    /// <summary>Standing still as a carved obstacle, agent off.</summary>
    public bool Settled => !Agent.enabled;
    public CustomerState State { get; set; }
    public CustomerRecord Record { get; } = new CustomerRecord();
    public string Resting { get; set; } = IdleState;
    public string Playing { get; private set; } = "";
    public float WalkClipSpeed { get; private set; } = 1.2f;
    public Transform Basket { get; set; }
    public Transform RightHand => Animator != null && Animator.isHuman ? Animator.GetBoneTransform(HumanBodyBones.RightHand) : null;

    bool playingOnce;

    void Awake()
    {
        Agent = GetComponent<NavMeshAgent>();
        Obstacle = GetComponent<NavMeshObstacle>();
        Obstacle.shape = NavMeshObstacleShape.Capsule;
        Obstacle.radius = Agent.radius;
        Obstacle.height = Agent.height;
        Obstacle.center = Vector3.up * (Agent.height * 0.5f);
        Obstacle.carving = true;
        Obstacle.carveOnlyStationary = false;
        Obstacle.enabled = false;
        if (Animator == null) Animator = GetComponentInChildren<Animator>();
    }

    void OnEnable() => everyone.Add(this);
    void OnDisable() => everyone.Remove(this);

    void Start()
    {
        if (Animator == null || Animator.runtimeAnimatorController == null) return;
        foreach (var clip in Animator.runtimeAnimatorController.animationClips)
        {
            if (!clip.name.Contains("walk")) continue;
            // A humanoid clip's root speed is in normalized units; the avatar's human scale and the model's scale bring it to meters.
            float scale = (Animator.isHuman ? Animator.humanScale : 1f) * Animator.transform.lossyScale.y;
            WalkClipSpeed = Mathf.Max(0.3f, clip.averageSpeed.magnitude * scale);
        }
    }

    void Update()
    {
        if (Animator == null || playingOnce) return;
        float speed = Agent.enabled ? Agent.velocity.magnitude : 0f;
        bool moving = speed > MovingSpeed || (Agent.enabled && Agent.hasPath && !Agent.isStopped && Agent.remainingDistance > Agent.stoppingDistance + ArriveSlack);
        Play(moving ? WalkState : Resting);
        // Feet keep pace with the agent instead of sliding.
        Animator.speed = Playing == WalkState ? Mathf.Clamp(speed / WalkClipSpeed, 0.6f, 1.4f) : 1f;
    }

    void Play(string state)
    {
        if (state == Playing) return;
        Playing = state;
        Animator.CrossFadeInFixedTime(state, CrossFadeSeconds);
    }

    public bool IsAt(Vector3 target) => FlatDistance(transform.position, target) <= Agent.stoppingDistance + ArriveSlack;

    /// <summary>Stops being an agent and becomes a carved obstacle where it stands.</summary>
    public void Settle()
    {
        if (!Agent.enabled) return;
        Agent.ResetPath();
        Agent.enabled = false;
        Obstacle.enabled = true;
    }

    /// <summary>
    /// Back to an agent. The hole this customer carved closes on a later NavMesh update, and an agent switched on inside
    /// it finds no mesh under it, so it waits for the mesh to come back before walking.
    /// </summary>
    IEnumerator Unsettle()
    {
        if (Agent.enabled) yield break;
        Obstacle.enabled = false;
        var position = transform.position;
        for (int frame = 0; frame < 30; frame++)
        {
            yield return null;
            if (frame >= 1 && NavMesh.SamplePosition(position, out _, 0.05f, NavMesh.AllAreas)) break;
        }
        Agent.enabled = true;
        if (!Agent.isOnNavMesh) Nudge(position);
    }

    /// <summary>Back onto the nearest NavMesh, at most a step away. The clerk standing still cuts a hole round them (Day 2).</summary>
    void Nudge(Vector3 from)
    {
        if (NavMesh.SamplePosition(from, out var hit, 1f, NavMesh.AllAreas))
        {
            Agent.Warp(hit.position);
            Nudges++;
        }
        else
        {
            Agent.Warp(from);
        }
    }

    /// <summary>
    /// Walks on the NavMesh. A walk that takes far longer than its path is finished with a warp and counted. Exact targets
    /// (shelf spots, line places, the counter) belong to one customer at a time; a target several may share, like the door,
    /// takes arriveWithin, or two of them would stand off a few centimeters short of the same point forever.
    /// </summary>
    public IEnumerator WalkTo(Vector3 target, float arriveWithin = 0f)
    {
        yield return Unsettle();
        Agent.updateRotation = true;
        Agent.isStopped = false;
        bool loose = arriveWithin > Agent.stoppingDistance + ArriveSlack;
        float begun = Time.time;
        float timeout = -1f;
        int stepAsides = 0;
        trail.Clear();
        Track();
        // A path planned while someone still stood as an obstacle at the target ends short of it; plan again from there.
        for (int attempt = 0; attempt < MaxReplans; attempt++)
        {
            if (!Agent.isOnNavMesh) Nudge(transform.position);
            if (!Agent.SetDestination(target))
            {
                Debug.LogWarning($"[Customers] {name} cannot path to {target.ToString("F2")}; warping.");
                Warp(target);
                yield break;
            }
            while (Agent.pathPending) yield return null;
            if (timeout < 0f) timeout = PathLength(Agent.path) / Mathf.Max(0.1f, Agent.speed) * 4f + WalkCapSlack;
            var checkPosition = transform.position;
            float checkRemaining = Remaining(target);
            float checkTime = Time.time;
            while (loose
                       ? Agent.remainingDistance > arriveWithin
                       : !(Agent.remainingDistance <= Agent.stoppingDistance + ArriveSlack && Agent.velocity.sqrMagnitude < 0.04f))
            {
                if (Time.time - trailTime >= TrailSeconds) Track();
                if (Time.time - begun > timeout)
                {
                    Debug.LogWarning($"[Customers] {name} still walking to {target.ToString("F2")} after the {timeout:0.0}s cap at {transform.position.ToString("F2")}; warping. {Surroundings()} Trail: {Trail()}");
                    Warp(target);
                    yield break;
                }
                if (Time.time - checkTime >= BlockedSeconds)
                {
                    float moved = FlatDistance(transform.position, checkPosition);
                    float closer = checkRemaining - Remaining(target);
                    // Hovering just short of an exact spot, held off by someone's elbow or the edge of the mesh: close enough.
                    if (!loose && FlatDistance(transform.position, target) <= ReplanDistance && moved < BlockedProgress) break;
                    if (closer < BlockedProgress && moved < BlockedMove && stepAsides >= MaxStepAsides)
                    {
                        Debug.LogWarning($"[Customers] {name} stuck walking to {target.ToString("F2")} at {transform.position.ToString("F2")} after stepping aside {stepAsides} times; warping. {Surroundings()} Trail: {Trail()}");
                        Warp(target);
                        yield break;
                    }
                    if (closer < BlockedProgress && moved < BlockedMove)
                    {
                        stepAsides++;
                        yield return StepAside(target, closer, moved);
                        if (!Agent.SetDestination(target))
                        {
                            Debug.LogWarning($"[Customers] {name} cannot path to {target.ToString("F2")} after stepping aside; warping.");
                            Warp(target);
                            yield break;
                        }
                        while (Agent.pathPending) yield return null;
                    }
                    checkPosition = transform.position;
                    checkRemaining = Remaining(target);
                    checkTime = Time.time;
                }
                yield return null;
            }
            if (loose || FlatDistance(transform.position, target) <= ReplanDistance) break;
            // The first replan is quick (the one standing there is usually just leaving); after that, give a closed way time to open.
            yield return new WaitForSeconds(attempt > 0 && Agent.hasPath && Agent.pathStatus == NavMeshPathStatus.PathPartial ? ClosedWaySeconds : 0.2f);
        }
        if (!loose && FlatDistance(transform.position, target) > ReplanDistance)
        {
            var clerk = Camera.main != null ? Camera.main.transform.position : new Vector3(0f, 0f, -100f);
            if (FlatDistance(clerk, target) < ClerkNear)
            {
                ShortArrivalsAtClerk++;
                Debug.Log($"[Customers] {name} stopped {FlatDistance(transform.position, target):0.00} m short of {target.ToString("F2")}: the clerk stands there.");
            }
            else
            {
                ShortArrivals++;
                Debug.LogWarning($"[Customers] {name} ended {FlatDistance(transform.position, target):0.00} m short of {target.ToString("F2")} at {transform.position.ToString("F2")} (path {(Agent.hasPath ? Agent.pathStatus.ToString() : "none")}). {Surroundings()} Trail: {Trail()}");
            }
        }
        Agent.ResetPath();
    }

    /// <summary>
    /// Keep right: a short walk to the right of the way ahead, or to the left, or back, whichever the NavMesh allows first.
    /// Two people blocking each other face to face both go to their own right and so pass.
    /// </summary>
    IEnumerator StepAside(Vector3 target, float closer, float moved)
    {
        StepAsides++;
        var ahead = Agent.hasPath ? Agent.steeringTarget - transform.position : target - transform.position;
        ahead.y = 0f;
        if (ahead.sqrMagnitude < 0.0001f) ahead = transform.forward;
        ahead.y = 0f;
        ahead.Normalize();
        var right = new Vector3(ahead.z, 0f, -ahead.x);
        var from = transform.position;
        var to = from;
        float free = 0f;
        foreach (var side in new[] { right, -right, -ahead })
        {
            float room = NavMesh.Raycast(from, from + side * StepAsideDistance, out var hit, NavMesh.AllAreas) ? hit.distance : StepAsideDistance;
            if (room > free)
            {
                free = room;
                to = from + side * room;
            }
            if (room >= StepAsideDistance * 0.6f) break;
        }
        Debug.Log($"[Customers] {name} blocked at {from.ToString("F2")} on the way to {target.ToString("F2")} ({closer:0.00} m closer, {moved:0.00} m moved in {BlockedSeconds:0} s); stepping aside {free:0.00} m. {Surroundings()}");
        if (free < 0.1f)
        {
            yield return new WaitForSeconds(0.5f);
            yield break;
        }
        Agent.SetDestination(to);
        float until = Time.time + StepAsideSeconds;
        while (Time.time < until && (Agent.pathPending || Agent.remainingDistance > Agent.stoppingDistance + ArriveSlack)) yield return null;
    }

    /// <summary>Path distance left, or the straight distance while there is no usable path.</summary>
    float Remaining(Vector3 target)
    {
        float left = Agent.pathPending ? float.PositiveInfinity : Agent.remainingDistance;
        return float.IsInfinity(left) ? FlatDistance(transform.position, target) : left;
    }

    readonly List<(Vector3 at, Vector3 toward)> trail = new List<(Vector3, Vector3)>();
    float trailTime;

    /// <summary>Where this walk has been, once a second, so a warp's log shows where the time went.</summary>
    void Track()
    {
        trailTime = Time.time;
        trail.Add((transform.position, Agent.enabled && Agent.hasPath ? Agent.steeringTarget : transform.position));
        if (trail.Count > TrailLength) trail.RemoveAt(0);
    }

    string Trail()
    {
        var points = new List<string>();
        foreach (var (at, toward) in trail) points.Add($"({at.x:0.00},{at.z:0.00})>({toward.x:0.00},{toward.z:0.00})");
        return string.Join(" ", points);
    }

    /// <summary>Who is near and where the clerk stands, for the logs of a block or a warp.</summary>
    string Surroundings()
    {
        var near = new List<string>();
        foreach (var other in everyone)
        {
            if (other == null || other == this) continue;
            float gap = FlatDistance(other.transform.position, transform.position);
            if (gap > 1.5f) continue;
            string how = other.Settled ? "standing" : $"walking {other.Agent.velocity.magnitude:0.00} m/s";
            near.Add($"{other.name} {other.State} {how} at {other.transform.position.ToString("F2")} ({gap:0.00} m)");
        }
        var clerk = Camera.main != null ? Camera.main.transform.position : Vector3.zero;
        return $"Near: {(near.Count > 0 ? string.Join("; ", near) : "nobody")}. Clerk at {clerk.ToString("F2")}.";
    }

    void Warp(Vector3 target)
    {
        StuckWarps++;
        Agent.Warp(target);
        Agent.ResetPath();
    }

    /// <summary>Turns in place to a facing, then settles there: every caller stands for a while after it.</summary>
    public IEnumerator Face(Quaternion rotation)
    {
        if (Agent.enabled) Agent.updateRotation = false;
        var flat = Quaternion.Euler(0f, rotation.eulerAngles.y, 0f);
        while (Quaternion.Angle(transform.rotation, flat) > 1f)
        {
            transform.rotation = Quaternion.RotateTowards(transform.rotation, flat, TurnDegreesPerSecond * Time.deltaTime);
            yield return null;
        }
        transform.rotation = flat;
        Settle();
    }

    /// <summary>D4's leave animation: a wave toward the clerk, then the walk out is up to the director.</summary>
    public IEnumerator PlayLeave()
    {
        if (Animator == null) yield break;
        playingOnce = true;
        Animator.speed = 1f;
        Playing = LeaveState;
        Animator.CrossFadeInFixedTime(LeaveState, CrossFadeSeconds);
        yield return new WaitForSeconds(LeaveSeconds);
        playingOnce = false;
    }

    static float PathLength(NavMeshPath path)
    {
        float length = 0f;
        var corners = path.corners;
        for (int i = 1; i < corners.Length; i++) length += Vector3.Distance(corners[i - 1], corners[i]);
        return length;
    }

    static float FlatDistance(Vector3 a, Vector3 b)
    {
        a.y = 0f;
        b.y = 0f;
        return Vector3.Distance(a, b);
    }
}
