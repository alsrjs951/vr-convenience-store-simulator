using UnityEngine;

/// <summary>
/// One more glass of a <see cref="ScannerZone"/>: the trigger volume in front of it, with this object's up axis pointing
/// out of the glass. A barcode in the volume that faces the glass is read, without touching anything.
/// </summary>
[RequireComponent(typeof(Collider))]
public class ScanWindow : MonoBehaviour
{
    public ScannerZone Zone;

    void OnTriggerEnter(Collider other) => Zone.Entered(other, transform);
    void OnTriggerStay(Collider other) => Zone.Stayed(other, transform);
    void OnTriggerExit(Collider other) => Zone.Left(other, transform);
}
