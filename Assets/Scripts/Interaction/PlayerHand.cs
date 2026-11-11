using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

/// <summary>
/// One of the player's hands (2026-11-11), drawn where the controller is instead of the controller. The fingers follow
/// the buttons under them: the grip closes the three lower fingers and the thumb, the trigger closes the index finger.
/// PlayerHandSetup cuts the hand out of a customer avatar and fills in the joints.
/// </summary>
public class PlayerHand : MonoBehaviour
{
    public const int Fingers = 5, JointsPerFinger = 3;
    public const int Thumb = 0, Index = 1, Middle = 2, Ring = 3, Little = 4;

    // Degrees at the three joints of each finger, from the knuckle out. The hand is modelled nearly flat, and that is 0.
    // Loose: how a hand hangs when it is doing nothing. Fist: everything closed. Point: the index finger out straight
    // while the others are closed, which is what the poke at the till needs.
    static readonly float[,] Loose = { { 4f, 0f, 4f }, { 4f, 6f, 4f }, { 8f, 10f, 6f }, { 10f, 12f, 6f }, { 12f, 12f, 6f } };
    static readonly float[,] Fist = { { 45f, 25f, 40f }, { 72f, 96f, 62f }, { 78f, 100f, 64f }, { 80f, 100f, 64f }, { 82f, 96f, 64f } };
    static readonly float[] Point = { 4f, 6f, 4f };
    // Around something held, before the fingers are fitted to the thing itself: about a handle's width.
    const float HoldClose = 0.5f;
    const float FollowPerSecond = 18f;

    public bool Left;
    /// <summary>Finger * 3 + joint, from the knuckle out.</summary>
    public Transform[] Joints = new Transform[Fingers * JointsPerFinger];
    /// <summary>Local rotations as modelled.</summary>
    public Quaternion[] Rest = new Quaternion[Fingers * JointsPerFinger];
    /// <summary>The hinge of each joint in its parent's space. A positive angle closes the finger.</summary>
    public Vector3[] CurlAxis = new Vector3[Fingers * JointsPerFinger];
    /// <summary>The end of each finger, for whatever needs to know where a finger touches.</summary>
    public Transform[] Tips = new Transform[Fingers];
    /// <summary>Where the index finger ends and which way it goes when it points, in this hand's space before its scale.</summary>
    public Vector3 PointTip, PointDirection = Vector3.forward;
    /// <summary>Whose buttons these fingers follow. The scene builder sets it; without one the hand stays loose.</summary>
    public XRBaseInputInteractor Interactor;

    public float Grip { get; private set; }
    public float Trigger { get; private set; }
    public bool Holding { get; private set; }

    readonly float[] angles = new float[Fingers * JointsPerFinger];
    bool posed;

    /// <summary>
    /// "-show-controllers" on the command line draws the rig's controller models again and no hands, to compare the two
    /// (in a headset: whether the hand lies where the real one is).
    /// </summary>
    void Awake()
    {
        if (System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-show-controllers") < 0) return;
        if (transform.parent != null)
            foreach (Transform sibling in transform.parent)
                if (sibling.name.EndsWith("Controller Visual")) sibling.gameObject.SetActive(true);
        gameObject.SetActive(false);
    }

    void LateUpdate()
    {
        if (Interactor != null)
        {
            Grip = Mathf.Clamp01(Interactor.selectInput.ReadValue());
            Trigger = Mathf.Clamp01(Interactor.activateInput.ReadValue());
            Holding = Interactor.hasSelection;
        }
        Apply(Grip, Trigger, Holding, posed ? 1f - Mathf.Exp(-FollowPerSecond * Time.deltaTime) : 1f);
    }

    /// <summary>The hand as it was modelled, every joint at 0.</summary>
    public void PoseAsModelled()
    {
        for (int i = 0; i < Joints.Length; i++)
        {
            angles[i] = 0f;
            if (Joints[i] != null) Joints[i].localRotation = Rest[i];
        }
    }

    /// <summary>Puts the fingers where these inputs lead at once. The editor's pictures use it; play goes through LateUpdate.</summary>
    public void PoseNow(float grip, float trigger, bool holding)
    {
        Grip = grip;
        Trigger = trigger;
        Holding = holding;
        Apply(grip, trigger, holding, 1f);
    }

    void Apply(float grip, float trigger, bool holding, float follow)
    {
        posed = true;
        for (int finger = 0; finger < Fingers; finger++)
        {
            for (int joint = 0; joint < JointsPerFinger; joint++)
            {
                int i = finger * JointsPerFinger + joint;
                if (Joints[i] == null) continue;
                angles[i] = Mathf.Lerp(angles[i], Target(finger, joint, grip, trigger, holding), follow);
                Joints[i].localRotation = Quaternion.AngleAxis(angles[i], CurlAxis[i]) * Rest[i];
            }
        }
    }

    static float Target(int finger, int joint, float grip, float trigger, bool holding)
    {
        float loose = Loose[finger, joint], fist = Fist[finger, joint];
        if (holding) return Mathf.Lerp(loose, fist, HoldClose);
        if (finger == Index)
        {
            // The more the other fingers close, the straighter the index finger stands out from them, until the trigger pulls it in.
            float open = Mathf.Lerp(loose, Point[joint], grip);
            return Mathf.Lerp(open, fist, trigger);
        }
        if (finger == Thumb) return Mathf.Lerp(loose, fist, Mathf.Max(grip, trigger * 0.5f));
        return Mathf.Lerp(loose, fist, grip);
    }
}
