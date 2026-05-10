using FFXIVClientStructs.FFXIV.Client.Game;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace BagAssistant.Services;

/// <summary>
/// Raw move enqueue entry used by the queue. Either the move targets a specific destination
/// slot (<see cref="UseFreeSlot"/> = false) or it picks the first free slot in <see cref="DestBag"/>.
/// </summary>
public readonly record struct QueuedMove(
    InventoryType SrcBag,
    int SrcSlot,
    InventoryType DestBag,
    int DestSlot,
    bool UseFreeSlot,
    string Label);

/// <summary>
/// Single source of truth for the paced item-move queue. The plugin ticks this once per frame
/// (so multiple UI windows sharing the queue can't double-process moves).
/// Tracks move history for undo capability.
/// </summary>
public sealed class SortQueueService(InventoryService inventoryService, Configuration config)
{
    private readonly Queue<QueuedMove> queue = new();
    private readonly List<(InventoryType SrcBag, int SrcSlot, InventoryType DestBag, int DestSlot)> moveHistory = new();
    private readonly Stopwatch timer = new();
    private int total;
    private int nextDelayMs;
    private static readonly Random Rng = new();

    public string StatusMessage { get; set; } = string.Empty;
    public bool IsBusy => queue.Count > 0;
    public int Remaining => queue.Count;
    public int Total => total;
    public bool CanUndo => moveHistory.Count > 0;

    public Action? OnComplete { get; set; }

    /// <summary>
    /// Legacy enqueue: takes (item, destBag, destSlot?, srcSlotOverride?) tuples and converts
    /// each into a <see cref="QueuedMove"/>. Free-slot moves are honored when destSlot is null.
    /// </summary>
    public void Enqueue(IEnumerable<(InventoryItemInfo Item, InventoryType DestBag, int? DestSlot, int? SrcSlotOverride)> items, string description)
    {
        var added = 0;
        foreach (var entry in items)
        {
            var srcSlot = entry.SrcSlotOverride ?? entry.Item.Slot;
            var useFree = !entry.DestSlot.HasValue;
            queue.Enqueue(new QueuedMove(
                entry.Item.Container,
                srcSlot,
                entry.DestBag,
                entry.DestSlot ?? 0,
                useFree,
                entry.Item.Name));
            added++;
        }
        FinalizeEnqueue(added, description);
    }

    /// <summary>
    /// Direct enqueue: callers supply explicit (src,dest) move tuples. Used by the
    /// Apply Zones full rebuild planner where a virtual-swap simulator generates the moves.
    /// </summary>
    public void EnqueueDirect(IEnumerable<QueuedMove> moves, string description)
    {
        var added = 0;
        foreach (var m in moves)
        {
            queue.Enqueue(m);
            added++;
        }
        FinalizeEnqueue(added, description);
    }

    private void FinalizeEnqueue(int added, string description)
    {
        if (added == 0)
        {
            StatusMessage = $"{description}: nothing to move.";
            return;
        }
        total = queue.Count;
        nextDelayMs = 0;
        timer.Restart();
        StatusMessage = $"{description}: moving {total} item(s)...";
    }

    public void Stop()
    {
        queue.Clear();
        timer.Stop();
        StatusMessage = "Sort stopped.";
    }

    public void Tick()
    {
        if (queue.Count == 0) return;
        if (timer.IsRunning && timer.ElapsedMilliseconds < nextDelayMs) return;

        var entry = queue.Dequeue();
        bool success;
        string msg;
        int finalDestSlot;

        if (entry.UseFreeSlot)
        {
            // Build a minimal InventoryItemInfo for the free-slot helper (it only uses
            // Container/Slot/Name).
            var stub = new InventoryItemInfo(0, entry.Label, entry.SrcBag, entry.SrcSlot,
                false, false, false, false, false, 0, 0, 0, 0, 0, 0, 0, Array.Empty<string>(), 0);
            var res = inventoryService.MoveOrSwap(stub, entry.DestBag);
            success = res.Success;
            msg = res.Message;
            finalDestSlot = 0; // unknown; best-effort for undo
        }
        else
        {
            var res = inventoryService.MoveSlotToSlot(entry.SrcBag, entry.SrcSlot, entry.DestBag, entry.DestSlot, entry.Label);
            success = res.Success;
            msg = res.Message;
            finalDestSlot = entry.DestSlot;
        }

        if (success)
        {
            moveHistory.Add((entry.SrcBag, entry.SrcSlot, entry.DestBag, finalDestSlot));
        }

        var done = total - queue.Count;

        if (queue.Count == 0)
        {
            timer.Stop();
            StatusMessage = $"Sort complete: {done}/{total}. (Undo available)";
            total = 0;
            OnComplete?.Invoke();
        }
        else
        {
            var min = Math.Max(20, config.MoveDelayMinMs);
            var max = Math.Max(min + 1, config.MoveDelayMaxMs);
            nextDelayMs = Rng.Next(min, max);
            timer.Restart();
            StatusMessage = $"Sorting... {done}/{total} (next in {nextDelayMs}ms)";
        }

        if (!success) StatusMessage = $"[{done}/{total}] {msg}";
    }

    public void Undo()
    {
        if (moveHistory.Count == 0)
        {
            StatusMessage = "No moves to undo.";
            return;
        }

        var movesToUndo = new Stack<(InventoryType SrcBag, int SrcSlot, InventoryType DestBag, int DestSlot)>(moveHistory);
        moveHistory.Clear();
        StatusMessage = "Undoing...";

        int count = 0;
        while (movesToUndo.Count > 0)
        {
            var (srcBag, srcSlot, destBag, destSlot) = movesToUndo.Pop();
            var (success, _) = inventoryService.MoveSlotToSlot(destBag, destSlot, srcBag, srcSlot, $"Undo {count}");
            if (success) count++;
        }

        StatusMessage = $"Undo complete: reversed {count} move(s).";
    }
}
