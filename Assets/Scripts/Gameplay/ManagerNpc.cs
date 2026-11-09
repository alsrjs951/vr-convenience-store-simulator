using System.Collections;
using UnityEngine;

/// <summary>
/// The store manager in person (2026-11-09). Until now she was only a voice, so the first person a player met came minutes
/// in. She stands at the far end of the counter from before the door opens, waves, looks at the player and says the
/// instructions with her mouth moving, nods when something gets done, and goes off to the bank when the player is on their
/// own. One of the customer bodies with a staff apron on: the body clips, the face and the walking are the customers'.
/// </summary>
public class ManagerNpc : MonoBehaviour
{
    const float LeaveWithinMeters = 0.6f;
    const float SheetMargin = 0.012f;

    public CustomerCharacter Prefab;
    public Transform Spot;
    public CustomerDirector Director;
    public GameAudio Audio;
    public Material ApronMaterial;

    public CustomerCharacter Body { get; private set; }
    /// <summary>Standing there, not on her way out.</summary>
    public bool InStore => Body != null && !leaving;
    public int Waves { get; private set; }

    bool leaving;
    bool listening;

    public void Arrive()
    {
        Clear();
        if (Prefab == null || Spot == null) return;
        Director.ReservedAvatar = Prefab.name;
        Body = Instantiate(Prefab, Spot.position, Spot.rotation);
        Body.name = "Store Manager";
        // AtCounter is the customers' state for "with the clerk": head and eyes on the player, now and then down at the counter.
        Body.State = CustomerState.AtCounter;
        Body.Resting = CustomerCharacter.IdleState;
        Body.Settle();
        Dress();
        if (Audio == null || Body.Animator == null || !Body.Animator.isHuman) return;
        Audio.NarrationSource = Body.Animator.GetBoneTransform(HumanBodyBones.Head);
        Audio.NarrationStarted += OnNarration;
        listening = true;
    }

    /// <summary>A wave and a smile as the door opens.</summary>
    public void Greet()
    {
        if (!InStore) return;
        Waves++;
        if (Body.FaceRig != null) Body.FaceRig.SetMood(CustomerMood.Pleased);
        StartCoroutine(Body.PlayLeave());
    }

    public void Nod()
    {
        if (InStore && Body.FaceRig != null) Body.FaceRig.Nod();
    }

    /// <summary>Out by the front door. Does nothing when she is already gone or going.</summary>
    public void Leave()
    {
        if (!InStore) return;
        leaving = true;
        StopListening();
        Body.State = CustomerState.Leaving;
        StartCoroutine(WalkOut(Body));
    }

    IEnumerator WalkOut(CustomerCharacter body)
    {
        yield return body.WalkTo(Director.DoorExit, LeaveWithinMeters);
        if (Body == body) Body = null;
        leaving = false;
        Destroy(body.gameObject);
    }

    /// <summary>Gone at once, for the next person at the title.</summary>
    public void Clear()
    {
        StopAllCoroutines();
        StopListening();
        if (Body != null) Destroy(Body.gameObject);
        Body = null;
        leaving = false;
    }

    void OnDestroy() => StopListening();

    void StopListening()
    {
        if (!listening) return;
        listening = false;
        Audio.NarrationSource = null;
        Audio.NarrationStarted -= OnNarration;
    }

    void OnNarration(AudioClip clip)
    {
        if (InStore && Body.FaceRig != null) Body.FaceRig.Talk(Audio.EnvelopeOf(clip), clip.length);
    }

    // ---------- The apron ----------

    /// <summary>Two curved sheets over the clothes, measured from her own bones: a bib on the chest and a skirt on the hips.</summary>
    void Dress()
    {
        var animator = Body.Animator;
        if (ApronMaterial == null || animator == null || !animator.isHuman) return;
        // Fresh from Instantiate the skeleton is still in the model's A-pose; measure her where the idle clip holds her.
        animator.Update(0f);
        var hips = animator.GetBoneTransform(HumanBodyBones.Hips);
        var chest = animator.GetBoneTransform(HumanBodyBones.UpperChest) ?? animator.GetBoneTransform(HumanBodyBones.Chest);
        var neck = animator.GetBoneTransform(HumanBodyBones.Neck);
        if (hips == null || chest == null || neck == null) return;

        var forward = Body.transform.forward;
        var facing = Quaternion.LookRotation(forward);
        float waist = hips.position.y + 0.13f;
        float bibTop = neck.position.y - 0.16f;
        Sheet("Apron Bib", chest, Over(chest, 0.105f, bibTop), facing, 0.2f, 0.34f, bibTop - waist + 0.03f, 0.17f);
        Sheet("Apron Skirt", hips, Over(hips, 0.115f, waist), facing, 0.36f, 0.42f, waist - (hips.position.y - 0.36f), 0.2f);

        Vector3 Over(Transform bone, float forwardMeters, float height)
        {
            var at = bone.position + forward * (forwardMeters + SheetMargin);
            at.y = height;
            return at;
        }

        // Straps from the top corners of the bib up to the sides of the neck.
        var right = Body.transform.right;
        foreach (float side in new[] { -1f, 1f })
        {
            var from = Over(chest, 0.085f, bibTop) + right * (side * 0.085f);
            var to = neck.position + forward * 0.055f + right * (side * 0.065f) + Vector3.up * 0.025f;
            var strap = GameObject.CreatePrimitive(PrimitiveType.Cube);
            strap.name = "Apron Strap";
            Destroy(strap.GetComponent<Collider>());
            strap.transform.SetParent(chest, true);
            strap.transform.SetPositionAndRotation((from + to) * 0.5f, Quaternion.LookRotation(to - from, forward));
            strap.transform.localScale = Vector3.one;
            strap.transform.localScale = LocalForLossy(strap.transform, new Vector3(0.022f, 0.004f, Vector3.Distance(from, to)));
            strap.GetComponent<Renderer>().sharedMaterial = ApronMaterial;
        }

        // A name badge. The canvas reads from behind its forward.
        var badgeAt = Over(chest, 0.105f, bibTop - 0.055f) + forward * 0.003f;
        var badge = WorldUi.CreatePanel(chest, "Name Badge", badgeAt, Quaternion.LookRotation(-forward), new Vector2(190f, 66f), 0.00042f, Color.white);
        var name = WorldUi.AddText(badge, 0f, 0f, 190f, 66f, 44, TextAnchor.MiddleCenter);
        name.fontStyle = FontStyle.Bold;
        name.color = WorldUi.Palette.Navy;
        name.text = "점장";
        Keep(badge);

        void Keep(RectTransform rect) => rect.localScale = LocalForLossy(rect, Vector3.one * 0.00042f);
    }

    void Sheet(string name, Transform bone, Vector3 top, Quaternion facing, float widthTop, float widthBottom, float height, float radius)
    {
        var go = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
        go.transform.SetParent(bone, true);
        go.transform.SetPositionAndRotation(top, facing);
        go.transform.localScale = LocalForLossy(go.transform, Vector3.one);
        go.GetComponent<MeshFilter>().sharedMesh = CurvedSheet(widthTop, widthBottom, height, radius);
        go.GetComponent<MeshRenderer>().sharedMaterial = ApronMaterial;
    }

    /// <summary>Rocketbox bones carry scale. This is the local scale that comes to the wanted size in the world.</summary>
    static Vector3 LocalForLossy(Transform t, Vector3 lossy)
    {
        var parent = t.parent != null ? t.parent.lossyScale : Vector3.one;
        return new Vector3(lossy.x / parent.x, lossy.y / parent.y, lossy.z / parent.z);
    }

    /// <summary>A sheet bent round the body. Origin at the top centre, +z out of the body, downward along -y.</summary>
    static Mesh CurvedSheet(float widthTop, float widthBottom, float height, float radius)
    {
        const int columns = 12, rows = 6;
        var vertices = new Vector3[(columns + 1) * (rows + 1)];
        var normals = new Vector3[vertices.Length];
        var triangles = new int[columns * rows * 6];
        for (int row = 0; row <= rows; row++)
        {
            float t = (float)row / rows;
            float half = Mathf.Lerp(widthTop, widthBottom, t) * 0.5f;
            for (int column = 0; column <= columns; column++)
            {
                float angle = ((float)column / columns * 2f - 1f) * half / radius;
                int at = row * (columns + 1) + column;
                vertices[at] = new Vector3(Mathf.Sin(angle) * radius, -t * height, (Mathf.Cos(angle) - 1f) * radius);
                normals[at] = new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle));
            }
        }
        int index = 0;
        for (int row = 0; row < rows; row++)
            for (int column = 0; column < columns; column++)
            {
                int a = row * (columns + 1) + column, b = a + 1, c = a + columns + 1, d = c + 1;
                triangles[index++] = a;
                triangles[index++] = c;
                triangles[index++] = d;
                triangles[index++] = a;
                triangles[index++] = d;
                triangles[index++] = b;
            }
        var mesh = new Mesh { name = "Apron Sheet", vertices = vertices, normals = normals, triangles = triangles };
        mesh.RecalculateBounds();
        return mesh;
    }
}
