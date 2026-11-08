using System;
using UnityEngine;

/// <summary>
/// Card terminal slot: holding the card in it for half a second approves it (doc 5.5). The zone sits inside an open
/// slot in the terminal's front face. On Quest 2 nobody could tell where or which way the card went into a plain box,
/// so a see-through card shows the inserted pose while the card is in the hand, and the slot frame lights once it is in.
/// </summary>
[RequireComponent(typeof(Collider))]
public class CardSlotZone : MonoBehaviour
{
    const float ApproveSeconds = 0.5f;
    // Trigger stays come every physics step; a little longer than one step without one means the card has left.
    const float InsideGraceSeconds = 0.1f;

    public GameAudio Audio;
    public Func<bool> CanApprove;
    public event Action Approved;
    public event Action Rejected;

    [Tooltip("See-through card in the inserted pose. Its forward is the way the card goes in.")]
    public GameObject Guide;
    public Renderer[] Frame;
    public Material FrameIdle, FrameLit;

    float inserted;
    bool rejectedThisInsert;
    float lastInside = float.NegativeInfinity;
    bool frameLit;

    public bool CardInside => Time.time - lastInside < InsideGraceSeconds;

    public void ShowGuide(bool show)
    {
        if (Guide != null && Guide.activeSelf != show) Guide.SetActive(show);
    }

    void Update()
    {
        bool lit = CardInside;
        if (lit == frameLit || Frame == null) return;
        frameLit = lit;
        foreach (var part in Frame)
            if (part) part.sharedMaterial = lit ? FrameLit : FrameIdle;
    }

    void OnTriggerStay(Collider other)
    {
        var prop = other.GetComponentInParent<PaymentProp>();
        if (prop == null || !prop.IsCard) return;
        lastInside = Time.time;
        inserted += Time.fixedDeltaTime;
        if (inserted < ApproveSeconds) return;

        if (CanApprove == null || CanApprove())
        {
            inserted = 0f;
            Approved?.Invoke();
        }
        else if (!rejectedThisInsert)
        {
            rejectedThisInsert = true;
            if (Audio) Audio.PlayError();
            Rejected?.Invoke();
        }
    }

    void OnTriggerExit(Collider other)
    {
        var prop = other.GetComponentInParent<PaymentProp>();
        if (prop == null || !prop.IsCard) return;
        inserted = 0f;
        rejectedThisInsert = false;
    }
}
