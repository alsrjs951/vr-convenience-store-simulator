using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

/// <summary>
/// -p2-film: plays the demo's three customers (no tutorial) through the same virtual devices as the checks, while
/// FilmRecorder films it from several cameras at once (2026-11-14, a first rough cut of the demo video). Not a check:
/// what the helpers find on the way is logged and nothing more. The first product of every order is held up and turned
/// in front of the hand camera before it is scanned.
///   -p2-evidence FOLDER   where the films go (default capture/film/take)
///   -film-size 1920x1080  -film-fps 30  -film-ffmpeg PATH
/// Editor only, XR Device Simulator: the hands move the way the checks move them, not the way a person does.
///
/// -p2-film-growth (with -save-path FILE, a copy of docs/p2/quest2_saves/day5_two_boxes.json) films the other half of
/// the game instead, for the presentation's film: the run of -p2-growth-hands as it is (a box of new products carried
/// from the cart, its tape cut, its flaps opened, the products put on a display shelf, and after the day something
/// ordered at the terminal), with cameras of its own that go where the player goes.
///
/// -p2-film-intro films the first minute, the run of -p2-intro-verify up to the first customer of the lesson: the title
/// seen through the glass door, the button pressed, the manager waving, the walk in along the arrows, the time card.
///
/// In the counter's film the third customer is given too little change at first (one note, then a wait), so the film has
/// the customer asking for the rest; the rest follows.
/// </summary>
public partial class P2DayVerification
{
    const float FilmMaxSeconds = 480f;
    const float ShowTurnDegreesPerSecond = 80f;
    // Over the scanner and above what its glass reads, so turning the product here scans nothing.
    static readonly Vector3 ShowSpot = new Vector3(0.45f, 1.45f, 0.25f);
    // Where the left hand waits while the right one works: down by the left hip, below the counter's edge. On the
    // counter there is no place left for it (2026-11-15): the bag stands where it lay, and between the bag and the
    // scanner is where a thing rung up is set aside.
    static readonly Vector3 LeftHandRest = new Vector3(-0.25f, 0.8f, -0.05f);

    bool filmMode, filmGrowth, filmIntro;
    /// <summary>The film of the demo's three customers at the counter, and not one of the films of a check's run.</summary>
    bool FilmsTheCounter => filmMode && !filmGrowth && !filmIntro;
    FilmRecorder film;
    readonly List<Behaviour> hiddenRayParts = new List<Behaviour>();
    readonly List<LineRenderer> hiddenRays = new List<LineRenderer>();

    /// <summary>What the waits are timed by: the film's own time while filming (a second of film can take several to draw).</summary>
    float Clock => filmMode ? Time.time : Time.realtimeSinceStartup;

    static string Argument(string[] args, string name, string otherwise)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : otherwise;
    }

    IEnumerator RunFilm()
    {
        yield return new WaitForSecondsRealtime(StartupSeconds);
        if (!FindRig()) yield break;
        view.fieldOfView = ViewFieldOfView;
        foreach (var canvas in FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (canvas.renderMode != RenderMode.WorldSpace) canvas.enabled = false;
        yield return FeedMouse(new Vector2(0f, LookDownMouseUnits), LookMouseUnitsPerSecond);
        yield return new WaitForSeconds(0.3f);
        yield return Tap(Key.Y);
        yield return Tap(Key.R);
        HidePointingRays();
        yield return RestLeftHand();

        var args = Environment.GetCommandLineArgs();
        var size = Argument(args, "-film-size", "1920x1080").Split('x');
        int fps = int.Parse(Argument(args, "-film-fps", "30"));
        film = FilmRecorder.Begin(output, Argument(args, "-film-ffmpeg", "ffmpeg"), int.Parse(size[0]), int.Parse(size[1]), fps);
        AddFilmCameras();
        film.Mark($"start state={Game.State} demo={Game.Demo}");
        StartCoroutine(FilmWatchdog());
        StartCoroutine(CounterFilmMarks());

        var director = Game.Director;
        var pos = Game.Pos;
        for (int number = 1; number <= DemoCustomers && !finished; number++)
        {
            yield return WaitUntil(() => director.CustomerNumber == number && pos.Current != null, 60f, $"film.order{number}.arrived");
            if (finished) yield break;
            var order = pos.Current;
            var buyer = director.AtCounter;
            film.Mark($"order{number} arrived {order.Payment} bag={order.NeedsBag} {(buyer != null ? buyer.name : "nobody")}");
            yield return GoodsSetDown($"film.order{number}", director.OrderProducts);
            film.Mark($"order{number} goods down");
            var products = new List<ProductPrefab>(director.OrderProducts);
            for (int i = 0; i < products.Count && !finished; i++)
            {
                string label = $"film.order{number}.item{i}";
                film.Mark($"order{number} item{i} reach {products[i].Data.Id}");
                yield return GrabProduct(products[i], label);
                film.Mark($"order{number} item{i} in hand");
                if (i == 0) yield return ShowToTheCamera(products[i], number);
                int scansBefore = scansSeen;
                yield return BringBeforeScannerForTheFilm(products[i]);
                film.Mark($"order{number} item{i} before the scanner");
                yield return ShowForTheFilm(products[i], label + ".scan", scansBefore);
                film.Mark($"order{number} item{i} scanned");
                if (order.NeedsBag) yield return PutInBag(products[i], label + ".bag");
                else yield return SetAside(products[i], i);
                film.Mark($"order{number} item{i} {(order.NeedsBag ? "in the bag" : "set aside")}");
                yield return ResetHandRotation();
            }
            if (order.Payment == PaymentMethod.Card)
            {
                film.Mark($"order{number} card begins");
                yield return PayByCard(() => pos.Current == null, $"film.order{number}.card");
            }
            else
            {
                int total = pos.Total;
                int bill = PaymentDesk.BillFor(total);
                film.Mark($"order{number} cash begins total={total} note={bill}");
                yield return GiveBill($"film.order{number}.bill");
                film.Mark($"order{number} note in the till");
                // The last customer gets one note and then nothing for a while: too little, and they say so.
                yield return GiveChange(bill - total, 0, number == DemoCustomers, () => pos.Current == null, $"film.order{number}.change");
            }
            yield return WaitUntil(() => pos.Current == null, 10f, $"film.order{number}.completed");
            film.Mark($"order{number} paid");
            yield return LeavesWithOrWithoutBag($"film.order{number}", order, buyer);
            film.Mark($"order{number} customer has their things");
        }
        if (finished) yield break;

        yield return WaitUntil(() => Game.State == DayState.DayEnd, 30f, "film.day_end");
        film.Mark($"day end grade={Game.Grade} score={Game.Score:0}");
        yield return new WaitForSeconds(5f);
        film.Mark("end");
        Finish();
    }

    /// <summary>
    /// The lines the hands point with are for aiming at buttons; on film they are two white strokes across every picture.
    /// Only for the film: the game itself is left as it is.
    /// </summary>
    void HidePointingRays()
    {
        int hidden = 0;
        foreach (var line in view.transform.root.GetComponentsInChildren<LineRenderer>(true))
        {
            foreach (var behaviour in line.GetComponents<MonoBehaviour>())
                if (behaviour != null && behaviour.enabled && (behaviour.GetType().Name.Contains("LineVisual") || behaviour.GetType().Name.Contains("CurveVisual")))
                {
                    behaviour.enabled = false;
                    hiddenRayParts.Add(behaviour);
                }
            if (line.enabled) hiddenRays.Add(line);
            line.enabled = false;
            hidden++;
        }
        Debug.Log($"[Film] pointing rays hidden: {hidden}");
    }

    /// <summary>Back again for what is done with them: the ordering terminal's button is pressed with the ray.</summary>
    void ShowPointingRays()
    {
        foreach (var line in hiddenRays)
            if (line != null) line.enabled = true;
        foreach (var part in hiddenRayParts)
            if (part != null) part.enabled = true;
        Debug.Log($"[Film] pointing rays shown again: {hiddenRays.Count}");
        hiddenRays.Clear();
        hiddenRayParts.Clear();
    }

    /// <summary>
    /// The left hand is not used at the counter and stays where the simulator starts it, held out in the air. It is
    /// brought down to rest by the counter's edge, through the simulator like every other move, and the right hand is
    /// taken up again.
    /// </summary>
    IEnumerator RestLeftHand() => RestLeftHand(LeftHandRest);

    IEnumerator RestLeftHand(Vector3 rest)
    {
        Transform left = null;
        foreach (var interactor in FindObjectsByType<NearFarInteractor>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (interactor.transform.parent != null && interactor.transform.parent.name.Contains("Left")) left = interactor.transform.parent;
        if (left == null) yield break;
        yield return Tap(Key.T);
        if (simulator.manipulatingLeftController && !simulator.manipulatingRightController)
        {
            var right = rightHand;
            rightHand = left;
            yield return MoveHand(new Vector3(rest.x, left.position.y, rest.z), "film.left_hand.over");
            yield return MoveHand(rest, "film.left_hand.down");
            rightHand = right;
        }
        yield return Tap(Key.Y);
        Check("film.right_hand_taken_up_again", simulator.manipulatingRightController && !simulator.manipulatingLeftController,
            $"left={simulator.manipulatingLeftController}; right={simulator.manipulatingRightController}; left hand at {left.position.ToString("F2")}");
    }

    /// <summary>A film that never ends is a film nobody sees: whatever has stalled, close the files and go.</summary>
    IEnumerator FilmWatchdog()
    {
        while (!finished && film != null && film.Seconds < FilmMaxSeconds) yield return null;
        if (finished) yield break;
        Check("film.ended_by_itself", false, $"still going after {FilmMaxSeconds} s of film; state={Game.State}; order on the POS={Game.Pos.Current != null}");
        Finish();
    }

    /// <summary>Up beside the scanner, and once around slowly: what the scan of the real package looks like from every side.</summary>
    IEnumerator ShowToTheCamera(ProductPrefab product, int number)
    {
        var center = product.transform.position;
        yield return MoveProduct(product, new Vector3(center.x, TravelHeight, center.z), "film.show.rise");
        yield return MoveProduct(product, ShowSpot, "film.show.to");
        film.Mark($"order{number} show begins");
        yield return TurnHandSlowly(180f);
        yield return new WaitForSeconds(0.5f);
        yield return TurnHandSlowly(180f);
        yield return new WaitForSeconds(0.3f);
        film.Mark($"order{number} show ends");
    }

    /// <summary>
    /// As a clerk does it: the thing stays upright as it was taken from the row, its barcode on the far side, and is
    /// brought in front of the scanner on the clerk's side. Only a barcode printed on another face is turned to the
    /// far side first, about the upright axis. (The checks lay the barcode down and stand it up again, which reads the
    /// same and looks like nobody's hands.)
    /// </summary>
    IEnumerator BringBeforeScannerForTheFilm(ProductPrefab product)
    {
        var glass = Scanner.Shown;
        var center = product.transform.position;
        // High: the way from the row to the clerk's side goes over the scanner, and a thing carried upright through the
        // space it reads, its barcode on the far side, is read on the way (take 14: read twice, and the sale would not close).
        float travel = Mathf.Max(center.y, ShowSpot.y + 0.05f);
        yield return MoveProduct(product, new Vector3(center.x, travel, center.z), "film.scanner.rise");
        var normal = product.transform.TransformDirection(product.Data.BarcodeLocalNormal);
        var flat = Vector3.ProjectOnPlane(normal, Vector3.up);
        float yaw = flat.sqrMagnitude > 0.01f ? -Vector3.SignedAngle(Vector3.forward, flat, Vector3.up) : 0f;
        if (Mathf.Abs(yaw) > 5f)
        {
            yield return RotateHand(new Vector3(0f, yaw, 0f));
            yield return new WaitForSeconds(0.3f);
        }
        normal = product.transform.TransformDirection(product.Data.BarcodeLocalNormal);
        Check("film.barcode_faces_the_glass", Vector3.Angle(normal, -glass.up) < 35f, $"{product.Data.Id} angle from the glass={Vector3.Angle(normal, -glass.up):0.0}; turned by {yaw:0}");
        center = product.transform.position;
        float height = Mathf.Max(glass.position.y, CounterTop + OverTheTerminal + center.y - LowestProxyPoint(product));
        // Down in front of the scanner from above the space it reads, well short of it.
        float near = Mathf.Max(ShowFrom, center.z + glass.position.z - ShowFromGlass - 0.06f - BarcodeFaceZ(product));
        yield return MoveProduct(product, new Vector3(glass.position.x, travel, near), "film.scanner.over");
        yield return MoveProduct(product, new Vector3(glass.position.x, height, near), "film.scanner.level");
    }

    /// <summary>In toward the glass until it is read, and held there a moment for the picture.</summary>
    IEnumerator ShowForTheFilm(ProductPrefab product, string label, int before)
    {
        var glass = Scanner.Shown;
        var target = rightHand.position;
        float begun = Clock;
        while (scansSeen == before && Clock - begun < LowerTimeout * 2f && glass.position.z - BarcodeFaceZ(product) > 0.04f)
        {
            target += Vector3.forward * ShowStep;
            yield return MoveHand(target, label + ".approach");
            yield return new WaitForSeconds(0.05f);
        }
        Check(label, scansSeen > before, $"{product.Data.Id} scans {before}->{scansSeen}; gap to the glass={glass.position.z - BarcodeFaceZ(product):0.000}");
        yield return new WaitForSeconds(0.4f);
    }

    /// <summary>RotateHand's yaw, at a speed fit to be looked at.</summary>
    IEnumerator TurnHandSlowly(float degrees)
    {
        float sensitivity = simulator.mouseXRotateSensitivity;
        heldKeys.Add(Key.LeftCtrl);
        yield return null;
        yield return FeedMouse(new Vector2(degrees / sensitivity, 0f), ShowTurnDegreesPerSecond / sensitivity);
        yield return null;
        heldKeys.Remove(Key.LeftCtrl);
        handEuler += new Vector3(0f, degrees, 0f);
        yield return null;
    }

    /// <summary>A camera in front of one of the world's screens (a canvas is read looking along its forward).</summary>
    void AddScreenCamera(string name, string canvasName)
    {
        var found = GameObject.Find(canvasName);
        if (found == null)
        {
            Debug.LogWarning($"[Film] no {canvasName}: no camera {name}");
            return;
        }
        AddScreenCamera(name, (RectTransform)found.transform);
    }

    void AddScreenCamera(string name, RectTransform screen)
    {
        const float fieldOfView = 30f, margin = 1.3f;
        film.Add(name, fieldOfView, (camera, t) =>
        {
            float height = screen.rect.height * screen.lossyScale.y, width = screen.rect.width * screen.lossyScale.x;
            float tall = Mathf.Max(height, width * 9f / 16f) * margin;
            float distance = tall * 0.5f / Mathf.Tan(fieldOfView * 0.5f * Mathf.Deg2Rad);
            var middle = screen.TransformPoint(screen.rect.center);
            camera.transform.SetPositionAndRotation(middle - screen.forward * distance, Quaternion.LookRotation(screen.forward, screen.up));
        });
    }

    /// <summary>What the counter's film cannot mark from its own steps: the change on the tray and what the customer makes of it.</summary>
    IEnumerator CounterFilmMarks()
    {
        var director = Game.Director;
        var watched = new List<(string name, Func<string> read)>
        {
            ("tray", () => Desk.Tray.Amount.ToString()),
            ("asked for the rest", () => director.ChangeAsks.ToString()),
            ("mood", () => director.AtCounter != null && director.AtCounter.FaceRig != null ? director.AtCounter.FaceRig.Mood.ToString() : "nobody"),
            ("tips", () => director.Tips.ToString()),
            ("till short", () => director.TillShort.ToString()),
            ("state", () => Game.State.ToString()),
        };
        yield return WatchForTheFilm(watched);
    }

    IEnumerator WatchForTheFilm(List<(string name, Func<string> read)> watched)
    {
        var last = new string[watched.Count];
        while (!finished && film != null)
        {
            for (int i = 0; i < watched.Count; i++)
            {
                string now = watched[i].read().Replace(',', ' ');
                if (now == last[i]) continue;
                last[i] = now;
                film.Mark($"{watched[i].name}: {now}");
            }
            yield return null;
        }
    }

    // ---------- The first minute: the title, the walk in, the time card ----------

    IEnumerator BeginIntroFilm()
    {
        // As in the store's film: the ring that narrows the view while walking is a black ball to every other camera.
        var vignette = FindFirstObjectByType<UnityEngine.XR.Interaction.Toolkit.Locomotion.Comfort.TunnelingVignetteController>();
        if (vignette != null) vignette.gameObject.SetActive(false);
        var head = view.transform.position;
        yield return RestLeftHand(new Vector3(head.x - 0.25f, 0.8f, head.z - 0.05f));

        var args = Environment.GetCommandLineArgs();
        var size = Argument(args, "-film-size", "1920x1080").Split('x');
        int fps = int.Parse(Argument(args, "-film-fps", "30"));
        film = FilmRecorder.Begin(output, Argument(args, "-film-ffmpeg", "ffmpeg"), int.Parse(size[0]), int.Parse(size[1]), fps);
        AddIntroFilmCameras();
        film.Mark($"start state={Game.State}");
        StartCoroutine(FilmWatchdog());
        var manager = Game.Manager;
        var director = Game.Director;
        StartCoroutine(WatchForTheFilm(new List<(string name, Func<string> read)>
        {
            ("state", () => Game.State.ToString()),
            ("title", () => Title.PanelShown ? "shown" : "gone"),
            ("door", () => Title.DoorClosed ? "closed" : "open"),
            ("guide", () => Title.GuideShown ? "shown" : "gone"),
            ("manager", () => manager != null && manager.Body != null ? $"{manager.Body.Playing} waves={manager.Waves} nods={(manager.Body.FaceRig != null ? manager.Body.FaceRig.Nods : 0)}" : "gone"),
            ("said", () => Game.Pos.Audio.LastNarration ?? ""),
            ("card", () => Desk.CustomerCard != null ? "on the plate" : "none"),
            ("customers", () => director.Present.Count.ToString()),
        }));
    }

    void AddIntroFilmCameras()
    {
        // What the player sees.
        var gaze = view.transform.rotation;
        film.Add("eyes", 72f, (camera, t) =>
        {
            gaze = Quaternion.Slerp(gaze, view.transform.rotation, 1f - Mathf.Exp(-6f * Time.deltaTime));
            camera.transform.SetPositionAndRotation(view.transform.position, gaze);
        });

        // The manager, from where the player is: the wave through the door, the nod at the time card.
        var manager = Game.Manager;
        Vector3 faceAt = manager != null && manager.Body != null ? manager.Body.transform.position + Vector3.up * 1.4f : new Vector3(0f, 1.4f, 1.5f), faceSpeed = Vector3.zero;
        film.Add("manager", 24f, (camera, t) =>
        {
            if (manager != null && manager.Body != null)
                faceAt = Vector3.SmoothDamp(faceAt, manager.Body.transform.position + Vector3.up * 1.32f, ref faceSpeed, 0.4f);
            Aim(camera, view.transform.position, faceAt);
        });

        // The way in, from the corner behind the counter: the door, the arrows on the floor, the ring by the counter.
        film.Add("walk", 58f, (camera, t) => Aim(camera, new Vector3(0.9f, 2.35f, -1.9f), new Vector3(-2.3f, 0.3f, -0.6f)));

        // The right hand and what it holds, as at the counter: the time card to the terminal and back.
        var drawnHand = rightHand.GetComponentInChildren<PlayerHand>();
        Func<Vector3> hand = () => drawnHand != null ? drawnHand.transform.position : rightHand.position;
        Vector3 handAt = hand(), handSpeed = Vector3.zero, handFrom = hand() + new Vector3(0.3f, 0.28f, -0.5f), fromSpeed = Vector3.zero;
        film.Add("hand", 40f, (camera, t) =>
        {
            var target = rightInteractor.hasSelection ? rightInteractor.interactablesSelected[0].transform.position : hand();
            handAt = Vector3.SmoothDamp(handAt, target, ref handSpeed, 0.2f);
            float left = Mathf.InverseLerp(0f, -0.3f, handAt.x);
            var offset = Vector3.Lerp(new Vector3(0.3f, 0.28f, -0.5f), new Vector3(-0.2f, 0.6f, -0.3f), left);
            handFrom = Vector3.SmoothDamp(handFrom, handAt + offset, ref fromSpeed, 0.35f);
            Aim(camera, handFrom, handAt);
        });

        // Straight down on the counter, as in its own film.
        film.Add("top", 48f, (camera, t) => camera.transform.SetPositionAndRotation(new Vector3(0.12f, 2.15f, 0.6f), Quaternion.LookRotation(Vector3.down, Vector3.forward)));
    }

    // ---------- The store that is kept: boxes of new products, the display shelves, the ordering terminal ----------

    IEnumerator BeginGrowthFilm(StockBox nearBox, StockBox farBox, ShelfSlotZone nearSlot, ShelfSlotZone farSlot)
    {
        HidePointingRays();
        // The dark ring that narrows the view while walking and turning is drawn around the head, for the head: every
        // other camera sees it as a black ball. Only for the film.
        var vignette = FindFirstObjectByType<UnityEngine.XR.Interaction.Toolkit.Locomotion.Comfort.TunnelingVignetteController>();
        if (vignette != null) vignette.gameObject.SetActive(false);
        // The left hand goes with the body through the store: down by the left hip, not held out into the shelves.
        var head = view.transform.position;
        yield return RestLeftHand(new Vector3(head.x - 0.25f, 0.8f, head.z - 0.05f));

        var args = Environment.GetCommandLineArgs();
        var size = Argument(args, "-film-size", "1920x1080").Split('x');
        int fps = int.Parse(Argument(args, "-film-fps", "30"));
        film = FilmRecorder.Begin(output, Argument(args, "-film-ffmpeg", "ffmpeg"), int.Parse(size[0]), int.Parse(size[1]), fps);
        AddGrowthFilmCameras(nearBox, farBox, nearSlot, farSlot);
        film.Mark($"start state={Game.State} day={Game.Day} near={nearBox.Delivery.Product.Data.Id} to {nearSlot.name} far={farBox.Delivery.Product.Data.Id} to {farSlot.name}");
        StartCoroutine(FilmWatchdog());
        StartCoroutine(GrowthFilmMarks(nearBox, farBox, nearSlot, farSlot));
    }

    /// <summary>
    /// The run itself is the check's and marks nothing: what changes in the store is watched from here and written down
    /// with the film's time, for finding the moments at the cutting. No commas (the marks are a CSV).
    /// </summary>
    IEnumerator GrowthFilmMarks(StockBox nearBox, StockBox farBox, ShelfSlotZone nearSlot, ShelfSlotZone farSlot)
    {
        Func<StockBox, string> flaps = box =>
        {
            int open = 0;
            foreach (var flap in box.MainFlaps)
                if (flap.IsOpen) open++;
            return open.ToString();
        };
        var watched = new List<(string name, Func<string> read)>
        {
            ("near box", () => $"{nearBox.Place} {nearBox.Stage}"),
            ("near flaps", () => flaps(nearBox)),
            ("near shelf", () => nearSlot.Filled.ToString()),
            ("far box", () => $"{farBox.Place} {farBox.Stage}"),
            ("far flaps", () => flaps(farBox)),
            ("far shelf", () => farSlot.Filled.ToString()),
            ("holding", () => rightInteractor.hasSelection ? rightInteractor.interactablesSelected[0].transform.name : "nothing"),
            ("green place", () => Preview.GhostVisible && Preview.Slot != null ? $"{Preview.Slot.name} {Preview.Unit}" : "none"),
            ("at", () => at),
            ("state", () => Game.State.ToString()),
            ("tablet", () => Game.Tablet.Shown ? "shown" : "off"),
            ("on order", () => Game.Progress.arriving.Count.ToString()),
            ("customer", () => $"{Game.Director.CustomerNumber}{(Game.Pos.Current != null ? " at the POS" : "")}"),
        };
        yield return WatchForTheFilm(watched);
    }

    void AddGrowthFilmCameras(StockBox nearBox, StockBox farBox, ShelfSlotZone nearSlot, ShelfSlotZone farSlot)
    {
        Func<Vector3> ahead = () => Vector3.ProjectOnPlane(view.transform.forward, Vector3.up).normalized;

        // What the player sees: the head's own place and gaze, the snap turns eased so the picture does not jump.
        var gaze = view.transform.rotation;
        film.Add("eyes", 72f, (camera, t) =>
        {
            gaze = Quaternion.Slerp(gaze, view.transform.rotation, 1f - Mathf.Exp(-6f * Time.deltaTime));
            camera.transform.SetPositionAndRotation(view.transform.position, gaze);
        });

        // From behind the hand and to its left wherever the player turns, on what the right hand holds (to the right the
        // end of a gondola stands in the way at stand B, take 9).
        var drawnHand = rightHand.GetComponentInChildren<PlayerHand>();
        Func<Vector3> hand = () => drawnHand != null ? drawnHand.transform.position : rightHand.position;
        Func<Vector3, Vector3> behind = held =>
        {
            var forward = ahead();
            return held - forward * 0.8f - Vector3.Cross(Vector3.up, forward) * 0.35f + Vector3.up * 0.32f;
        };
        Vector3 heldAt = hand(), heldSpeed = Vector3.zero, shoulder = behind(hand()), shoulderSpeed = Vector3.zero;
        film.Add("follow", 46f, (camera, t) =>
        {
            var target = rightInteractor.hasSelection ? rightInteractor.interactablesSelected[0].transform.position : hand();
            heldAt = Vector3.SmoothDamp(heldAt, target, ref heldSpeed, 0.2f);
            shoulder = Vector3.SmoothDamp(shoulder, behind(heldAt), ref shoulderSpeed, 0.35f);
            Aim(camera, shoulder, heldAt);
        });

        // The cart behind the counter with the morning's two boxes on it.
        var cart = (nearBox.transform.position + farBox.transform.position) * 0.5f;
        film.Add("cart", 40f, (camera, t) => Aim(camera, cart + new Vector3(-1.0f, 0.75f, 1.35f), cart + Vector3.up * 0.05f));

        // The box being worked on, from in front of it and above: the tape, the flaps, what stands inside. It goes with the
        // box while that is carried; on the cart and on a stand it looks from the side the player works from.
        Func<StockBox, Vector3> over = box =>
        {
            var forward = box.Place == BoxPlace.Docked ? Vector3.forward : box.Place == BoxPlace.Cart ? Vector3.back : ahead();
            return box.transform.position - forward * 0.7f + Vector3.Cross(Vector3.up, forward) * 0.3f + Vector3.up * 0.6f;
        };
        Vector3 boxFrom = over(nearBox), boxFromSpeed = Vector3.zero, boxAt = nearBox.transform.position, boxAtSpeed = Vector3.zero;
        film.Add("box", 36f, (camera, t) =>
        {
            var box = nearBox.Stage != BoxStage.Empty ? nearBox : farBox;
            boxAt = Vector3.SmoothDamp(boxAt, box.transform.position + Vector3.up * 0.06f, ref boxAtSpeed, 0.25f);
            boxFrom = Vector3.SmoothDamp(boxFrom, over(box), ref boxFromSpeed, 0.4f);
            Aim(camera, boxFrom, boxAt);
        });

        // The two display shelves the products go on, close: the green place, the product standing there.
        foreach (var (name, slot) in new[] { ("place_near", nearSlot), ("place_far", farSlot) })
        {
            var spot = slot.transform.position;
            film.Add(name, 50f, (camera, t) => Aim(camera, spot + new Vector3(0.25f, 0.3f, -0.75f), spot + new Vector3(0f, 0.1f, 0.05f)));
        }

        // The ordering terminal: its screen square on, and from where the clerk stands with the hand that points at it.
        var terminal = Game.Tablet.GetComponentInChildren<Canvas>(true);
        if (terminal != null) AddScreenCamera("tablet", (RectTransform)terminal.transform);
        var terminalAt = Game.Tablet.transform.position;
        film.Add("order_hand", 50f, (camera, t) => Aim(camera, new Vector3(0.35f, 1.55f, -0.25f), terminalAt + Vector3.down * 0.12f));
    }

    static void Aim(Camera camera, Vector3 from, Vector3 at)
    {
        camera.transform.SetPositionAndRotation(from, Quaternion.LookRotation(at - from));
    }

    void AddFilmCameras()
    {
        var director = Game.Director;

        // Where the clerk's eyes are, looking over the counter at whoever stands there.
        film.Add("pov", 60f, (camera, t) => Aim(camera, new Vector3(0f, 1.6f, -0.2f), new Vector3(0.18f, 1.0f, 0.8f)));

        // Over the clerk's right shoulder, staying with the right hand and what it holds.
        var drawnHand = rightHand.GetComponentInChildren<PlayerHand>();
        Func<Vector3> hand = () => drawnHand != null ? drawnHand.transform.position : rightHand.position;
        Vector3 handAt = hand(), handSpeed = Vector3.zero, handFrom = hand() + new Vector3(0.3f, 0.28f, -0.5f), fromSpeed = Vector3.zero;
        film.Add("hand", 40f, (camera, t) =>
        {
            // What it holds is what is looked at; far enough back that a bag of snacks fits in the picture.
            var target = rightInteractor.hasSelection ? rightInteractor.interactablesSelected[0].transform.position : hand();
            handAt = Vector3.SmoothDamp(handAt, target, ref handSpeed, 0.2f);
            // From the right; from the left once the hand is over the bag, the card terminal and the till, which would
            // stand in the way. There it also looks down from higher up.
            float left = Mathf.InverseLerp(0f, -0.3f, handAt.x);
            var offset = Vector3.Lerp(new Vector3(0.3f, 0.28f, -0.5f), new Vector3(-0.2f, 0.6f, -0.3f), left);
            handFrom = Vector3.SmoothDamp(handFrom, handAt + offset, ref fromSpeed, 0.35f);
            Aim(camera, handFrom, handAt);
        });

        // Along the counter from its two ends, level with what stands on it.
        film.Add("side_left", 42f, (camera, t) => Aim(camera, new Vector3(-0.95f, 1.22f, 0.3f), new Vector3(0.25f, 0.98f, 0.72f)));
        film.Add("side_right", 42f, (camera, t) => Aim(camera, new Vector3(1.0f, 1.25f, 0.22f), new Vector3(-0.1f, 0.98f, 0.7f)));

        // Straight down on the counter, the customer's side at the top of the picture.
        film.Add("top", 48f, (camera, t) => camera.transform.SetPositionAndRotation(new Vector3(0.12f, 2.15f, 0.6f), Quaternion.LookRotation(Vector3.down, Vector3.forward)));

        // From behind and above the clerk: the counter, the line, the store.
        film.Add("wide", 62f, (camera, t) => Aim(camera, new Vector3(-0.6f, 2.3f, -1.2f), new Vector3(0.3f, 0.9f, 1.8f)));

        // From the customers' side of the counter, past its far end: whoever walks up and stands there, with the shelves
        // of scanned products behind them. The clerk's place is out of the picture (no body goes with the hands), and so
        // are the bare chiller shelves to the right of the product shelves (take 6).
        film.Add("aisle", 38f, (camera, t) => Aim(camera, new Vector3(1.6f, 1.45f, 1.55f), new Vector3(-2.6f, 1.0f, 2.0f)));

        // The store's two screens, square on and filling the picture, for showing what the rules are (2026-11-14, the
        // film for the presentation): what this customer wants, and what the POS has rung up.
        AddScreenCamera("order", "Order HUD");
        AddScreenCamera("pos", "POS Panel");

        // The customer at the counter, from the clerk's left.
        Vector3 faceAt = new Vector3(0.2f, 1.35f, 1.5f), faceSpeed = Vector3.zero;
        film.Add("customer", 40f, (camera, t) =>
        {
            var who = director.AtCounter;
            var target = who != null ? who.transform.position + Vector3.up * 1.3f : new Vector3(0.2f, 1.35f, 1.5f);
            faceAt = Vector3.SmoothDamp(faceAt, target, ref faceSpeed, 0.6f);
            Aim(camera, new Vector3(-0.3f, 1.45f, 0.2f), faceAt);
        });

        // Along the two product shelves and back, slowly; once the day is over, the settlement board.
        film.Add("shelf", 50f, (camera, t) =>
        {
            if (Game.State == DayState.DayEnd || Game.State == DayState.Settlement)
            {
                var board = Game.BoardAnchor;
                camera.fieldOfView = 34f;
                Aim(camera, board.position - board.forward * 1.5f + Vector3.down * 0.1f, board.position + Vector3.down * 0.1f);
                return;
            }
            float along = Mathf.SmoothStep(0f, 1f, Mathf.PingPong(t / 12f, 1f));
            // Low enough to take in all four boards, now that the lower two hold products as well.
            Aim(camera, Vector3.Lerp(new Vector3(-1.9f, 1.45f, 1.45f), new Vector3(-0.5f, 1.45f, 1.45f), along),
                Vector3.Lerp(new Vector3(-3.25f, 0.85f, 3.1f), new Vector3(-1.2f, 0.85f, 3.1f), along));
        });
    }
}
