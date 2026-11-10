using System;
using System.Collections;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Comfort;
using UnityEngine.XR.Interaction.Toolkit.Samples.StarterAssets;

/// <summary>What was pressed on the title of a store that is kept between sessions.</summary>
public enum TitleChoice { Fresh, Continue, SkipTraining }

/// <summary>
/// The way into the shift and out of it (game design 10.0, 2026-11-09). The game waits at the closed store door with a
/// title on it, so nothing runs until someone has the headset on and chooses. The door slides open, the player walks to the
/// counter along floor arrows, which is where movement is learned, and punches in with a time card in the card terminal:
/// the first thing done with the hands is a grip, in the fiction of the job. GameManager runs the days between.
/// </summary>
public class TitleFlow : MonoBehaviour
{
    const float PanelMetersPerPixel = 0.0008f;
    const float PanelWidth = 860f, PanelHeight = 490f;
    const float GuideMetersPerPixel = 0.001f;
    const float DoorOpenSeconds = 1.1f;
    const float SettleSeconds = 0.5f;
    // Walking off this far from where the title is read (or putting the headset on somewhere else) brings the view back.
    const float OffSpotMeters = 1.0f;
    const float OffSpotSeconds = 1.5f;
    const float ArrivalMeters = 0.6f;
    const float WalkHintSeconds = 20f;
    const float WalkGiveUpSeconds = 75f;
    const float PunchHintSeconds = 20f;
    const float PunchGiveUpSeconds = 70f;
    const float NormalSpeed = 1.5f, SlowSpeed = 1f;
    const float NormalAperture = 0.7f, StrongAperture = 0.5f;
    const string GripWords = "가운뎃손가락 버튼을 꾹";

    public XROrigin Rig;
    public DynamicMoveProvider Move;
    public TunnelingVignetteController Vignette;
    public PaymentDesk Desk;
    public GameAudio Audio;
    public Transform StandSpot, PanelAnchor, GuideAnchor, CounterSpot;
    /// <summary>Near leaf first. They slide into the wall on either side, along <see cref="LeafSlide"/>, and start switched off.</summary>
    public Transform[] DoorLeaves = new Transform[0];
    public Vector3 LeafSlide = new Vector3(0f, 0f, 1.18f);
    public GameObject CommutePath;
    public TargetPointer Pointer;
    public ControllerHint Hands;
    public ManagerNpc Manager;

    /// <summary>The day the player chose to start from; 0 until then.</summary>
    public int ChosenDay { get; private set; }
    /// <summary>How a store that is kept between sessions is entered (see <see cref="OfferStore"/>).</summary>
    public TitleChoice Choice { get; private set; }
    public Button FreshButton { get; private set; }
    public Button SkipButton { get; private set; }
    public string StartLabel => startLabel != null ? startLabel.text : "";
    public string FreshLabel => freshLabel != null ? freshLabel.text : "";
    public Button StartButton { get; private set; }
    public Button MoreButton { get; private set; }
    public Button BackButton { get; private set; }
    public bool MoreShown => moreGroup != null && moreGroup.activeSelf;
    public Button Day2Button { get; private set; }
    public Button Day3Button { get; private set; }
    public Button SpeedButton { get; private set; }
    public Button VignetteButton { get; private set; }
    public Button RecenterButton { get; private set; }
    public bool PanelShown => panel != null && panel.gameObject.activeSelf;
    public bool DoorClosed => DoorLeaves.Length > 0 && DoorLeaves[0].gameObject.activeSelf;
    public bool GuideShown => guidePanel != null && guidePanel.gameObject.activeSelf;
    public string GuideText => guideText != null ? guideText.text : "";
    public bool SlowWalk { get; private set; }
    public bool StrongVignette { get; private set; }
    public bool WalkGaveUp { get; private set; }
    public bool PunchGaveUp { get; private set; }

    RectTransform panel, guidePanel;
    GameObject mainGroup, moreGroup;
    Text guideText, speedLabel, vignetteLabel;
    Text subtitle, hint, startLabel, freshLabel, skipLabel;
    Vector3[] leafClosed = new Vector3[0];
    // A store kept between sessions: 0 while the title is the plain three days, else the first day after the training.
    int afterTraining;
    int continueDay;
    bool freshArmed, skipArmed;

    void Awake()
    {
        leafClosed = new Vector3[DoorLeaves.Length];
        for (int i = 0; i < DoorLeaves.Length; i++) leafClosed[i] = DoorLeaves[i].position;
        BuildPanel();
        guidePanel = WorldUi.CreatePanel(transform, "Commute Guide", GuideAnchor.position, GuideAnchor.rotation, new Vector2(860f, 170f), GuideMetersPerPixel, WorldUi.Palette.Panel);
        guideText = WorldUi.AddText(guidePanel, 24f, 12f, 812f, 146f, 44, TextAnchor.MiddleCenter);
        guidePanel.gameObject.SetActive(false);
    }

    /// <summary>
    /// One thing to do: go to work. The first look is through the glass at a lit store with people in it, so the sign is kept
    /// small and low, and everything a first-timer does not need yet waits behind 설정.
    /// </summary>
    void BuildPanel()
    {
        panel = WorldUi.CreatePanel(transform, "Title Panel", PanelAnchor.position, PanelAnchor.rotation, new Vector2(PanelWidth, PanelHeight), PanelMetersPerPixel, WorldUi.Palette.Navy, interactive: true);
        mainGroup = Group("Main");
        var main = (RectTransform)mainGroup.transform;
        var title = WorldUi.AddText(main, 0f, 18f, PanelWidth, 110f, 96, TextAnchor.MiddleCenter);
        WorldUi.Bold(title);
        title.text = "동아편의점";
        subtitle = WorldUi.AddText(main, 0f, 132f, PanelWidth, 56f, 42, TextAnchor.MiddleCenter);
        subtitle.color = WorldUi.Palette.TextDim;
        subtitle.text = "오늘은 첫 출근 날입니다";
        StartButton = Button(main, 150f, 204f, 560f, 120f, "출근하기", 66, true);
        startLabel = StartButton.GetComponentInChildren<Text>();
        hint = WorldUi.AddText(main, 0f, 338f, PanelWidth, 50f, 32, TextAnchor.MiddleCenter);
        hint.color = WorldUi.Palette.TextDim;
        hint.text = "컨트롤러로 가리키고 검지 버튼을 누르세요";
        // In the hint's place once there is a store to go back to: someone who has one knows how to press.
        FreshButton = Button(main, 200f, 334f, 460f, 58f, "새로 시작", 32, false);
        freshLabel = FreshButton.GetComponentInChildren<Text>();
        FreshButton.gameObject.SetActive(false);
        MoreButton = Button(main, 340f, 404f, 180f, 62f, "설정", 34, false);

        moreGroup = Group("More");
        var more = (RectTransform)moreGroup.transform;
        var moreTitle = WorldUi.AddText(more, 0f, 14f, PanelWidth, 70f, 54, TextAnchor.MiddleCenter);
        WorldUi.Bold(moreTitle);
        moreTitle.text = "설정";
        Day2Button = Button(more, 50f, 96f, 370f, 72f, "2일차부터", 38, false);
        Day3Button = Button(more, 440f, 96f, 370f, 72f, "3일차부터", 38, false);
        // In their place when the store is kept between sessions: the training can be skipped, not entered half way.
        SkipButton = Button(more, 50f, 96f, 760f, 72f, "수습 건너뛰기", 38, false);
        skipLabel = SkipButton.GetComponentInChildren<Text>();
        SkipButton.gameObject.SetActive(false);
        SpeedButton = Button(more, 50f, 182f, 370f, 72f, "", 32, false);
        VignetteButton = Button(more, 440f, 182f, 370f, 72f, "", 32, false);
        RecenterButton = Button(more, 50f, 268f, 760f, 66f, "시점 다시 맞추기", 34, false);
        BackButton = Button(more, 250f, 372f, 360f, 84f, "돌아가기", 44, true);
        moreGroup.SetActive(false);
        speedLabel = SpeedButton.GetComponentInChildren<Text>();
        vignetteLabel = VignetteButton.GetComponentInChildren<Text>();

        StartButton.onClick.AddListener(() =>
        {
            if (continueDay > 0) Choose(continueDay, TitleChoice.Continue);
            else Choose(1, TitleChoice.Fresh);
        });
        FreshButton.onClick.AddListener(() =>
        {
            // Starting over throws the store away, so it takes a second press.
            if (freshArmed)
            {
                Choose(1, TitleChoice.Fresh);
                return;
            }
            freshArmed = true;
            freshLabel.text = "기록을 지우고 새로 시작";
        });
        SkipButton.onClick.AddListener(() =>
        {
            if (continueDay == 0 || skipArmed)
            {
                Choose(afterTraining, TitleChoice.SkipTraining);
                return;
            }
            skipArmed = true;
            skipLabel.text = "기록을 지우고 수습 건너뛰기";
        });
        MoreButton.onClick.AddListener(() => ShowMore(true));
        BackButton.onClick.AddListener(() => ShowMore(false));
        Day2Button.onClick.AddListener(() => Choose(2));
        Day3Button.onClick.AddListener(() => Choose(3));
        SpeedButton.onClick.AddListener(() => SetComfort(!SlowWalk, StrongVignette));
        VignetteButton.onClick.AddListener(() => SetComfort(SlowWalk, !StrongVignette));
        RecenterButton.onClick.AddListener(() => PlacePlayer(StandSpot));
        SetComfort(false, false);
        panel.gameObject.SetActive(false);
    }

    GameObject Group(string groupName)
    {
        var go = new GameObject(groupName, typeof(RectTransform));
        go.transform.SetParent(panel, false);
        var rect = (RectTransform)go.transform;
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
        rect.anchoredPosition = Vector2.zero;
        rect.sizeDelta = new Vector2(PanelWidth, PanelHeight);
        return go;
    }

    void ShowMore(bool show)
    {
        moreGroup.SetActive(show);
        mainGroup.SetActive(!show);
    }

    Button Button(RectTransform group, float x, float y, float width, float height, string label, int fontSize, bool primary)
    {
        var button = WorldUi.AddButton(group, x, y, width, height, label, fontSize);
        if (!primary) button.GetComponent<Image>().color = new Color(0.17f, 0.29f, 0.47f);
        // Tints multiply the color, so the resting state sits below full: pointing at a button has to be seen on Quest 2.
        var colors = button.colors;
        colors.normalColor = new Color(0.82f, 0.82f, 0.82f);
        colors.highlightedColor = Color.white;
        colors.selectedColor = new Color(0.82f, 0.82f, 0.82f);
        colors.pressedColor = new Color(0.6f, 0.6f, 0.6f);
        button.colors = colors;
        return button;
    }

    void Choose(int day, TitleChoice choice = TitleChoice.Fresh)
    {
        if (ChosenDay != 0) return;
        ChosenDay = day;
        Choice = choice;
    }

    /// <summary>
    /// The title for a store that is kept between sessions (docs/p2/해금_성장_기획_2026-11-10.md 3). With a store to go
    /// back to, the big button goes on with it and 새로 시작 sits under it; the settings offer skipping the training in
    /// place of starting at Day 2 or 3. Call before <see cref="ChooseDay"/>.
    /// </summary>
    /// <param name="savedDay">The day the saved store is at, or 0 when there is none.</param>
    /// <param name="firstDayAfterTraining">Where skipping the training starts.</param>
    public void OfferStore(int savedDay, int firstDayAfterTraining)
    {
        afterTraining = firstDayAfterTraining;
        continueDay = savedDay;
        freshArmed = skipArmed = false;
        bool saved = savedDay > 0;
        subtitle.text = saved ? $"근무 {savedDay}일차" : "오늘은 첫 출근 날입니다";
        startLabel.text = saved ? "이어서 출근" : "출근하기";
        freshLabel.text = "새로 시작";
        skipLabel.text = "수습 건너뛰기";
        hint.gameObject.SetActive(!saved);
        FreshButton.gameObject.SetActive(saved);
        Day2Button.gameObject.SetActive(false);
        Day3Button.gameObject.SetActive(false);
        SkipButton.gameObject.SetActive(true);
    }

    /// <summary>Game design 16: for anyone feeling queasy, a slower walk and a stronger vignette. They stay for the next run.</summary>
    void SetComfort(bool slow, bool strong)
    {
        SlowWalk = slow;
        StrongVignette = strong;
        speedLabel.text = slow ? "이동 속도: 느리게" : "이동 속도: 보통";
        vignetteLabel.text = strong ? "화면 가장자리: 진하게" : "화면 가장자리: 보통";
        if (Move != null) Move.moveSpeed = slow ? SlowSpeed : NormalSpeed;
        if (Vignette == null) return;
        float aperture = strong ? StrongAperture : NormalAperture;
        Vignette.defaultParameters.apertureSize = aperture;
        foreach (var provider in Vignette.locomotionVignetteProviders)
            if (provider.overrideDefaultParameters) provider.overrideParameters.apertureSize = aperture;
    }

    /// <summary>Stands the player at the closed door with the title in front and waits for a choice.</summary>
    public IEnumerator ChooseDay()
    {
        ChosenDay = 0;
        SetDoor(true);
        if (CommutePath != null) CommutePath.SetActive(false);
        ShowMore(false);
        panel.gameObject.SetActive(true);
        yield return new WaitForSeconds(SettleSeconds);
        PlacePlayer(StandSpot);

        float offSpotSince = -1f;
        while (ChosenDay == 0)
        {
            // Link starts tracking wherever the headset happens to be; the title must still end up in front of the wearer.
            if (Distance(StandSpot) <= OffSpotMeters) offSpotSince = -1f;
            else if (offSpotSince < 0f) offSpotSince = Time.time;
            else if (Time.time - offSpotSince > OffSpotSeconds)
            {
                PlacePlayer(StandSpot);
                offSpotSince = -1f;
            }
            yield return null;
        }
        panel.gameObject.SetActive(false);
    }

    /// <summary>The door opens, and the player walks to the place behind the counter. Nobody is left standing at the door.</summary>
    public IEnumerator WalkIn()
    {
        WalkGaveUp = false;
        if (Manager != null) Manager.Greet();
        if (Audio)
        {
            Audio.PlayAt(StoreSound.Door, StandSpot.position + Vector3.up * 2f);
            Audio.NarrateManager(ManagerLine.Welcome);
        }
        yield return SlideDoorOpen();
        if (CommutePath != null) CommutePath.SetActive(true);
        // Said from the first moment: someone who has never used these sticks would otherwise just stand there.
        guideText.text = "왼손 스틱을 앞으로 밀어\n노란 화살표를 따라가세요";
        guidePanel.gameObject.SetActive(true);
        if (Hands != null) Hands.Show("이 스틱을 앞으로 밀기", null);

        float begun = Time.time;
        bool hinted = false;
        while (Distance(CounterSpot) > ArrivalMeters)
        {
            float waited = Time.time - begun;
            if (!hinted && waited > WalkHintSeconds)
            {
                hinted = true;
                guideText.text = "왼손 스틱: 걷기\n오른손 스틱을 좌우로: 방향 돌리기";
                if (Hands != null) Hands.Show("이 스틱을 앞으로 밀기", "좌우로 밀면 몸이 돌아요");
            }
            if (waited > WalkGiveUpSeconds)
            {
                WalkGaveUp = true;
                PlacePlayer(CounterSpot);
                break;
            }
            yield return null;
        }
        if (Hands != null) Hands.Hide();
        if (CommutePath != null) CommutePath.SetActive(false);
        guidePanel.gameObject.SetActive(false);
    }

    /// <summary>
    /// A time card on the plate, into the terminal like a customer's card. It is the first grip of the game, so someone who
    /// cannot manage it gets a plainer hint and then is let through (game design 3, "막히지 않게").
    /// </summary>
    public IEnumerator Punch(string cardName, ManagerLine line, Action<string> say)
    {
        PunchGaveUp = false;
        bool punched = false;
        if (Audio) Audio.NarrateManager(line);
        string prompt = $"{cardName}를 집어\n단말기 노란 칸에 꽂으세요";
        say(prompt);
        Desk.BeginCard(() => true, notice => say(prompt + "\n" + notice), () => punched = true, cardName);
        if (Pointer != null) Pointer.Aim = () => TargetPointer.AimAtCard(Desk);
        if (Hands != null) Hands.Show(GripWords, GripWords);

        float begun = Time.time;
        bool hinted = false;
        while (!punched)
        {
            float waited = Time.time - begun;
            if (Hands != null && Desk.CustomerCard != null && Desk.CustomerCard.Grab.isSelected) Hands.Hide();
            if (!hinted && waited > PunchHintSeconds)
            {
                hinted = true;
                say($"{cardName}에 손을 대고 가운뎃손가락 버튼을 누른 채\n오른쪽 단말기의 노란 칸으로 밀어 넣기");
            }
            if (waited > PunchGiveUpSeconds)
            {
                PunchGaveUp = true;
                break;
            }
            yield return null;
        }
        if (Pointer != null) Pointer.Aim = null;
        if (Hands != null) Hands.Hide();
        if (Audio) Audio.PlayApproved();
        if (Manager != null) Manager.Nod();
        Desk.EndPayment();
    }

    /// <summary>Turns about the head and slides, so the head ends over the spot looking along its forward. The floor height stays.</summary>
    public void PlacePlayer(Transform spot)
    {
        var head = Rig.Camera.transform;
        var look = Vector3.ProjectOnPlane(head.forward, Vector3.up);
        if (look.sqrMagnitude < 1e-4f) look = Rig.transform.forward;
        Rig.transform.RotateAround(head.position, Vector3.up, Vector3.SignedAngle(look, spot.forward, Vector3.up));
        var offset = spot.position - head.position;
        offset.y = 0f;
        Rig.transform.position += offset;
        Physics.SyncTransforms();
    }

    float Distance(Transform spot)
    {
        var offset = Rig.Camera.transform.position - spot.position;
        offset.y = 0f;
        return offset.magnitude;
    }

    void SetDoor(bool closed)
    {
        for (int i = 0; i < DoorLeaves.Length; i++)
        {
            DoorLeaves[i].position = leafClosed[i];
            DoorLeaves[i].gameObject.SetActive(closed);
        }
        Physics.SyncTransforms();
    }

    IEnumerator SlideDoorOpen()
    {
        float elapsed = 0f;
        while (elapsed < DoorOpenSeconds)
        {
            elapsed += Time.deltaTime;
            float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(elapsed / DoorOpenSeconds));
            for (int i = 0; i < DoorLeaves.Length; i++)
                DoorLeaves[i].position = leafClosed[i] + (i == 0 ? -LeafSlide : LeafSlide) * k;
            yield return null;
        }
        SetDoor(false);
    }
}
