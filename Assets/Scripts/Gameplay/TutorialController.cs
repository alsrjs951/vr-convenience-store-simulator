using System.Collections;
using UnityEngine;
using UnityEngine.UI;

public enum TutorialStep { NotStarted, Grab, Rotate, Scan, Bag, Card, Cash, Done }

/// <summary>The six-step Day 1 chain with its failure recovery (doc 5.6). Guidance is text on the board plus narration T1 to T6.</summary>
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

    public ProductCatalog Catalog;
    public string ProductId = "dev_potato_01";
    public ScannerZone Scanner;
    public BagZone Bag;
    public PaymentDesk Desk;
    public Transform BasketAnchor;
    public Text Board;

    public TutorialStep Step { get; private set; } = TutorialStep.NotStarted;
    public ProductPrefab Product { get; private set; }

    bool productScanned;

    public IEnumerator Run()
    {
        StationLabel.ShowAll(true);
        var prefab = Catalog.Find(ProductId);
        var position = BasketAnchor.position + Vector3.up * (prefab.Proxy.size.y * 0.5f + ItemClearance);
        Product = Instantiate(prefab, position, Quaternion.Euler(0f, 180f, 0f));
        Scanner.Scanned += OnScanned;

        yield return RunGrab();
        yield return RunRotate();
        yield return RunScan();
        Scanner.Scanned -= OnScanned;
        yield return RunBag();
        Destroy(Product.gameObject);
        yield return RunCard();
        yield return RunCash();

        Step = TutorialStep.Done;
        Board.text = "튜토리얼 완료\n이제 손님이 들어옵니다";
    }

    void OnDestroy()
    {
        if (Scanner) Scanner.Scanned -= OnScanned;
    }

    void OnScanned(ProductPrefab product) => productScanned |= product == Product;

    void Narrate(int step)
    {
        if (Desk != null && Desk.Audio != null) Desk.Audio.NarrateTutorial(step);
    }

    IEnumerator RunGrab()
    {
        Step = TutorialStep.Grab;
        Narrate(1);
        Board.text = "1/6  상품 잡기\n손을 대고 그립";
        float begun = Time.time;
        while (!Product.Grab.isSelected)
        {
            if (Time.time - begun > HintAfterSeconds)
                Board.text = "1/6  상품 잡기\n중지 버튼(그립)을 누른 채로";
            yield return null;
        }
    }

    IEnumerator RunRotate()
    {
        Step = TutorialStep.Rotate;
        Narrate(2);
        Board.text = "2/6  바코드 찾기\n바코드 면을 스캐너 쪽으로";
        float begun = Time.time;
        float held = 0f;
        // A quick player may turn and press it on the pad before this step has seen a full second.
        while (held < FacingHoldSeconds && !productScanned)
        {
            var normal = Product.transform.TransformDirection(Product.Data.BarcodeLocalNormal);
            var toScanner = Scanner.transform.position - Product.transform.position;
            held = Product.Grab.isSelected && Vector3.Angle(normal, toScanner) <= FacingAngle ? held + Time.deltaTime : 0f;
            if (Time.time - begun > HintAfterSeconds)
                Board.text = "2/6  바코드 찾기\n바코드는 뒷면에 있어요";
            yield return null;
        }
    }

    IEnumerator RunScan()
    {
        Step = TutorialStep.Scan;
        Narrate(3);
        Board.text = "3/6  스캔\n바코드 면을 스캐너 유리에";
        float begun = Time.time;
        while (!productScanned)
        {
            if (Time.time - begun > ScanHintAfterSeconds)
                Board.text = "3/6  스캔\n바코드가 아래로, 내려놓듯이";
            yield return null;
        }
    }

    IEnumerator RunBag()
    {
        Step = TutorialStep.Bag;
        Narrate(4);
        Board.text = "4/6  봉투에 담기";
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
        Board.text = "5/6  카드 결제\n카드를 단말기 노란 슬롯에";
        bool paid = false;
        Desk.BeginCard(() => true, notice => Board.text = "5/6  카드 결제\n" + notice, () => paid = true);
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
}
