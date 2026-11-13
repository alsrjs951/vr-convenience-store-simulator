using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Change handed back on the tray (doc 5.5).
///
/// With a customer (2026-11-13) nobody counts it for the clerk: what lies on the tray is the customer's to judge. The
/// right amount, held for half a second, is taken. More than that is taken too, a little later (TakenAnywaySeconds: the
/// moment in which a note laid by mistake can still be snatched back), and Over says how much the till is short by.
/// Less than that, with no more on its way in a hand, and the customer asks for the rest (Short), and asks again if it
/// still does not come. The game was won before by following the amounts on the screen; now the change can be wrong.
///
/// In a lesson (Begin with guided) it is as it was: only the right amount completes it, too much is an error once it
/// settles, and too little is an error when the player has stopped adding, again each time they stay stopped that long.
/// </summary>
[RequireComponent(typeof(Collider))]
public class ChangeTrayZone : MonoBehaviour
{
    const float SettleSeconds = 0.5f;
    const float StoppedAddingSeconds = 8f;
    /// <summary>Too much change lies this long before the customer takes it. [제안]</summary>
    public const float TakenAnywaySeconds = 1.5f;
    /// <summary>Too little lies this long, with nothing more in a hand, before the customer asks; and this long before they ask again. [제안]</summary>
    public const float AskAfterSeconds = 8f, AskAgainSeconds = 12f;

    public GameAudio Audio;
    public int Target { get; private set; } = -1;
    public int ErrorCount { get; private set; }
    public int Amount { get; private set; }
    /// <summary>A lesson: the tray counts for the clerk and only the right amount will do.</summary>
    public bool Guided { get; private set; }
    /// <summary>How much more than the change due was on the tray when the customer took it. 0 when it was right.</summary>
    public int Over { get; private set; }
    /// <summary>Times the customer had to ask for the rest of this change.</summary>
    public int ShortCount { get; private set; }
    /// <summary>More change is on its way in a hand, so too little on the tray is not yet what the clerk means to give.</summary>
    public Func<bool> PieceOnItsWay;

    public event Action Matched;
    public event Action<int> WrongAmount;
    public event Action<int> AmountChanged;
    /// <summary>The customer finds the change short by this much (never in a lesson).</summary>
    public event Action<int> Short;

    readonly HashSet<PaymentProp> inside = new HashSet<PaymentProp>();
    float stableFor, askAfter;
    bool reportedTooMuch;

    public void Begin(int target, bool guided = false)
    {
        Target = target;
        Guided = guided;
        ErrorCount = 0;
        Over = 0;
        ShortCount = 0;
        stableFor = 0f;
        askAfter = AskAfterSeconds;
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

    /// <summary>
    /// What lies on the tray, no longer the tray's to count or to clear away: the customer takes it (2026-11-12).
    /// What a hand still holds stays.
    /// </summary>
    public List<PaymentProp> Release()
    {
        var lying = new List<PaymentProp>();
        foreach (var prop in inside)
            if (prop && !prop.Grab.isSelected && !prop.Leaving) lying.Add(prop);
        foreach (var prop in lying) inside.Remove(prop);
        return lying;
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
        if (Guided) JudgeForTheLesson(amount);
        else JudgeAsTheCustomer(amount);
    }

    void JudgeAsTheCustomer(int amount)
    {
        if (amount < Target)
        {
            // Still being counted out: the customer waits while a piece is on its way.
            if (PieceOnItsWay != null && PieceOnItsWay())
            {
                stableFor = 0f;
                return;
            }
            stableFor += Time.fixedDeltaTime;
            if (stableFor < askAfter) return;
            stableFor = 0f;
            askAfter = AskAgainSeconds;
            ShortCount++;
            Short?.Invoke(Target - amount);
            return;
        }
        stableFor += Time.fixedDeltaTime;
        if (stableFor < (amount == Target ? SettleSeconds : TakenAnywaySeconds)) return;
        Over = amount - Target;
        Target = -1;
        Matched?.Invoke();
    }

    void JudgeForTheLesson(int amount)
    {
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
