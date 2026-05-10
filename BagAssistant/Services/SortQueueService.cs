using FFXIVClientStructs.FFXIV.Client.Game;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace BagAssistant.Services;

/// <summary>
/// Single source of truth for the paced item-move queue. The plugin ticks this once per frame
/// (so multiple UI windows sharing the queue can't double-process moves).
/// </summary>
public sealed class SortQueueService(InventoryService inventoryService, Configuration config)
{
    private readonly Queue<(InventoryItemInfo Item, InventoryType DestBag)> queue = new();
    private readonly Stopwatch timer = new();
    private int total;
    private int nextDelayMs;
    private static readonly Random Rng = new();

    public string StatusMessage { get; private set; } = string.Empty;
    public bool IsBusy => queue.Count > 0;
    public int Remaining => queue.Count;
    public int Total => total;

    public void Enqueue(IEnumerable<(InventoryItemInfo Item, InventoryType DestBag)> items, string description)
    {
        var added = 0;
        foreach (var entry in items)
        {
            queue.Enqueue(entry);
            added++;
        }
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
        var (success, msg) = inventoryService.MoveOrSwap(entry.Item, entry.DestBag);
        var done = total - queue.Count;

        if (queue.Count == 0)
        {
            timer.Stop();
            StatusMessage = $"Sort complete: {done}/{total}.";
            total = 0;
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
}
