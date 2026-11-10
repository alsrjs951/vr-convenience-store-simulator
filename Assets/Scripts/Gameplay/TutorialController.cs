using System.Collections;
using UnityEngine;
using UnityEngine.UI;

public enum TutorialStep { NotStarted, Grab, Rotate, Scan, Bag, Card, Cash, Done }

/// <summary>
/// The Day 1 tutorial with its failure recovery (doc 5.6), done on real people (2026-11-09). The first customer walks up and
/// is served all the way through the card, and gets to say thank you and leave before anything more is taught. A second one
/// then pays in cash. The manager says what to do from beside the counter, the board says it in words, and an arrow shows
/// where.
/// </summary>
public class TutorialController : MonoBehaviour
{
    const float FacingAngle = 60f;
    const float FacingHoldSeconds = 1f;
    const float HintAfterSeconds = 15f;
    const float ScanHintAfterSeconds = 20f;
    const int CashBill = 5000;
    const int CashPrice = 1500;
    const int HintAfterErrors = 2;
    const int AutoCompleteAfterErrors = 3;
    const float ItemClearance = 0.004f;
    const float LinePauseSeconds = 0.3f;
    const float AfterBarcodeMeters = 0.035f;
    // The arrow's tip stops this far short of the scanner's upright glass.
    const float BeforeGlassMeters = 0.03f;
    const float DoneSeconds = 4f;
    const string GripWords = "가운뎃손가락 버튼을 꾹";

    public ProductCatalog Catalog;
    public string ProductId = "dev_potato_01";
    public ScannerZone Scanner;
    public BagZone Bag;
    public PaymentDesk Desk;
    public Transform BasketAnchor;
    public Text Board;
    public CustomerDirector Director;
    public TargetPointer Pointer;
    public ControllerHint Hands;
    public ManagerNpc Manager;

    public TutorialStep Step { get; private set; } = TutorialStep.NotStarted;
    public ProductPrefab Product { get; private set; }
    /// <summary>The customer at the counter right now.</summary>
    public CustomerCharacter Guest { get; private set; }

    bool productScanned;
    Coroutine pendingLine;

    GameAudio Audio => Desk != null ? Desk.Audio : null;

    public IEnumerator Run()
    {
        StationLabel.ShowAll(true);
        Board.text = "첫 손님이 옵니다";
        if (Audio) Audio.NarrateManager(ManagerLine.FirstCustomer);

        Guest = Director.CallGuest();
        yield return Director.BringGuest(Guest);
        Director.GuestPutsBasketDown(Guest);
        var prefab = Catalog.Find(ProductId);
        var position = BasketAnchor.position + Vector3.up * (prefab.Proxy.size.y * 0.5f + ItemClearance);
        Product = Instantiate(prefab, position, Quaternion.Euler(0f, 180f, 0f));
        int price = Product.Data.Price;
        Scanner.Scanned += OnScanned;

        float greeting = Director.GuestSays(Guest, CustomerLine.Greet);
        yield return new WaitForSeconds(greeting + LinePauseSeconds);
        yield return RunGrab();
        yield return RunRotate();
        yield return RunScan();
        Scanner.Scanned -= OnScanned;
        if (Manager) Manager.Nod();
        yield return RunBag();
        Destroy(Product.gameObject);
        yield return RunCard();
        Director.GuestLeaves(Guest, price);
        if (Manager) Manager.Nod();

        Pointer.Aim = null;
        Board.text = "다음 손님이 옵니다";
        Guest = Director.CallGuest();
        yield return Director.BringGuest(Guest);
        greeting = Director.GuestSays(Guest, CustomerLine.Greet, 1);
        yield return new WaitForSeconds(greeting + LinePauseSeconds);
        yield return RunCash();
        Director.GuestLeaves(Guest, CashPrice);
        Guest = null;

        Pointer.Aim = null;
        Step = TutorialStep.Done;
        Board.text = "튜토리얼 완료\n이제 혼자 해 볼 차례입니다";
        if (Audio) Audio.NarrateManager(ManagerLine.Alone);
        if (Manager) Manager.Nod();
        yield return new WaitForSeconds(DoneSeconds);
    }

    void OnDestroy()
    {
        if (Scanner) Scanner.Scanned -= OnScanned;
    }

    void OnScanned(ProductPrefab product) => productScanned |= product == Product;

    void Narrate(int step)
    {
        if (pendingLine != null) StopCoroutine(pendingLine);
        pendingLine = null;
        if (Audio) Audio.NarrateTutorial(step);
    }

    /// <summary>What the customer asks for first, then the manager saying how, unless the player has already moved on.</summary>
    IEnumerator GuestThenNarrate(CustomerLine line, int step)
    {
        float talking = Director.GuestSays(Guest, line);
        yield return new WaitForSeconds(talking + LinePauseSeconds);
        pendingLine = null;
        if (Audio) Audio.NarrateTutorial(step);
    }

    IEnumerator RunGrab()
    {
        Step = TutorialStep.Grab;
        Narrate(1);
        Board.text = $"1/6  상품 잡기\n손을 대고 {GripWords}";
        Pointer.Aim = () => PointerAim.Down(TopOf(Product), "집기");
        Hands.Show(GripWords, GripWords);
        float begun = Time.time;
        while (!Product.Grab.isSelected)
        {
            if (Time.time - begun > HintAfterSeconds)
                Board.text = "1/6  상품 잡기\n컨트롤러 옆면의 버튼을 누른 채로 들어 올리기";
            yield return null;
        }
        Hands.Hide();
    }

    IEnumerator RunRotate()
    {
        Step = TutorialStep.Rotate;
        Narrate(2);
        Board.text = "2/6  바코드 찾기\n손목을 돌려 바코드 면을 스캐너 쪽으로";
        Pointer.Aim = AimAtBarcode;
        float begun = Time.time;
        float held = 0f;
        // A quick player may turn and scan it before this step has seen a full second.
        while (held < FacingHoldSeconds && !productScanned)
        {
            var normal = Product.transform.TransformDirection(Product.Data.BarcodeLocalNormal);
            // Toward either glass: the upright one the arrow shows next, or the flat one under it.
            bool facing = Vector3.Angle(normal, Scanner.Shown.position - Product.transform.position) <= FacingAngle
                || Vector3.Angle(normal, Scanner.transform.position - Product.transform.position) <= FacingAngle;
            held = Product.Grab.isSelected && facing ? held + Time.deltaTime : 0f;
            if (Time.time - begun > HintAfterSeconds)
                Board.text = "2/6  바코드 찾기\n화살표가 가리키는 면이 바코드입니다";
            yield return null;
        }
    }

    IEnumerator RunScan()
    {
        Step = TutorialStep.Scan;
        Narrate(3);
        Board.text = "3/6  스캔\n바코드 면을 붉은 빛이 나오는 유리 앞에";
        Pointer.Aim = AimAtScanner;
        float begun = Time.time;
        while (!productScanned)
        {
            if (Time.time - begun > ScanHintAfterSeconds)
                Board.text = "3/6  스캔\n닿지 않아도 됩니다. 바코드가 유리를 보게, 한 뼘 앞에서";
            yield return null;
        }
    }

    IEnumerator RunBag()
    {
        Step = TutorialStep.Bag;
        if (pendingLine != null) StopCoroutine(pendingLine);
        pendingLine = StartCoroutine(GuestThenNarrate(CustomerLine.Bag, 4));
        Board.text = "4/6  봉투에 담기\n봉투 위로 옮겨서 버튼 놓기";
        Pointer.Aim = () => PointerAim.Down(Bag.transform.position, "여기에 넣기");
        bool bagged = false;
        void OnBagged(ProductPrefab product) => bagged |= product == Product;
        Bag.AcceptOverride = product => product == Product;
        Bag.Bagged += OnBagged;
        while (!bagged) yield return null;
        Bag.Bagged -= OnBagged;
        Bag.AcceptOverride = null;
    }

    IEnumerator RunCard()
    {
        Step = TutorialStep.Card;
        Narrate(5);
        Board.text = "5/6  카드 결제\n손님 카드를 단말기 노란 칸에";
        bool paid = false;
        Desk.BeginCard(() => true, notice => Board.text = "5/6  카드 결제\n" + notice, () => paid = true);
        Pointer.Aim = () => TargetPointer.AimAtCard(Desk);
        while (!paid) yield return null;
        if (Desk.Audio) Desk.Audio.PlayApproved();
        Desk.EndPayment();
    }

    IEnumerator RunCash()
    {
        Step = TutorialStep.Cash;
        Narrate(6);
        int change = CashBill - CashPrice;
        Board.text = $"6/6  현금 결제\n{CashBill:N0}원을 서랍에 (가격 {CashPrice:N0}원)";
        bool paid = false;
        int errorsShown = 0;
        Desk.BeginCash(CashBill, () => true, () => CashPrice,
            _ => Board.text = $"6/6  현금 결제\n거스름 {change:N0}원을 접시에",
            notice => Board.text = "6/6  현금 결제\n" + notice,
            () => paid = true);
        Pointer.Aim = AimForCash;
        while (!paid)
        {
            int errors = Desk.Tray.ErrorCount;
            if (errors != errorsShown)
            {
                errorsShown = errors;
                if (errors >= AutoCompleteAfterErrors) paid = true;
                else if (errors >= HintAfterErrors) Board.text = $"6/6  현금 결제\n1,000원 3장 + 500원 1개 (지금 {Desk.Tray.Amount:N0}원)";
            }
            yield return null;
        }
        Desk.EndPayment();
    }

    static Vector3 TopOf(ProductPrefab product) =>
        product.transform.TransformPoint(product.Proxy.center) + Vector3.up * (product.Proxy.size.y * 0.5f);

    /// <summary>
    /// An arrow at the glass the scanner's light comes out of. At the upright one it comes down at a slant from the clerk's
    /// side: pointing straight into the glass, it would be seen end-on as a dot.
    /// </summary>
    PointerAim? AimAtScanner()
    {
        var glass = Scanner.Shown;
        return glass == Scanner.transform
            ? PointerAim.Down(glass.position, "여기에 대기")
            : PointerAim.Along(glass.position + glass.up * BeforeGlassMeters, Vector3.down - glass.up, "여기에 대기");
    }

    /// <summary>An arrow at the barcode face of the product in the hand, pointing at it, whichever way the product is held.</summary>
    PointerAim? AimAtBarcode()
    {
        if (Product == null) return null;
        var local = Product.Data.BarcodeLocalNormal.normalized;
        var size = Product.Proxy.size;
        float halfDepth = 0.5f * (Mathf.Abs(local.x) * size.x + Mathf.Abs(local.y) * size.y + Mathf.Abs(local.z) * size.z);
        var normal = Product.transform.TransformDirection(local);
        var center = Product.transform.TransformPoint(Product.Proxy.center);
        return PointerAim.Along(center + normal * (halfDepth + AfterBarcodeMeters), -normal, "바코드");
    }

    /// <summary>The customer's bill into the drawer, then change from the drawer onto the tray.</summary>
    PointerAim? AimForCash()
    {
        var bill = Desk.CustomerBill;
        if (bill != null)
            return bill.Grab.isSelected ? PointerAim.Down(Desk.Drawer.transform.position, "서랍에 넣기") : PointerAim.Down(bill.transform.position, "집기");
        foreach (var item in Desk.StockItems.Values)
            if (item != null && item.Grab.isSelected) return PointerAim.Down(Desk.Tray.transform.position, "접시에 놓기");
        return PointerAim.Down(Desk.Drawer.transform.position, "거스름돈 집기");
    }
}
