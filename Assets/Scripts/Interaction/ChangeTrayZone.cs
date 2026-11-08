using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Change handed back on the tray (doc 5.5). The right amount held for half a second completes it. Too much is an
/// error once it settles. Too little is normal while pieces are still being laid down, so it only counts as an
/// error when the player has stopped adding, and again each time they stay stopped that long.
/// </summary>
[RequireComponent(typeof(Collider))]
public class ChangeTrayZone : MonoBehaviour
{
    const float SettleSeconds = 0.5f;
    const float StoppedAddingSeconds = 8f;

    public GameAudio Audio;
    public int Target { get; private set; } = -1;
    public int ErrorCount { get; private set; }
    public int Amount { get; private set; }

    public event Action Matched;
    public event Action<int> WrongAmount;
    public event Action<int> AmountChanged;

    readonly HashSet<PaymentProp> inside = new HashSet<PaymentProp>();
    float stableFor;
    bool reportedTooMuch;

    public void Begin(int target)
    {
        Target = target;
        ErrorCount = 0;
        stableFor = 0f;
        reportedTooMuch = false;
    }

    public void End()
    {
        Target = -1;
        foreach (var prop in inside)
            if (prop) Destroy(prop.gameObject);
        inside.Clear();
        Amount = 0;
    }

    void OnTriggerEnter(Collider other)
    {
        var prop = other.GetComponentInParent<PaymentProp>();
        if (prop != null && !prop.IsCard) inside.Add(prop);
    }

    void OnTriggerExit(Collider other)
    {
        var prop = other.GetComponentInParent<PaymentProp>();
        if (prop != null) inside.Remove(prop);
    }

    void FixedUpdate()
    {
        inside.RemoveWhere(p => p == null);
        int amount = 0;
        bool anyHeld = false;
        foreach (var prop in inside)
        {
            amount += prop.Value;
            anyHeld |= prop.Grab.isSelected;
        }
        if (amount != Amount)
        {
            Amount = amount;
            stableFor = 0f;
            reportedTooMuch = false;
            if (Target >= 0) AmountChanged?.Invoke(amount);
        }
        if (Target < 0 || amount == 0 || anyHeld)
        {
            stableFor = 0f;
            return;
        }
        stableFor += Time.fixedDeltaTime;

        if (amount == Target)
        {
            if (stableFor < SettleSeconds) return;
            Target = -1;
            Matched?.Invoke();
        }
        else if (amount > Target)
        {
            if (stableFor < SettleSeconds || reportedTooMuch) return;
            reportedTooMuch = true;
            ReportWrong(amount);
        }
        else if (stableFor >= StoppedAddingSeconds)
        {
            stableFor = 0f;
            ReportWrong(amount);
        }
    }

    void ReportWrong(int amount)
    {
        ErrorCount++;
        if (Audio) Audio.PlayError();
        WrongAmount?.Invoke(amount);
    }
}
