using System.Collections;
using UnityEngine;

/// <summary>What a thing is to the hand that takes it.</summary>
public enum TouchKind { Plain, Bag, Carton, Card, Note, Coin, StockBox, Tool }

/// <summary>
/// What a held thing sounds and feels like (2026-11-11), on each of the player's hands. A snack in a film bag crackles as
/// it is taken, and again whenever it is turned in the hand or squeezed, which is most of what the hand does with it on the
/// way to the scanner. A carton knocks. Each kind has its own pulse in the controller: a bag a scatter of small ones, a
/// carton one firm one, a coin a tick, a stock box a long push.
///
/// This adds to what the things already do themselves (a product's short pulse when taken, a coin's clink and a note's
/// paper sound out of the till); it takes nothing away, so with the hands switched off everything is as it was.
/// </summary>
[RequireComponent(typeof(PlayerHand))]
public class TouchFeel : MonoBehaviour
{
    // A bag rustles once it has been turned this far at more than this speed, or squeezed this much (the grip's travel,
    // 0 to 1), and not more often than this.
    const float RustleAboveDegreesPerSecond = 70f, RustleEveryDegrees = 26f, RustleEverySqueeze = 0.12f, RustleGapSeconds = 0.11f;
    // The squeeze of taking the bag is the take itself, not a rustle.
    const float SqueezeAfterSeconds = 0.3f;
    // Turning slower than the speed above is forgotten at this rate, so a slow, careful turn stays quiet.
    const float ForgetDegreesPerSecond = 25f;
    const float TakeVolume = 0.6f;
    // A product set down or dropped: silent under the first speed, full at the second.
    const float SetDownFromSpeed = 0.3f, SetDownFullSpeed = 2.5f;

    /// <summary>What the hand holds now.</summary>
    public TouchKind Holding { get; private set; }
    /// <summary>How many pulses this hand has asked its controller for, for the checks. Nothing feels them in the simulator.</summary>
    public int Pulses { get; private set; }

    PlayerHand hand;
    Transform held;
    Quaternion lastRotation;
    float turned, squeezed, lastGrip, lastRustle, heldSince;
    static GameAudio sounds;

    void Awake() => hand = GetComponent<PlayerHand>();

    static GameAudio Sounds()
    {
        if (sounds == null) sounds = FindFirstObjectByType<GameAudio>();
        return sounds;
    }

    /// <summary>What kind of thing this is to a hand.</summary>
    public static TouchKind KindOf(Transform thing)
    {
        if (thing == null) return TouchKind.Plain;
        if (thing.TryGetComponent(out ProductPrefab product))
            return product.Data != null && product.Data.Package == ProductPackage.Carton ? TouchKind.Carton : TouchKind.Bag;
        if (thing.TryGetComponent(out PaymentProp money)) return money.IsCard ? TouchKind.Card : money.IsCoin ? TouchKind.Coin : TouchKind.Note;
        if (thing.TryGetComponent(out BoxCutter _)) return TouchKind.Tool;
        return thing.GetComponentInParent<StockBox>() != null ? TouchKind.StockBox : TouchKind.Plain;
    }

    /// <summary>A product landing on something: the soft weight of a bag, the knock of a carton, louder the faster it lands.</summary>
    public static void SetDown(ProductPrefab product, float speed)
    {
        var audio = Sounds();
        if (audio == null || speed < SetDownFromSpeed) return;
        float loud = Mathf.Lerp(0.3f, 1f, Mathf.InverseLerp(SetDownFromSpeed, SetDownFullSpeed, speed));
        audio.PlayTouch(KindOf(product.transform) == TouchKind.Carton ? StoreSound.Carton : StoreSound.BagDown, product.transform.position, loud);
    }

    void LateUpdate()
    {
        var interactor = hand.Interactor;
        if (interactor == null) return;
        var now = interactor.hasSelection ? interactor.interactablesSelected[0].transform : null;
        if (now != held)
        {
            held = now;
            Holding = KindOf(now);
            if (now != null) Take();
        }
        if (held == null || Holding != TouchKind.Bag) return;

        // The film answers the hand: turned quickly, or squeezed.
        float angle = Quaternion.Angle(lastRotation, held.rotation);
        lastRotation = held.rotation;
        float speed = angle / Mathf.Max(Time.deltaTime, 1e-4f);
        if (speed > RustleAboveDegreesPerSecond) turned += angle;
        else turned = Mathf.Max(0f, turned - ForgetDegreesPerSecond * Time.deltaTime);
        if (Time.time - heldSince > SqueezeAfterSeconds) squeezed += Mathf.Abs(hand.Grip - lastGrip);
        lastGrip = hand.Grip;
        if ((turned < RustleEveryDegrees && squeezed < RustleEverySqueeze) || Time.time - lastRustle < RustleGapSeconds) return;
        float loud = squeezed >= RustleEverySqueeze ? 0.55f : Mathf.Clamp(0.25f + speed / 500f, 0.25f, 0.75f);
        turned = 0f;
        squeezed = 0f;
        lastRustle = Time.time;
        Sounds()?.PlayTouch(StoreSound.Rustle, held.position, loud);
        Pulse(0.08f + 0.16f * loud, 0.012f);
    }

    void Take()
    {
        lastRotation = held.rotation;
        lastGrip = hand.Grip;
        turned = 0f;
        squeezed = 0f;
        heldSince = Time.time;
        lastRustle = Time.time;
        switch (Holding)
        {
            case TouchKind.Bag:
                Sounds()?.PlayTouch(StoreSound.Crinkle, held.position, TakeVolume);
                StartCoroutine(Scatter());
                break;
            case TouchKind.Carton:
                Sounds()?.PlayTouch(StoreSound.Carton, held.position, TakeVolume);
                Pulse(0.45f, 0.02f);
                break;
            case TouchKind.Card: Pulse(0.18f, 0.012f); break;
            case TouchKind.Note: Pulse(0.08f, 0.02f); break;
            case TouchKind.Coin: Pulse(0.3f, 0.008f); break;
            case TouchKind.StockBox: StartCoroutine(Weight()); break;
            case TouchKind.Tool: Pulse(0.2f, 0.02f); break;
        }
    }

    /// <summary>A film bag in the fingers: after the pulse of taking it, a few small uneven ones as the film gives.</summary>
    IEnumerator Scatter()
    {
        yield return new WaitForSeconds(0.05f);
        for (int i = 0; i < 4 && Holding == TouchKind.Bag; i++)
        {
            Pulse(Random.Range(0.1f, 0.28f), 0.012f);
            yield return new WaitForSeconds(Random.Range(0.018f, 0.04f));
        }
    }

    /// <summary>A full box by its handle: the pulse of taking it, then its weight coming onto the hand.</summary>
    IEnumerator Weight()
    {
        yield return new WaitForSeconds(0.05f);
        if (Holding == TouchKind.StockBox) Pulse(0.4f, 0.12f);
    }

    void Pulse(float amplitude, float seconds)
    {
        if (hand.Interactor == null) return;
        hand.Interactor.SendHapticImpulse(amplitude, seconds);
        Pulses++;
    }
}
