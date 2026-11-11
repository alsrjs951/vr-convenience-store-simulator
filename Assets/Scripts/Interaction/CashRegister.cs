using System;
using UnityEngine;

/// <summary>
/// The till on the counter (2026-11-10): a steel box under the POS monitor whose drawer springs out toward the clerk for a
/// cash sale and is shut again when the sale is over. The drawer holds an insert with a slot for each note and a cup for
/// each coin; each has a place where the piece on top lies, which is what the clerk picks up.
/// </summary>
public class CashRegister : MonoBehaviour
{
    [Serializable]
    public class Slot
    {
        public int Value;
        /// <summary>Where the piece on top lies, and which way round. A child of the drawer, so it rides with it.</summary>
        public Transform Anchor;
    }

    // The spring that throws the drawer out, and how much of its speed is left after it hits the end of its runners.
    const float Stiffness = 240f, Damping = 17f, Rebound = 0.22f;
    // Shut by a push that gathers speed.
    const float CloseAcceleration = 16f;

    public Transform Drawer;
    public float Travel = 0.3f;
    public Slot[] Slots = new Slot[0];
    public GameAudio Audio;

    public bool IsOpen { get; private set; }
    /// <summary>0 shut, 1 all the way out.</summary>
    public float Opened { get; private set; }
    public bool FullyOpen => IsOpen && Opened > 0.97f;
    public bool Shut => !IsOpen && Opened <= 0f;
    /// <summary>How many times it has opened, for the checks.</summary>
    public int OpenCount { get; private set; }

    Vector3 shutAt;
    float speed;

    void Awake() => shutAt = Drawer.localPosition;

    public Transform SlotFor(int value)
    {
        foreach (var slot in Slots)
            if (slot.Value == value) return slot.Anchor;
        return null;
    }

    public void Open()
    {
        if (IsOpen) return;
        IsOpen = true;
        OpenCount++;
        speed = 0f;
        if (Audio) Audio.PlayAt(StoreSound.DrawerOpen, Drawer.position);
    }

    public void Close()
    {
        if (!IsOpen) return;
        IsOpen = false;
        speed = 0f;
        if (Audio) Audio.PlayAt(StoreSound.DrawerClose, Drawer.position);
    }

    void Update()
    {
        // A long frame must not throw the spring off.
        float dt = Mathf.Min(Time.deltaTime, 0.02f);
        if (IsOpen)
        {
            if (Opened >= 1f && speed == 0f) return;
            speed += (Stiffness * (1f - Opened) - Damping * speed) * dt;
            Opened += speed * dt;
            if (Opened >= 1f)
            {
                Opened = 1f;
                speed = speed > 0.6f ? -speed * Rebound : 0f;
            }
        }
        else
        {
            if (Opened <= 0f) return;
            speed -= CloseAcceleration * dt;
            Opened = Mathf.Max(0f, Opened + speed * dt);
        }
        // Toward the clerk, who stands on the -z side of the counter.
        Drawer.localPosition = shutAt + Vector3.back * (Travel * Opened);
    }
}
