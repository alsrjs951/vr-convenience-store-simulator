using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

/// <summary>Where the work on a box has got to. Picking it up again after docking keeps this (doc 5.7.1).</summary>
public enum BoxStage { Sealed, TapeCut, Open, Empty, Recycled }

/// <summary>Where the box is.</summary>
public enum BoxPlace { Cart, Carried, Docked, Loose }

/// <summary>Doc 5.7.1's single state: Sealed → Carried → Docked → TapeCut → Open → Empty → Recycled.</summary>
public enum StockBoxState { Sealed, Carried, Docked, TapeCut, Open, Empty, Recycled }

/// <summary>
/// One delivery box (doc 5.7, D10): one product, 4/6/8 in one layer (2x2, 2x3, 2x4), built from the product's size when
/// it is spawned (doc 5.7.3, no per-product box prefab). Picked up only by the hand holes, carried with Instantaneous
/// movement, docked on a stand, tape cut with the cutter (six checkpoints in order, at most 2 s apart, only while docked,
/// only when no other box is open), flaps folded out past 110°, then the products come out one by one.
///
/// The products inside appear when the box opens. They are the same 3DGS products as everywhere else: there is no
/// lighter {id}_LOD1 asset yet (doc 5.7.3 wants one for the box), so the box shows full ones.
/// </summary>
public class StockBox : MonoBehaviour
{
    public const float Wall = 0.005f;
    const float Mass = 2f;
    const float FloorRespawnSeconds = 3f;
    const float CheckpointRadius = 0.03f;
    const float CheckpointGapSeconds = 2f;
    public const int DefaultCheckpoints = 6;
    /// <summary>How many checkpoints the next boxes get along their tape. A sharper cutter (an equipment unlock) needs fewer.</summary>
    public static int CheckpointsPerBox = DefaultCheckpoints;
    const float NoticeSeconds = 3f;
    const float HintAfterDockedSeconds = 20f;
    const float FlapHintSeconds = 15f;

    public Delivery Delivery { get; private set; }
    public IntakeDirector Intake { get; private set; }
    public BoxStage Stage { get; private set; } = BoxStage.Sealed;
    public BoxPlace Place { get; private set; } = BoxPlace.Cart;
    public StockBoxState State =>
        Stage == BoxStage.Recycled ? StockBoxState.Recycled :
        Stage == BoxStage.Empty ? StockBoxState.Empty :
        Stage == BoxStage.Open ? StockBoxState.Open :
        Stage == BoxStage.TapeCut ? StockBoxState.TapeCut :
        Place == BoxPlace.Docked ? StockBoxState.Docked :
        Place == BoxPlace.Carried ? StockBoxState.Carried : StockBoxState.Sealed;

    public XRGrabInteractable Grab { get; private set; }
    public Rigidbody Body { get; private set; }
    public Vector3 Inner { get; private set; }
    public float OuterHeight => Inner.y + Wall;
    public BoxFlap[] MainFlaps { get; private set; }
    public BoxUnit[] Units { get; private set; }
    public Collider[] Handles { get; private set; }
    public BoxStandSocket DockedAt { get; private set; }
    /// <summary>The stand it last sat on: a drop sends it back there (doc 5.7.1 B1).</summary>
    public BoxStandSocket LastStand { get; private set; }
    public int CutProgress { get; private set; }
    public int CutResets { get; private set; }
    public bool BeadsShown => beads.Count > 0 && beads[0].activeSelf;
    public string Notice => notice != null && notice.transform.parent.gameObject.activeSelf ? notice.text : "";
    public float DockedSince { get; private set; } = -1f;
    public float CutAt { get; private set; } = -1f;
    public event Action<StockBox> Changed;

    readonly List<Collider> bodyColliders = new List<Collider>();
    readonly List<GameObject> beads = new List<GameObject>();
    Vector3[] checkpointLocal;
    // Fixed when the box is made: its beads are built to this number.
    int checkpoints = DefaultCheckpoints;
    Transform[] minorFlaps;
    Transform tapeA, tapeB;
    Text notice;
    float noticeUntil;
    int cutDirection;
    float lastCheckpointTime;
    readonly Dictionary<BoxCutter, Vector3> lastTip = new Dictionary<BoxCutter, Vector3>();
    Vector3 cartPosition;
    Quaternion cartRotation;
    float floorContactTime = -1f;
    bool forceBeads;
    float flapHintStart = -1f;
    Material flapMaterial, hintMaterial;

    public string Label => $"{Delivery.Product.Data.Name.Replace(" (개발용)", "")} ×{Delivery.CaseQuantity} → {Delivery.TargetSlot}";

    public struct Look
    {
        public Material Cardboard, Tape, Hole, Bead, Hint;
    }

    /// <summary>Builds the box inactive, wires every component, then switches it on so XRI registers the right colliders.</summary>
    public static StockBox Create(Delivery delivery, IntakeDirector intake, Vector3 position, Quaternion rotation, Look look)
    {
        var holder = new GameObject("Box Holder (inactive)");
        holder.SetActive(false);
        var root = new GameObject($"Stock Box {delivery.BoxSequence} ({delivery.Product.Data.Id} x{delivery.CaseQuantity})");
        root.transform.SetParent(holder.transform, false);
        root.transform.SetPositionAndRotation(position, rotation);
        root.layer = LayerMask.NameToLayer("Interactable");
        var box = root.AddComponent<StockBox>();
        box.Delivery = delivery;
        box.Intake = intake;
        box.cartPosition = position;
        box.cartRotation = rotation;
        box.BuildBody(look);
        root.transform.SetParent(null, true);
        Destroy(holder);
        return box;
    }

    void BuildBody(Look look)
    {
        flapMaterial = look.Cardboard;
        hintMaterial = look.Hint;
        var size = Delivery.Product.Proxy.size;
        int columns = 2, rows = Delivery.CaseQuantity / 2;
        Inner = new Vector3(columns * size.x + 0.01f, size.y + 0.01f, rows * size.z + 0.01f);
        float ox = Inner.x * 0.5f + Wall, oz = Inner.z * 0.5f + Wall;

        Body = gameObject.AddComponent<Rigidbody>();
        Body.mass = Mass;
        Body.interpolation = RigidbodyInterpolation.Interpolate;
        Body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

        var body = new GameObject("Body").transform;
        body.SetParent(transform, false);
        bodyColliders.Add(Part(body, "Bottom", new Vector3(0f, Wall * 0.5f, 0f), new Vector3(Inner.x + 2f * Wall, Wall, Inner.z + 2f * Wall), look.Cardboard, true));
        float wallY = Wall + Inner.y * 0.5f;
        bodyColliders.Add(Part(body, "Wall Front", new Vector3(0f, wallY, -oz + Wall * 0.5f), new Vector3(Inner.x + 2f * Wall, Inner.y, Wall), look.Cardboard, true));
        bodyColliders.Add(Part(body, "Wall Back", new Vector3(0f, wallY, oz - Wall * 0.5f), new Vector3(Inner.x + 2f * Wall, Inner.y, Wall), look.Cardboard, true));
        bodyColliders.Add(Part(body, "Wall L", new Vector3(-ox + Wall * 0.5f, wallY, 0f), new Vector3(Wall, Inner.y, Inner.z), look.Cardboard, true));
        bodyColliders.Add(Part(body, "Wall R", new Vector3(ox - Wall * 0.5f, wallY, 0f), new Vector3(Wall, Inner.y, Inner.z), look.Cardboard, true));

        // Hand holes on the two short sides: the only colliders the box is grabbed by (doc 5.7.3 HandleL/R).
        float handleY = OuterHeight - Mathf.Min(0.05f, Inner.y * 0.35f);
        var handleSize = new Vector3(0.03f, 0.035f, Mathf.Min(0.1f, Inner.z - 0.01f));
        Handles = new[]
        {
            Part(transform, "HandleL", new Vector3(-ox - handleSize.x * 0.5f, handleY, 0f), handleSize, look.Hole, true),
            Part(transform, "HandleR", new Vector3(ox + handleSize.x * 0.5f, handleY, 0f), handleSize, look.Hole, true),
        };
        bodyColliders.AddRange(Handles);

        // Main flaps on the long edges, meeting over the middle; minor flaps on the short edges underneath (visual).
        float flapWidth = oz;
        MainFlaps = new[]
        {
            BoxFlap.Create(this, "Flap Front", new Vector3(0f, OuterHeight, -oz), new Vector3(0f, 0f, 1f), Inner.x + 2f * Wall, flapWidth, look.Cardboard),
            BoxFlap.Create(this, "Flap Back", new Vector3(0f, OuterHeight, oz), new Vector3(0f, 0f, -1f), Inner.x + 2f * Wall, flapWidth, look.Cardboard),
        };
        foreach (var flap in MainFlaps) bodyColliders.Add(flap.Panel);
        minorFlaps = new[] { MinorFlap("Minor Flap L", -ox, 1f, look.Cardboard), MinorFlap("Minor Flap R", ox, -1f, look.Cardboard) };

        // Tape across the seam, one half on each main flap so each half goes with its flap once cut.
        float tapeLength = Inner.x + 2f * Wall + 0.04f;
        tapeA = Visual(MainFlaps[0].Panel.transform.parent, "Tape (front half)", MainFlaps[0].Panel.transform.localPosition + new Vector3(0f, 0.003f, flapWidth * 0.5f - 0.0125f), new Vector3(tapeLength, 0.001f, 0.025f), look.Tape);
        tapeB = Visual(MainFlaps[1].Panel.transform.parent, "Tape (back half)", MainFlaps[1].Panel.transform.localPosition + new Vector3(0f, 0.003f, -flapWidth * 0.5f + 0.0125f), new Vector3(tapeLength, 0.001f, 0.025f), look.Tape);

        // Six checkpoints along the tape line (doc 5.7.1 B3). Blade tips are tested against them each frame, so a quick
        // stroke cannot skip one between physics steps.
        checkpoints = Mathf.Max(2, CheckpointsPerBox);
        checkpointLocal = new Vector3[checkpoints];
        float span = Inner.x - 0.04f;
        for (int i = 0; i < checkpoints; i++)
        {
            checkpointLocal[i] = new Vector3(-span * 0.5f + span * i / (checkpoints - 1), OuterHeight + 0.006f, 0f);
            var bead = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Destroy(bead.GetComponent<Collider>());
            bead.name = $"Checkpoint {i + 1}";
            bead.transform.SetParent(transform, false);
            bead.transform.localPosition = checkpointLocal[i];
            bead.transform.localScale = Vector3.one * 0.016f;
            bead.GetComponent<Renderer>().sharedMaterial = look.Bead;
            bead.SetActive(false);
            beads.Add(bead);
        }

        // Unit places, front to the box's -z (the side with the label).
        Units = new BoxUnit[Delivery.CaseQuantity];
        for (int c = 0, i = 0; c < columns; c++)
            for (int r = 0; r < rows; r++, i++)
            {
                var local = new Vector3((c - (columns - 1) * 0.5f) * size.x, Wall + size.y * 0.5f + 0.002f, (r - (rows - 1) * 0.5f) * size.z);
                Units[i] = new BoxUnit(this, i, local);
            }

        foreach (var collider in GetComponentsInChildren<Collider>(true))
            collider.gameObject.layer = gameObject.layer;

        Grab = gameObject.AddComponent<XRGrabInteractable>();
        Grab.colliders.Clear();
        Grab.colliders.AddRange(Handles);
        Grab.movementType = XRBaseInteractable.MovementType.Instantaneous;
        Grab.throwOnDetach = false;
        Grab.useDynamicAttach = true;
        Grab.selectEntered.AddListener(OnSelected);
        Grab.selectExited.AddListener(OnDeselected);

        AddLabel(new Vector3(0f, Wall + Inner.y * 0.55f, -oz - 0.002f), Quaternion.identity);
        AddLabel(new Vector3(0f, Wall + Inner.y * 0.55f, oz + 0.002f), Quaternion.Euler(0f, 180f, 0f));
        var noticePanel = WorldUi.CreatePanel(transform, "Box Notice", transform.TransformPoint(new Vector3(0f, OuterHeight + 0.17f, 0f)), transform.rotation,
            new Vector2(700f, 90f), 0.0006f, new Color(0.62f, 0.1f, 0.18f, 0.94f));
        notice = WorldUi.AddText(noticePanel, 0f, 0f, 700f, 90f, 36, TextAnchor.MiddleCenter);
        noticePanel.gameObject.SetActive(false);
    }

    void AddLabel(Vector3 local, Quaternion turn)
    {
        float height = Mathf.Min(0.09f, Inner.y * 0.6f);
        var panel = WorldUi.CreatePanel(transform, "Box Label", transform.TransformPoint(local), transform.rotation * turn, new Vector2(620f, height / 0.0006f), 0.0006f, new Color(1f, 1f, 1f, 0.92f));
        var text = WorldUi.AddText(panel, 0f, 0f, 620f, height / 0.0006f, 38, TextAnchor.MiddleCenter);
        text.color = WorldUi.Palette.Navy;
        WorldUi.Bold(text);
        text.text = WorldUi.Clean(Label);
    }

    Transform MinorFlap(string name, float x, float inward, Material material)
    {
        var pivot = new GameObject(name).transform;
        pivot.SetParent(transform, false);
        pivot.localPosition = new Vector3(x, OuterHeight - 0.003f, 0f);
        float width = Mathf.Min(0.07f, Inner.x * 0.3f);
        Part(pivot, "Panel", new Vector3(inward * width * 0.5f, 0f, 0f), new Vector3(width, 0.003f, Inner.z - 0.004f), material, false);
        return pivot;
    }

    Transform Visual(Transform parent, string name, Vector3 local, Vector3 size, Material material)
    {
        Part(parent, name, local, size, material, false);
        return parent.Find(name);
    }

    Collider Part(Transform parent, string name, Vector3 local, Vector3 size, Material material, bool collider)
    {
        var part = GameObject.CreatePrimitive(PrimitiveType.Cube);
        part.name = name;
        part.transform.SetParent(parent, false);
        part.transform.localPosition = local;
        part.transform.localScale = size;
        if (material) part.GetComponent<Renderer>().sharedMaterial = material;
        var c = part.GetComponent<Collider>();
        if (!collider)
        {
            Destroy(c);
            return null;
        }
        return c;
    }

    // ---------- Places ----------

    void OnSelected(SelectEnterEventArgs args)
    {
        floorContactTime = -1f;
        if (args.interactorObject is BoxStandSocket stand)
        {
            Place = BoxPlace.Docked;
            DockedAt = stand;
            LastStand = stand;
            DockedSince = Time.time;
            if (Intake && Intake.Audio) Intake.Audio.PlayAt(StoreSound.Thud, transform.position);
        }
        else
        {
            Place = BoxPlace.Carried;
            if (args.interactorObject is XRBaseInputInteractor hand) hand.SendHapticImpulse(0.3f, 0.05f);
        }
        Changed?.Invoke(this);
    }

    void OnDeselected(SelectExitEventArgs args)
    {
        if (args.interactorObject is BoxStandSocket)
        {
            DockedAt = null;
            DockedSince = -1f;
        }
        if (!Grab.isSelected) Place = BoxPlace.Loose;
        lastTip.Clear();
        Changed?.Invoke(this);
    }

    void OnCollisionEnter(Collision collision)
    {
        if (!Grab.isSelected && collision.collider.GetComponent<ProductRespawnSurface>()) floorContactTime = Time.time;
    }

    void FixedUpdate()
    {
        if (Grab.isSelected || Stage == BoxStage.Recycled) return;
        bool dropped = floorContactTime >= 0f && Time.time - floorContactTime >= FloorRespawnSeconds;
        if (dropped || transform.position.y < -5f) ReturnHome();
    }

    /// <summary>Doc 5.7.1 B1: back to the last stand it sat on, else its place on the cart.</summary>
    public void ReturnHome()
    {
        floorContactTime = -1f;
        if (!Body.isKinematic)
        {
            Body.linearVelocity = Vector3.zero;
            Body.angularVelocity = Vector3.zero;
        }
        var stand = LastStand;
        if (stand != null && !stand.hasSelection)
            transform.SetPositionAndRotation(stand.attachTransform.position + Vector3.up * 0.01f, stand.attachTransform.rotation);
        else
            transform.SetPositionAndRotation(cartPosition + Vector3.up * 0.01f, cartRotation);
        Place = BoxPlace.Loose;
        Changed?.Invoke(this);
    }

    // ---------- Tape ----------

    void Update()
    {
        if (notice != null && notice.transform.parent.gameObject.activeSelf && Time.time > noticeUntil) notice.transform.parent.gameObject.SetActive(false);
        if (Stage == BoxStage.Sealed)
        {
            bool hint = forceBeads || (Place == BoxPlace.Docked && DockedSince >= 0f && Time.time - DockedSince > HintAfterDockedSeconds);
            if (beads[0].activeSelf != hint) foreach (var bead in beads) bead.SetActive(hint);
            TrackCutters();
        }
        if (Stage == BoxStage.TapeCut && flapHintStart >= 0f)
        {
            bool blink = Time.time - flapHintStart > FlapHintSeconds && Mathf.Repeat(Time.time, 0.8f) < 0.4f;
            foreach (var flap in MainFlaps) flap.SetMaterial(blink && !flap.IsOpen ? hintMaterial : flapMaterial);
        }
    }

    void TrackCutters()
    {
        if (Intake == null) return;
        foreach (var cutter in Intake.Cutters)
        {
            if (cutter == null || !cutter.Grab.isSelected)
            {
                lastTip.Remove(cutter);
                continue;
            }
            var tip = transform.InverseTransformPoint(cutter.Tip.position);
            if (!lastTip.TryGetValue(cutter, out var previous)) previous = tip;
            lastTip[cutter] = tip;
            if (CutProgress > 0 && Time.time - lastCheckpointTime > CheckpointGapSeconds) ResetCut();
            for (int guard = 0; guard < checkpoints; guard++)
            {
                int next = CutProgress == 0 ? -1 : cutDirection > 0 ? CutProgress : checkpoints - 1 - CutProgress;
                bool advanced = false;
                if (next < 0)
                {
                    if (Touches(previous, tip, 0)) advanced = BeginCut(+1, cutter);
                    else if (Touches(previous, tip, checkpoints - 1)) advanced = BeginCut(-1, cutter);
                }
                else if (Touches(previous, tip, next))
                {
                    advanced = Pass(cutter);
                }
                if (!advanced || Stage != BoxStage.Sealed) break;
            }
        }
    }

    bool Touches(Vector3 from, Vector3 to, int checkpoint)
    {
        var p = checkpointLocal[checkpoint];
        var segment = to - from;
        float t = segment.sqrMagnitude > 1e-8f ? Mathf.Clamp01(Vector3.Dot(p - from, segment) / segment.sqrMagnitude) : 0f;
        return Vector3.Distance(from + segment * t, p) <= CheckpointRadius;
    }

    bool BeginCut(int direction, BoxCutter cutter)
    {
        // Doc 5.7.1 B3: only on a stand, and doc 5.7.2: only one open box at a time.
        if (Place != BoxPlace.Docked)
        {
            ShowNotice("거치대에 올린 뒤 자르세요");
            return false;
        }
        var open = Intake.OpenBox;
        if (open != null && open != this)
        {
            ShowNotice("열린 박스를 먼저 비우세요");
            return false;
        }
        cutDirection = direction;
        return Pass(cutter);
    }

    bool Pass(BoxCutter cutter)
    {
        CutProgress++;
        lastCheckpointTime = Time.time;
        cutter.Tick();
        if (Intake.Audio) Intake.Audio.PlayAt(StoreSound.Tape, transform.TransformPoint(checkpointLocal[cutDirection > 0 ? CutProgress - 1 : checkpoints - CutProgress]));
        if (CutProgress >= checkpoints) FinishCut();
        Changed?.Invoke(this);
        return true;
    }

    void ResetCut()
    {
        CutProgress = 0;
        CutResets++;
        Changed?.Invoke(this);
    }

    /// <summary>Doc 5.7.4 I2: after four resets the tutorial cuts it, so a beginner is not stuck.</summary>
    public void AutoCut()
    {
        if (Stage == BoxStage.Sealed) FinishCut();
    }

    public void ShowBeads(bool show) => forceBeads = show;

    /// <summary>Where checkpoint i is now, for tests aiming the blade.</summary>
    public Vector3 CheckpointWorld(int i) => transform.TransformPoint(checkpointLocal[i]);
    public int CheckpointCount => checkpoints;

    void FinishCut()
    {
        Stage = BoxStage.TapeCut;
        CutAt = Time.time;
        flapHintStart = Time.time;
        foreach (var bead in beads) bead.SetActive(false);
        // The two halves part along the cut.
        tapeA.localPosition += new Vector3(0f, 0f, -0.002f);
        tapeB.localPosition += new Vector3(0f, 0f, 0.002f);
        foreach (var flap in MainFlaps) flap.SetInteractable(true);
        Changed?.Invoke(this);
    }

    // ---------- Flaps and units ----------

    public void OnFlapChanged()
    {
        if (Stage != BoxStage.TapeCut) return;
        foreach (var flap in MainFlaps)
            if (!flap.IsOpen) return;
        Open();
    }

    void Open()
    {
        Stage = BoxStage.Open;
        flapHintStart = -1f;
        foreach (var flap in MainFlaps)
        {
            flap.SetInteractable(false);
            flap.SetMaterial(flapMaterial);
        }
        foreach (var minor in minorFlaps)
            minor.localRotation = Quaternion.AngleAxis(minor.localPosition.x < 0f ? 200f : -200f, Vector3.forward);
        foreach (var unit in Units) unit.Spawn(Delivery.Product);
        if (Intake.Audio) Intake.Audio.PlayAt(StoreSound.Flap, transform.position);
        Changed?.Invoke(this);
    }

    public void IgnoreBody(Collider other, bool ignore = true)
    {
        foreach (var own in bodyColliders)
            if (own != null && other != null) Physics.IgnoreCollision(own, other, ignore);
    }

    public void OnUnitShelved()
    {
        foreach (var unit in Units)
            if (!unit.Done) return;
        Stage = BoxStage.Empty;
        Changed?.Invoke(this);
    }

    public void Recycle()
    {
        Stage = BoxStage.Recycled;
        Changed?.Invoke(this);
        if (Grab.isSelected && Grab.interactionManager != null)
            Grab.interactionManager.CancelInteractableSelection((IXRSelectInteractable)Grab);
        Destroy(gameObject);
    }

    public void ShowNotice(string text)
    {
        if (notice == null) return;
        notice.text = text;
        noticeUntil = Time.time + NoticeSeconds;
        var panel = notice.transform.parent;
        panel.gameObject.SetActive(true);
        // Readable from the side the player is on.
        var eye = Camera.main != null ? Camera.main.transform.position : panel.position - Vector3.forward;
        var away = panel.position - eye;
        away.y = 0f;
        if (away.sqrMagnitude > 1e-4f) panel.rotation = Quaternion.LookRotation(away);
    }
}

/// <summary>One place in a box (doc 5.7.3 BoxUnit). Its product is made when the box opens.</summary>
public class BoxUnit
{
    public readonly StockBox Box;
    public readonly int Index;
    public readonly Vector3 Local;
    public ProductPrefab Product { get; private set; }
    /// <summary>Out of the box: in a hand, dropped, or on its way back.</summary>
    public bool Taken { get; private set; }
    /// <summary>Accepted into its shelf slot.</summary>
    public bool Done { get; private set; }

    public BoxUnit(StockBox box, int index, Vector3 local)
    {
        Box = box;
        Index = index;
        Local = local;
    }

    public void Spawn(ProductPrefab prefab)
    {
        var rotation = Box.transform.rotation * Quaternion.Euler(0f, 180f, 0f);
        Product = ProductPrefab.Spawn(prefab, Box.transform.TransformPoint(Local) - rotation * prefab.Proxy.center, rotation, p => p.UseLightSplat());
        Product.name = $"{prefab.name} (box {Box.Delivery.BoxSequence} unit {Index + 1})";
        Box.IgnoreBody(Product.Proxy);
        Product.RestHere();
        Product.ReturnUnit = this;
        Product.transform.SetParent(Box.transform, true);
        Product.Grabbed += _ => Taken = true;
    }

    /// <summary>Back into its place (drop, misplacement). False once the box is gone.</summary>
    public bool TakeBack(ProductPrefab product)
    {
        if (Box == null || Box.Stage == BoxStage.Recycled || product != Product) return false;
        var body = product.Body;
        if (!body.isKinematic)
        {
            body.linearVelocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
        }
        body.isKinematic = true;
        var rotation = Box.transform.rotation * Quaternion.Euler(0f, 180f, 0f);
        product.transform.SetParent(Box.transform, true);
        product.TeleportTo(Box.transform.TransformPoint(Local) - rotation * product.Proxy.center, rotation);
        product.UseLightSplat();
        Taken = false;
        return true;
    }

    public void MarkDone()
    {
        Done = true;
        Taken = false;
        Box.OnUnitShelved();
    }
}
