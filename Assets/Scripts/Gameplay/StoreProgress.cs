using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// What the store has become and what the player has to spend, kept between sessions
/// (docs/p2/해금_성장_기획_2026-11-10.md). One JSON file; a broken or missing one is a new store.
/// </summary>
[Serializable]
public class StoreProgress
{
    public const string FileName = "store_progress.json";

    public int version = 1;
    /// <summary>The next day to work, counted from 1. The first days are the training.</summary>
    public int day = 1;
    /// <summary>The training is done: the ordering tablet is the player's.</summary>
    public bool graduated;
    public int budget;
    public int totalSales;
    public int daysWorked;
    /// <summary>Ids of Data/unlocks.csv in the store now.</summary>
    public List<string> owned = new List<string>();
    /// <summary>Ordered today; in the store from the next morning.</summary>
    public List<string> arriving = new List<string>();

    /// <summary>Where this is written. Null: kept in memory only.</summary>
    [NonSerialized] public string Path;

    /// <summary>There is a store to go back to, so the title offers it.</summary>
    public bool Started => day > 1 || graduated;

    public bool Has(string id) => owned.Contains(id);
    public bool Ordered(string id) => owned.Contains(id) || arriving.Contains(id);
    public bool CanBuy(UnlockItem item) => !Ordered(item.Id) && budget >= item.Price;

    public bool Buy(UnlockItem item)
    {
        if (!CanBuy(item)) return false;
        budget -= item.Price;
        arriving.Add(item.Id);
        return true;
    }

    /// <summary>The morning's delivery: what was ordered is in the store. Returns how many things came.</summary>
    public int ReceiveArrivals()
    {
        int count = arriving.Count;
        owned.AddRange(arriving);
        arriving.Clear();
        return count;
    }

    /// <summary>A new store, keeping where it is saved.</summary>
    public void Reset()
    {
        day = 1;
        graduated = false;
        budget = totalSales = daysWorked = 0;
        owned.Clear();
        arriving.Clear();
    }

    public static StoreProgress Load(string path)
    {
        StoreProgress progress = null;
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            try
            {
                progress = JsonUtility.FromJson<StoreProgress>(File.ReadAllText(path));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Progress] {path} could not be read ({e.Message}); starting a new store.");
            }
        }
        if (progress == null) progress = new StoreProgress();
        if (progress.owned == null) progress.owned = new List<string>();
        if (progress.arriving == null) progress.arriving = new List<string>();
        progress.day = Mathf.Max(1, progress.day);
        progress.Path = path;
        Debug.Log($"[Progress] Loaded day={progress.day} graduated={progress.graduated} budget={progress.budget} owned={progress.owned.Count} arriving={progress.arriving.Count} from {(string.IsNullOrEmpty(path) ? "(memory)" : path)}");
        return progress;
    }

    public void Save()
    {
        if (string.IsNullOrEmpty(Path)) return;
        try
        {
            var folder = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
            File.WriteAllText(Path, JsonUtility.ToJson(this, true));
        }
        catch (Exception e)
        {
            // Losing a save must not stop the shift.
            Debug.LogWarning($"[Progress] {Path} could not be written: {e.Message}");
        }
    }
}
