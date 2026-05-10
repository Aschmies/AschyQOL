using ArmouryCleaner.Services;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;

namespace ArmouryCleaner.Windows
{
    public sealed class ArmouryCleanerWindow : Window, IDisposable
    {
        private readonly ArmouryCleanerPlugin plugin;
        private readonly ArmouryService armouryService;
        private Configuration Config => plugin.Configuration;

        private List<ArmouryItem> scanResults = [];
        private bool confirmingMoveAll;
        private string statusMessage = string.Empty;

        // Discard queue — processes one item per Draw() tick with a random 800-1100ms delay
        private readonly Queue<ArmouryItem> discardQueue = new();
        private int discardTotal;
        private readonly Stopwatch discardTimer = new();
        private int nextDelayMs;
        private static readonly Random Rng = new();

        // Jobs grouped by role for display
        private static readonly (string Abbr, string Role)[] CombatJobs =
        [
            ("PLD", "Tank"), ("WAR", "Tank"), ("DRK", "Tank"), ("GNB", "Tank"),
            ("WHM", "Healer"), ("SCH", "Healer"), ("AST", "Healer"), ("SGE", "Healer"),
            ("MNK", "Melee"), ("DRG", "Melee"), ("NIN", "Melee"), ("SAM", "Melee"), ("RPR", "Melee"), ("VPR", "Melee"),
            ("BRD", "Phys Range"), ("MCH", "Phys Range"), ("DNC", "Phys Range"),
            ("BLM", "Mag Range"), ("SMN", "Mag Range"), ("RDM", "Mag Range"), ("PCT", "Mag Range"),
            ("BLU", "Limited"),
        ];

        private static readonly string[] Roles = CombatJobs.Select(j => j.Role).Distinct().ToArray();

        public ArmouryCleanerWindow(ArmouryCleanerPlugin plugin, ArmouryService armouryService)
            : base("Armoury Cleaner##ArmouryCleaner")
        {
            this.plugin = plugin;
            this.armouryService = armouryService;
            SizeConstraints = new WindowSizeConstraints
            {
                MinimumSize = new Vector2(560, 420),
                MaximumSize = new Vector2(1000, 900),
            };
        }

        public void Dispose() { }

        private const string NoneSentinel = "__none__";

        public override void Draw()
        {
            TickDiscardQueue();
            DrawInstructions();
            ImGui.Separator();
            DrawFilters();
            ImGui.Separator();
            DrawResults();
        }

        private void TickDiscardQueue()
        {
            if (discardQueue.Count == 0) return;
            if (discardTimer.IsRunning && discardTimer.ElapsedMilliseconds < nextDelayMs) return;

            var item = discardQueue.Dequeue();
            var (success, msg) = DiscardWithFallback(item);
            var done = discardTotal - discardQueue.Count;
            if (success)
                scanResults.Remove(item);

            if (discardQueue.Count == 0)
            {
                discardTimer.Stop();
                statusMessage = $"Discarded {done}/{discardTotal} item(s).";
            }
            else
            {
                nextDelayMs = Rng.Next(800, 1101);
                discardTimer.Restart();
                statusMessage = $"Discarding... {done}/{discardTotal} (next in {nextDelayMs}ms)";
            }

            if (!success)
                statusMessage = $"[{done}/{discardTotal}] {msg}";
        }

        private static void DrawInstructions()
        {
            if (ImGui.TreeNode("How to use Armoury Cleaner##instructions"))
            {
                ImGui.TextWrapped("1. Select which jobs' gear you want to clear out using the checkboxes below. Leave all ticked to include gear for every job.");
                ImGui.TextWrapped("2. Set the Equip Level Range to target a tier of gear — e.g. 1 to 90 to catch all levelling gear below endgame.");
                ImGui.TextWrapped("3. Optionally enable Filter by iLvl to restrict by item level instead of (or in addition to) equip level.");
                ImGui.TextWrapped("4. Skip High Quality keeps your HQ pieces safe. Skip Untradeable hides bound/unsellable gear.");
                ImGui.TextWrapped("5. Click Scan Armoury. Matched items appear in the table below.");
                ImGui.TextWrapped("6. Click Move next to individual items, or Move All to send everything to your regular inventory.");
                ImGui.TextWrapped("7. Open your inventory in-game, right-click each item, and choose Discard.");
                ImGui.Spacing();
                ImGui.TextColored(new Vector4(1f, 0.85f, 0.3f, 1f), "Example: to clean out all tank gear from Shadowbringers, check only Tank jobs and set level range 71–80, then Scan.");
                ImGui.TreePop();
            }
        }

        private void DrawFilters()
        {
            ImGui.TextUnformatted("Job Filter");
            ImGui.SameLine();
            if (ImGui.SmallButton("All##selectAll"))
            {
                Config.SelectedJobs.Clear();
                Config.Save();
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("None##selectNone"))
            {
                Config.SelectedJobs = [NoneSentinel];
                Config.Save();
            }

            foreach (var role in Roles)
            {
                ImGui.TextColored(new Vector4(0.6f, 0.8f, 1f, 1f), role);
                ImGui.SameLine();
                var jobsInRole = CombatJobs.Where(j => j.Role == role).ToArray();
                for (var i = 0; i < jobsInRole.Length; i++)
                {
                    var (abbr, _) = jobsInRole[i];
                    var enabled = Config.SelectedJobs.Count == 0 || Config.SelectedJobs.Contains(abbr);
                    if (ImGui.Checkbox($"{abbr}##job_{abbr}", ref enabled))
                        ToggleJob(abbr, enabled);
                    if (i < jobsInRole.Length - 1)
                        ImGui.SameLine();
                }
            }

            ImGui.Spacing();

            // Level range
            var minLvl = Config.MinLevel;
            var maxLvl = Config.MaxLevel;
            ImGui.SetNextItemWidth(100);
            if (ImGui.InputInt("##minlvl", ref minLvl, 1, 5))
            {
                Config.MinLevel = Math.Clamp(minLvl, 1, 100);
                Config.Save();
            }
            ImGui.SameLine();
            ImGui.TextUnformatted("–");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(100);
            if (ImGui.InputInt("##maxlvl", ref maxLvl, 1, 5))
            {
                Config.MaxLevel = Math.Clamp(maxLvl, 1, 100);
                Config.Save();
            }
            ImGui.SameLine();
            ImGui.TextUnformatted("Equip Level Range");

            // Item level filter
            var filterByIlvl = Config.FilterByIlvl;
            if (ImGui.Checkbox("Filter by iLvl##filterilvl", ref filterByIlvl))
            {
                Config.FilterByIlvl = filterByIlvl;
                Config.Save();
            }
            if (Config.FilterByIlvl)
            {
                ImGui.SameLine();
                var minIlvl = Config.MinIlvl;
                var maxIlvl = Config.MaxIlvl;
                ImGui.SetNextItemWidth(110);
                if (ImGui.InputInt("##minilvl", ref minIlvl, 1, 10))
                {
                    Config.MinIlvl = Math.Clamp(minIlvl, 0, 9999);
                    Config.Save();
                }
                ImGui.SameLine();
                ImGui.TextUnformatted("–");
                ImGui.SameLine();
                ImGui.SetNextItemWidth(110);
                if (ImGui.InputInt("##maxilvl", ref maxIlvl, 1, 10))
                {
                    Config.MaxIlvl = Math.Clamp(maxIlvl, 0, 9999);
                    Config.Save();
                }
            }

            // Skip flags
            var skipHQ = Config.SkipHighQuality;
            if (ImGui.Checkbox("Skip High Quality (✦)##skiphq", ref skipHQ))
            {
                Config.SkipHighQuality = skipHQ;
                Config.Save();
            }
            ImGui.SameLine();
            var skipUntradeable = Config.SkipUntradeable;
            if (ImGui.Checkbox("Skip Untradeable##skipuntrade", ref skipUntradeable))
            {
                Config.SkipUntradeable = skipUntradeable;
                Config.Save();
            }

            // Rarity skip flags
            ImGui.TextUnformatted("Skip Rarity:");
            ImGui.SameLine();
            var skipWhite  = Config.SkipWhite;
            var skipGreen  = Config.SkipGreen;
            var skipBlue   = Config.SkipBlue;
            var skipPurple = Config.SkipPurple;
            var skipPink   = Config.SkipPink;
            DrawRarityCheckbox("White##skipWhite",   new Vector4(0.85f, 0.85f, 0.85f, 1f), ref skipWhite,  v => { Config.SkipWhite  = v; Config.Save(); });
            ImGui.SameLine();
            DrawRarityCheckbox("Green##skipGreen",   new Vector4(0.13f, 0.87f, 0.13f, 1f), ref skipGreen,  v => { Config.SkipGreen  = v; Config.Save(); });
            ImGui.SameLine();
            DrawRarityCheckbox("Blue##skipBlue",     new Vector4(0.33f, 0.67f, 1.00f, 1f), ref skipBlue,   v => { Config.SkipBlue   = v; Config.Save(); });
            ImGui.SameLine();
            DrawRarityCheckbox("Purple##skipPurple", new Vector4(0.75f, 0.40f, 1.00f, 1f), ref skipPurple, v => { Config.SkipPurple = v; Config.Save(); });
            ImGui.SameLine();
            DrawRarityCheckbox("Pink##skipPink",     new Vector4(1.00f, 0.50f, 0.80f, 1f), ref skipPink,   v => { Config.SkipPink   = v; Config.Save(); });

            ImGui.Spacing();
            if (ImGui.Button("Scan Armoury##scan"))
            {
                scanResults = armouryService.ScanCandidates(Config);
                confirmingMoveAll = false;
                statusMessage = scanResults.Count == 0
                    ? "No matching items found."
                    : $"Found {scanResults.Count} candidate item(s).";
            }
            if (!string.IsNullOrEmpty(statusMessage))
            {
                ImGui.SameLine();
                ImGui.TextUnformatted(statusMessage);
            }
        }

        private void DrawResults()
        {
            if (scanResults.Count == 0)
            {
                ImGui.TextDisabled("No results — adjust filters and click Scan.");
                return;
            }

            // Auto-discard toggle
            var autoDiscard = Config.AutoDiscard;
            if (ImGui.Checkbox("Auto-Discard (skip inventory step)##autodiscard", ref autoDiscard))
            {
                Config.AutoDiscard = autoDiscard;
                Config.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("When enabled, items are moved to a free inventory slot and immediately discarded.\nWhen disabled, items are only moved to your inventory so you can review them first.");
            if (Config.AutoDiscard)
            {
                ImGui.SameLine();
                var direct = Config.DiscardDirectlyFromArmoury;
                if (ImGui.Checkbox("Discard from armoury directly##directdiscard", ref direct))
                {
                    Config.DiscardDirectlyFromArmoury = direct;
                    Config.Save();
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Skip the move-to-inventory step entirely — discard directly from the armoury slot.\nFaster and avoids touching your bags. Falls back to move+discard if the game rejects it.");
            }
            if (Config.AutoDiscard)
            {
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(1f, 0.4f, 0.2f, 1f), "WARNING: items will be permanently deleted!");
            }

            ImGui.TextUnformatted($"{scanResults.Count} item(s) matched.");
            var actionLabel = Config.AutoDiscard ? "Discard" : "Move";
            var actionTooltip = Config.AutoDiscard
                ? "Permanently discard this item immediately."
                : "Move this item to your inventory so you can right-click → Discard it.";

            if (ImGui.BeginTable("##armouryResults", 6,
                ImGuiTableFlags.Borders | ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit,
                new Vector2(0, 220)))
            {
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Lvl", ImGuiTableColumnFlags.WidthFixed, 36);
                ImGui.TableSetupColumn("iLvl", ImGuiTableColumnFlags.WidthFixed, 42);
                ImGui.TableSetupColumn("Jobs", ImGuiTableColumnFlags.WidthFixed, 130);
                ImGui.TableSetupColumn("HQ", ImGuiTableColumnFlags.WidthFixed, 26);
                ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed, 66);
                ImGui.TableHeadersRow();

                for (var i = 0; i < scanResults.Count; i++)
                {
                    var item = scanResults[i];
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TextColored(RarityColor(item.Rarity), item.Name);
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(item.Level.ToString());
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(item.ILvl.ToString());
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(string.Join(" ", item.ClassJobs));
                    ImGui.TableNextColumn();
                    if (item.IsHQ)
                        ImGui.TextColored(new Vector4(1f, 0.85f, 0.2f, 1f), "HQ");
                    ImGui.TableNextColumn();
                    if (ImGui.SmallButton($"{actionLabel}##act{i}"))
                    {
                        var (success, msg) = Config.AutoDiscard
                            ? DiscardWithFallback(item)
                            : armouryService.MoveToInventory(item);
                        statusMessage = msg;
                        if (success)
                        {
                            scanResults.RemoveAt(i);
                            i--;
                        }
                    }
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(actionTooltip);
                }

                ImGui.EndTable();
            }

            ImGui.Spacing();
            var bulkLabel = Config.AutoDiscard
                ? $"Discard All {scanResults.Count}##discardall"
                : $"Move All {scanResults.Count} to Inventory##moveall";

            // While a queued discard is in progress, show progress instead of the normal buttons
            if (discardQueue.Count > 0)
            {
                var done = discardTotal - discardQueue.Count;
                ImGui.TextColored(new Vector4(1f, 0.85f, 0.2f, 1f), $"Discarding {done}/{discardTotal}...");
                ImGui.SameLine();
                if (ImGui.Button("Cancel Queue##cancelQueue"))
                {
                    discardQueue.Clear();
                    discardTimer.Stop();
                    statusMessage = $"Cancelled — {done} item(s) already discarded.";
                }
            }
            else if (!confirmingMoveAll)
            {
                if (ImGui.Button(bulkLabel))
                    confirmingMoveAll = true;
            }
            else
            {
                var warningText = Config.AutoDiscard
                    ? $"PERMANENTLY DISCARD all {scanResults.Count} items? This cannot be undone!"
                    : $"Move all {scanResults.Count} items to your inventory?";
                ImGui.TextColored(new Vector4(1f, 0.4f, 0.2f, 1f), warningText);
                ImGui.SameLine();
                if (ImGui.Button("Confirm##confirmAll"))
                {
                    if (Config.AutoDiscard)
                    {
                        // Enqueue all — TickDiscardQueue() fires one per frame with 800-1100ms random delay
                        discardQueue.Clear();
                        foreach (var item in scanResults)
                            discardQueue.Enqueue(item);
                        discardTotal = discardQueue.Count;
                        nextDelayMs = Rng.Next(800, 1101);
                        discardTimer.Restart();
                        statusMessage = $"Discarding 0/{discardTotal}...";
                    }
                    else
                    {
                        // Move All is instant — no ban risk for moving
                        var processed = 0;
                        for (var i = scanResults.Count - 1; i >= 0; i--)
                        {
                            var (success, _) = armouryService.MoveToInventory(scanResults[i]);
                            if (success) { scanResults.RemoveAt(i); processed++; }
                        }
                        statusMessage = $"Moved {processed} item(s). Right-click each in your inventory to Discard.";
                    }
                    confirmingMoveAll = false;
                }
                ImGui.SameLine();
                if (ImGui.Button("Cancel##cancelAll"))
                    confirmingMoveAll = false;
            }

            if (!Config.AutoDiscard)
            {
                ImGui.Spacing();
                ImGui.TextDisabled("Items moved to inventory can be discarded via right-click → Discard.");
            }
        }

        private (bool Success, string Message) DiscardWithFallback(ArmouryItem item)
        {
            if (Config.DiscardDirectlyFromArmoury)
            {
                var (ok, msg) = armouryService.DiscardFromArmoury(item);
                if (ok) return (true, msg);
                // Fall through to move+discard if the game rejected the direct discard
            }
            return armouryService.MoveAndDiscard(item);
        }

        private void ToggleJob(string abbr, bool nowEnabled)
        {
            // If in "none" state (sentinel), clear it so we start from an empty explicit selection
            if (Config.SelectedJobs.Count == 1 && Config.SelectedJobs[0] == NoneSentinel)
                Config.SelectedJobs.Clear();

            if (Config.SelectedJobs.Count == 0 && !nowEnabled)
            {
                // Was "all" state → unchecking one: populate with every job except this one
                Config.SelectedJobs = CombatJobs.Select(j => j.Abbr).Where(a => a != abbr).ToList();
            }
            else if (nowEnabled)
            {
                if (!Config.SelectedJobs.Contains(abbr))
                    Config.SelectedJobs.Add(abbr);
            }
            else
            {
                Config.SelectedJobs.Remove(abbr);
                // Prevent empty list being read as "all" when the last job is unchecked
                if (Config.SelectedJobs.Count == 0)
                    Config.SelectedJobs.Add(NoneSentinel);
            }

            // Collapse back to "all" (empty) if every real job is now explicitly selected
            if (Config.SelectedJobs.Count == CombatJobs.Length)
                Config.SelectedJobs.Clear();

            Config.Save();
        }

        private static void DrawRarityCheckbox(string label, Vector4 color, ref bool value, Action<bool> onChange)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, color);
            if (ImGui.Checkbox(label, ref value))
                onChange(value);
            ImGui.PopStyleColor();
        }

        private static Vector4 RarityColor(byte rarity) => rarity switch
        {
            2 => new Vector4(0.13f, 0.87f, 0.13f, 1f), // Green
            3 => new Vector4(0.33f, 0.67f, 1.00f, 1f), // Blue
            4 => new Vector4(0.75f, 0.40f, 1.00f, 1f), // Purple
            7 => new Vector4(1.00f, 0.50f, 0.80f, 1f), // Pink
            _ => new Vector4(0.85f, 0.85f, 0.85f, 1f), // White / default
        };
    }
}
