using System;
using System.Collections;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Comfort;
using UnityEngine.XR.Interaction.Toolkit.Samples.StarterAssets;

/// <summary>
/// The way into the shift and out of it (game design 10.0, 2026-11-09). The game waits at the closed store door with a
/// title on it, so nothing runs until someone has the headset on and chooses. The door slides open, the player walks to the
/// counter along floor arrows, which is where movement is learned, and punches in with a time card in the card terminal:
/// the first thing done with the hands is a grip, in the fiction of the job. GameManager runs the days between.
/// </summary>
public class TitleFlow : MonoBehaviour
{
    const float PanelMetersPerPixel = 0.0008f;
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

    /// <summary>The day the player chose to start from; 0 until then.</summary>
    public int ChosenDay { get; private set; }
    public Button StartButton { get; private set; }
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
    Text guideText, speedLabel, vignetteLabel;
    Vector3[] leafClosed = new Vector3[0];

    void Awake()
    {
        leafClosed = new Vector3[DoorLeaves.Length];
        for (int i = 0; i < DoorLeaves.Length; i++) leafClosed[i] = DoorLeaves[i].position;
        BuildPanel();
        guidePanel = WorldUi.CreatePanel(transform, "Commute Guide", GuideAnchor.position, GuideAnchor.rotation, new Vector2(860f, 170f), GuideMetersPerPixel, WorldUi.Palette.Panel);
        guideText = WorldUi.AddText(guidePanel, 24f, 12f, 812f, 146f, 44, TextAnchor.MiddleCenter);
        guidePanel.gameObject.SetActive(false);
    }

    void BuildPanel()
    {
        panel = WorldUi.CreatePanel(transform, "Title Panel", PanelAnchor.position, PanelAnchor.rotation, new Vector2(1100f, 720f), PanelMetersPerPixel, WorldUi.Palette.Navy, interactive: true);
        var title = WorldUi.AddText(panel, 0f, 26f, 1100f, 120f, 104, TextAnchor.MiddleCenter);
        title.fontStyle = FontStyle.Bold;
        title.text = "동아편의점";
        var subtitle = WorldUi.AddText(panel, 0f, 150f, 1100f, 50f, 40, TextAnchor.MiddleCenter);
        subtitle.color = WorldUi.Palette.TextDim;
        subtitle.text = "3일 알바 체험";

        StartButton = Button(250f, 215f, 600f, 104f, "출근하기", 58, true);
        Day2Button = Button(250f, 335f, 290f, 76f, "2일차부터", 38, false);
        Day3Button = Button(560f, 335f, 290f, 76f, "3일차부터", 38, false);
        SpeedButton = Button(90f, 462f, 450f, 72f, "", 34, false);
        VignetteButton = Button(560f, 462f, 450f, 72f, "", 34, false);
        RecenterButton = Button(325f, 548f, 450f, 66f, "시점 다시 맞추기", 34, false);
        speedLabel = SpeedButton.GetComponentInChildren<Text>();
        vignetteLabel = VignetteButton.GetComponentInChildren<Text>();
        var hint = WorldUi.AddText(panel, 0f, 636f, 1100f, 50f, 32, TextAnchor.MiddleCenter);
        hint.color = WorldUi.Palette.TextDim;
        hint.text = "컨트롤러로 가리키고 검지 버튼을 누르세요";

        StartButton.onClick.AddListener(() => Choose(1));
        Day2Button.onClick.AddListener(() => Choose(2));
        Day3Button.onClick.AddListener(() => Choose(3));
        SpeedButton.onClick.AddListener(() => SetComfort(!SlowWalk, StrongVignette));
        VignetteButton.onClick.AddListener(() => SetComfort(SlowWalk, !StrongVignette));
        RecenterButton.onClick.AddListener(() => PlacePlayer(StandSpot));
        SetComfort(false, false);
        panel.gameObject.SetActive(false);
    }

    Button Button(float x, float y, float width, float height, string label, int fontSize, bool primary)
    {
        var button = WorldUi.AddButton(panel, x, y, width, height, label, fontSize);
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

    void Choose(int day)
    {
        if (ChosenDay == 0) ChosenDay = day;
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
        if (Audio)
        {
            Audio.PlayAt(StoreSound.Door, StandSpot.position + Vector3.up * 2f);
            Audio.NarrateManager(ManagerLine.Welcome);
        }
        yield return SlideDoorOpen();
        if (CommutePath != null) CommutePath.SetActive(true);
        guideText.text = "노란 화살표를 따라 계산대 안쪽으로";
        guidePanel.gameObject.SetActive(true);

        float begun = Time.time;
        bool hinted = false;
        while (Distance(CounterSpot) > ArrivalMeters)
        {
            float waited = Time.time - begun;
            if (!hinted && waited > WalkHintSeconds)
            {
                hinted = true;
                guideText.text = "왼손 썸스틱을 밀면 걸어갑니다\n오른손 썸스틱 좌우로 방향 전환";
            }
            if (waited > WalkGiveUpSeconds)
            {
                WalkGaveUp = true;
                PlacePlayer(CounterSpot);
                break;
            }
            yield return null;
        }
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

        float begun = Time.time;
        bool hinted = false;
        while (!punched)
        {
            float waited = Time.time - begun;
            if (!hinted && waited > PunchHintSeconds)
            {
                hinted = true;
                say($"{cardName}에 손을 대고 그립 버튼을 누른 채\n오른쪽 단말기의 노란 칸으로 밀어 넣기");
            }
            if (waited > PunchGiveUpSeconds)
            {
                PunchGaveUp = true;
                break;
            }
            yield return null;
        }
        if (Audio) Audio.PlayApproved();
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
