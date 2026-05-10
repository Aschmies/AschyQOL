using BagAssistant.Services;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;

namespace BagAssistant.Windows;

public sealed class BagAssistantWindow : Window, IDisposable
{
    private readonly BagAssistantPlugin plugin;
    private readonly IDataManager dataManager;
    private Configuration Config => plugin.Configuration;

    private string statusMessage = string.Empty;
    private List<InventoryItemInfo> previewItems = new();
    private bool confirmingSort;
    private QuickPreset? confirmingPreset;
    private bool confirmingSmartSort;
    private int selectedBag = 0;

    // Cached UI category list.
    private readonly List<(uint RowId, string Name)> uiCategoryCache = new();
    private string uiCategoryFilter = string.Empty;

    private static readonly (string Abbr, string Group)[] AllJobs =
    [
        ("PLD","Tank"),("WAR","Tank"),("DRK","Tank"),("GNB","Tank"),
        ("WHM","Healer"),("SCH","Healer"),("AST","Healer"),("SGE","Healer"),
        ("MNK","Melee"),("DRG","Melee"),("NIN","Melee"),("SAM","Melee"),("RPR","Melee"),("VPR","Melee"),
        ("BRD","Phys Range"),("MCH","Phys Range"),("DNC","Phys Range"),
        ("BLM","Mag Range"),("SMN","Mag Range"),("RDM","Mag Range"),("PCT","Mag Range"),("BLU","Mag Range"),
        ("CRP","Crafter"),("BSM","Crafter"),("ARM","Crafter"),("GSM","Crafter"),("LTW","Crafter"),("WVR","Crafter"),("ALC","Crafter"),("CUL","Crafter"),
        ("MIN","Gatherer"),("BTN","Gatherer"),("FSH","Gatherer"),
    ];

    private static readonly (string Label, byte Value)[] RarityChoices =
    [
        ("White (1)", 1), ("Green (2)", 2), ("Blue (3)", 3), ("Purple (4)", 4), ("Pink (7)", 7),
    ];

    public BagAssistantWindow(BagAssistantPlugin plugin, IDataManager dataManager)
        : base("Bag Assistant##BagAssistant")
    {
        this.plugin = plugin;
        this.dataManager = dataManager;
        IsOpen = false;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(640, 520),
            MaximumSize = new Vector2(1200, 1000),
        };
        BuildCategoryCache();
    }

    public void Dispose() { }

    private void BuildCategoryCache()
    {
        var sheet = dataManager.GetExcelSheet<ItemUICategory>();
        if (sheet == null) return;
        foreach (var row in sheet)
        {
            var name = row.Name.ToString();
            if (string.IsNullOrWhiteSpace(name)) continue;
            uiCategoryCache.Add((row.RowId, name));
        }
        uiCategoryCache.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
    }

    public override void Draw()
    {
        if (ImGui.BeginTabBar("##BAQTabs"))
        {
            if (ImGui.BeginTabItem("Quick Sort"))
            {
                DrawQuickSortTab();
                ImGui.EndTabItem();
            }
            // if (ImGui.BeginTabItem("Custom Rules"))
            // {
            //     DrawRulesTab();
            //     ImGui.EndTabItem();
            // }
            if (ImGui.BeginTabItem("Layout Zones"))
            {
                DrawVisualZonesTab();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Run / Preview"))
            {
                DrawRunTab();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Junk & Settings"))
            {
                DrawSettingsTab();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Advanced"))
            {
                DrawAdvancedTab();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Help"))
            {
                DrawHelpTab();
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }

                if (openJunkConfirmPopup)
        {
            ImGui.OpenPopup("Confirm Junk Discard");
            openJunkConfirmPopup = false;
        }

        if (ImGui.BeginPopupModal("Confirm Junk Discard", ref openJunkConfirmPopup, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.Text("The following items will be permanently discarded:");
            ImGui.Separator();

            if (pendingJunkDiscards != null && pendingJunkDiscards.Count > 0)
            {
                ImGui.BeginChild("##junkList", new Vector2(400, 200), true);
                for (int i = pendingJunkDiscards.Count - 1; i >= 0; i--)
                {
                    var item = pendingJunkDiscards[i];
                    ImGui.Text($"{item.Name} (Price: {item.VendorPrice} gil)");
                    ImGui.SameLine(ImGui.GetWindowWidth() - 70);
                    if (ImGui.Button($"Keep##{i}"))
                    {
                        pendingJunkDiscards.RemoveAt(i);
                    }
                }
                ImGui.EndChild();
            }
            else
            {
                ImGui.Text("No junk items found, or all items were removed from discard list.");
            }

            ImGui.Separator();
            if (ImGui.Button("Confirm Discard", new Vector2(120, 0)))
            {
                if (pendingJunkDiscards != null && pendingJunkDiscards.Count > 0)
                {
                    plugin.DeleteSpecificJunk(pendingJunkDiscards);
                }
                ImGui.CloseCurrentPopup();
            }
            ImGui.SetItemDefaultFocus();
            ImGui.SameLine();
            if (ImGui.Button("Cancel", new Vector2(120, 0)))
            {
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }

        if (!string.IsNullOrEmpty(plugin.SortQueueStatus))
        {
            ImGui.Separator();
            ImGui.TextColored(new Vector4(0.7f, 0.9f, 1f, 1f), plugin.SortQueueStatus);
        }
        else if (!string.IsNullOrEmpty(statusMessage))
        {
            ImGui.Separator();
            ImGui.TextColored(new Vector4(0.7f, 0.9f, 1f, 1f), statusMessage);
        }
    }

    // ─── Tab: Quick Sort ─────────────────────────────────────────────────────

    private void DrawQuickSortTab()
    {
        ImGui.TextWrapped("One-click sorts. Pick a preset and BagAssistant will move every matching item into the named bag. No rules, no setup.");
        ImGui.Spacing();

        var queueBusy = plugin.IsSortQueueBusy;

        // ── Smart Sort Everything ───────────────────────────────────────────
        ImGui.TextColored(new Vector4(1f, 0.85f, 0.3f, 1f), "★ Smart Sort Everything");
        ImGui.TextWrapped("Gear → Bag 1 · Food/Medicine → Bag 2 · Crafting Materials → Bag 3 · Crystals/Materia → Bag 4. Anything that doesn't match stays where it is.");

        if (queueBusy) ImGui.BeginDisabled();
        var smartLabel = Config.RequireConfirmation && !confirmingSmartSort ? "Smart Sort Everything..." : "Confirm Smart Sort";
        if (ImGui.Button($"{smartLabel}##smartsort", new Vector2(220, 30)))
        {
            if (Config.RequireConfirmation && !confirmingSmartSort)
            {
                confirmingSmartSort = true;
                confirmingPreset = null;
            }
            else
            {
                plugin.RunSmartSort();
                confirmingSmartSort = false;
            }
        }
        if (queueBusy) ImGui.EndDisabled();

        if (confirmingSmartSort)
        {
            ImGui.SameLine();
            if (ImGui.Button("Cancel##cancelsmart")) confirmingSmartSort = false;
            ImGui.TextColored(new Vector4(1f, 0.85f, 0.3f, 1f), "Click 'Confirm Smart Sort' to begin.");
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ── Individual presets ──────────────────────────────────────────────
        ImGui.TextUnformatted("Targeted presets:");
        ImGui.Spacing();

        foreach (var preset in QuickSortPresets.All)
        {
            ImGui.PushID($"preset_{preset.Name}");

            if (queueBusy) ImGui.BeginDisabled();
            var label = (Config.RequireConfirmation && confirmingPreset == preset) ? "Confirm" : "Run";
            if (ImGui.Button($"{label}##run", new Vector2(80, 0)))
            {
                if (Config.RequireConfirmation && confirmingPreset != preset)
                {
                    confirmingPreset = preset;
                    confirmingSmartSort = false;
                }
                else
                {
                    plugin.RunPreset(preset);
                    confirmingPreset = null;
                }
            }
            if (queueBusy) ImGui.EndDisabled();

            ImGui.SameLine();
            ImGui.TextUnformatted(preset.Name);
            if (confirmingPreset == preset)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("Cancel##cancelp")) confirmingPreset = null;
            }
            ImGui.Indent(90);
            ImGui.TextDisabled(preset.Description);
            ImGui.Unindent(90);
            ImGui.Spacing();

            ImGui.PopID();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ── Advanced Presets ────────────────────────────────────────────────
        ImGui.TextUnformatted("Advanced presets:");
        ImGui.Spacing();

        var presets = new (string Name, string Desc, System.Action RunAction)[]
        {
            ("The Gatherer", "Crystals/Materia prioritized, materials grouped, rest scattered.", () => plugin.RunGathererSort()),
            ("The Raider", "High-level gear + raid consumables for combat readiness.", () => plugin.RunRaiderSort()),
            ("The Hoarder", "Group by rarity: junk (white) obvious and easy to discard.", () => plugin.RunHoarderSort()),
        };

        foreach (var (name, desc, action) in presets)
        {
            ImGui.PushID($"adv_{name}");

            if (queueBusy) ImGui.BeginDisabled();
            if (ImGui.Button($"Run##adv_{name}", new Vector2(80, 0)))
            {
                action();
            }
            if (queueBusy) ImGui.EndDisabled();

            ImGui.SameLine();
            ImGui.TextUnformatted(name);
            ImGui.Indent(90);
            ImGui.TextDisabled(desc);
            ImGui.Unindent(90);
            ImGui.Spacing();

            ImGui.PopID();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ── Delete Junk & Undo ──────────────────────────────────────────────
        if (ImGui.Button("Delete All Junk##deljunk", new Vector2(150, 0)))
        {
            pendingJunkDiscards = plugin.GetJunkItems();
            openJunkConfirmPopup = true;
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.Text("Discard all white (vendor trash) items.");
            ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(1, 0, 0, 1));
            ImGui.Text("DELETES ALL ITEMS IN YOUR INVENTORY THAT HAVE A VENDOR PRICE UNDER \"MAX VENDOR PRICE FOR JUNK\" IN SETTINGS...");
            ImGui.Text("(Note: Currently an experimental feature and may fail, use at own risk.)");
            ImGui.PopStyleColor();
            ImGui.EndTooltip();
        }

        ImGui.SameLine();
        if (plugin.CanUndo)
        {
            if (ImGui.Button("Undo Last Sort##undo", new Vector2(150, 0)))
            {
                plugin.UndoLastSort();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Reverse all moves from the last sort.");
        }
        else
        {
            ImGui.BeginDisabled();
            ImGui.Button("Undo Last Sort##undo_dis", new Vector2(150, 0));
            ImGui.EndDisabled();
        }

        ImGui.Spacing();
        ImGui.Separator();

        if (queueBusy)
        {
            if (ImGui.Button("Stop Sort##stopquick"))
                plugin.StopSort();
        }

        ImGui.Spacing();
        ImGui.Separator();
    }

    // ─── Tab: Rules ──────────────────────────────────────────────────────────

    private void DrawRulesTab()
    {
        ImGui.TextWrapped("Rules are evaluated top-to-bottom. The first rule whose filters match an item wins. Use the arrows to reorder.");
        ImGui.Spacing();

        if (ImGui.Button("Add Rule"))
        {
            Config.Rules.Add(new SortRule { Name = $"Rule {Config.Rules.Count + 1}" });
            Config.Save();
        }
        ImGui.SameLine();
        if (ImGui.Button("Add Common Presets"))
        {
            AddPresetRules();
            Config.Save();
        }

        ImGui.Spacing();

        for (var i = 0; i < Config.Rules.Count; i++)
        {
            var rule = Config.Rules[i];
            ImGui.PushID($"rule_{i}");
            ImGui.PushStyleColor(ImGuiCol.Header, rule.GetColor() * new Vector4(1, 1, 1, 0.35f));

            var header = $"{(rule.Enabled ? "[on] " : "[off] ")}{rule.Name} -> {DescribeTarget(rule)}";
            var open = ImGui.CollapsingHeader($"{header}##header_{i}");
            ImGui.PopStyleColor();

            if (open)
            {
                DrawRuleEditor(rule, i);
            }

            ImGui.PopID();
        }
    }

    private static string DescribeTarget(SortRule rule)
    {
        return rule.Target switch
        {
            SortTarget.SpecificBag => $"Bag {rule.TargetBagIndex + 1}",
            _ => "(classify only)",
        };
    }

    private void DrawRuleEditor(SortRule rule, int index)
    {
        // Header row: name, enabled, reorder, delete
        var name = rule.Name;
        ImGui.SetNextItemWidth(200);
        if (ImGui.InputText("Name##name", ref name, 64))
        {
            rule.Name = name;
            Config.Save();
        }
        ImGui.SameLine();
        var enabled = rule.Enabled;
        if (ImGui.Checkbox("Enabled", ref enabled))
        {
            rule.Enabled = enabled;
            Config.Save();
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("^") && index > 0)
        {
            (Config.Rules[index - 1], Config.Rules[index]) = (Config.Rules[index], Config.Rules[index - 1]);
            Config.Save();
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("v") && index < Config.Rules.Count - 1)
        {
            (Config.Rules[index + 1], Config.Rules[index]) = (Config.Rules[index], Config.Rules[index + 1]);
            Config.Save();
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Delete"))
        {
            Config.Rules.RemoveAt(index);
            Config.Save();
            return;
        }

        var color = new Vector3(rule.DisplayColorR, rule.DisplayColorG, rule.DisplayColorB);
        if (ImGui.ColorEdit3("Color", ref color, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoLabel))
        {
            rule.DisplayColorR = color.X;
            rule.DisplayColorG = color.Y;
            rule.DisplayColorB = color.Z;
            Config.Save();
        }
        ImGui.SameLine();
        ImGui.TextUnformatted("Accent");

        ImGui.Separator();

        // Target
        ImGui.TextUnformatted("Destination");
        var targetIdx = (int)rule.Target;
        if (ImGui.Combo("Target##target", ref targetIdx, "Any free slot (classify only)\0Specific bag\0"))
        {
            rule.Target = (SortTarget)targetIdx;
            Config.Save();
        }
        if (rule.Target == SortTarget.SpecificBag)
        {
            ImGui.SameLine();
            var bagIdx = rule.TargetBagIndex;
            ImGui.SetNextItemWidth(120);
            if (ImGui.Combo("Bag##bag", ref bagIdx, "Bag 1\0Bag 2\0Bag 3\0Bag 4\0"))
            {
                rule.TargetBagIndex = bagIdx;
                Config.Save();
            }
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Filters (all selected criteria must match — empty = any)");
        ImGui.Spacing();

        DrawCategoryFilter(rule);
        DrawRarityFilter(rule);
        DrawLevelFilters(rule);
        DrawJobFilter(rule);
        DrawFlagsFilter(rule);
        DrawNameFilter(rule);
        DrawPriceFilter(rule);
        DrawIdListFilters(rule);

        ImGui.Spacing();
        ImGui.Separator();
    }

    private void DrawCategoryFilter(SortRule rule)
    {
        if (!ImGui.TreeNode("Item Category##cat")) return;

        ImGui.SetNextItemWidth(220);
        ImGui.InputTextWithHint("##catfilter", "Search categories", ref uiCategoryFilter, 64);

        var pending = uiCategoryCache
            .Where(c => string.IsNullOrEmpty(uiCategoryFilter)
                        || c.Name.Contains(uiCategoryFilter, StringComparison.OrdinalIgnoreCase))
            .Take(80);

        ImGui.BeginChild("##catlist", new Vector2(0, 160), true);
        foreach (var (rowId, catName) in pending)
        {
            var on = rule.ItemUICategories.Contains(rowId);
            if (ImGui.Checkbox($"{catName}##c{rowId}", ref on))
            {
                if (on) rule.ItemUICategories.Add(rowId);
                else rule.ItemUICategories.Remove(rowId);
                Config.Save();
            }
        }
        ImGui.EndChild();

        if (rule.ItemUICategories.Count > 0)
        {
            ImGui.TextDisabled($"Selected: {rule.ItemUICategories.Count}");
            ImGui.SameLine();
            if (ImGui.SmallButton("Clear##clearcat"))
            {
                rule.ItemUICategories.Clear();
                Config.Save();
            }
        }
        ImGui.TreePop();
    }

    private void DrawRarityFilter(SortRule rule)
    {
        if (!ImGui.TreeNode("Rarity##rarity")) return;
        foreach (var (label, val) in RarityChoices)
        {
            var on = rule.Rarities.Contains(val);
            if (ImGui.Checkbox($"{label}##rar{val}", ref on))
            {
                if (on) rule.Rarities.Add(val);
                else rule.Rarities.Remove(val);
                Config.Save();
            }
            ImGui.SameLine();
        }
        ImGui.NewLine();
        ImGui.TreePop();
    }

    private void DrawLevelFilters(SortRule rule)
    {
        if (!ImGui.TreeNode("Equip Level / Item Level##levels")) return;

        var minE = rule.MinEquipLevel;
        var maxE = rule.MaxEquipLevel;
        ImGui.SetNextItemWidth(80);
        if (ImGui.InputInt("Min eqp lvl##minel", ref minE)) { rule.MinEquipLevel = Math.Max(0, minE); Config.Save(); }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(80);
        if (ImGui.InputInt("Max eqp lvl##maxel", ref maxE)) { rule.MaxEquipLevel = Math.Max(0, maxE); Config.Save(); }

        var minI = rule.MinItemLevel;
        var maxI = rule.MaxItemLevel;
        ImGui.SetNextItemWidth(80);
        if (ImGui.InputInt("Min ilvl##minil", ref minI)) { rule.MinItemLevel = Math.Max(0, minI); Config.Save(); }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(80);
        if (ImGui.InputInt("Max ilvl##maxil", ref maxI)) { rule.MaxItemLevel = Math.Max(0, maxI); Config.Save(); }

        ImGui.TextDisabled("(0 = unlimited / off)");
        ImGui.TreePop();
    }

    private void DrawJobFilter(SortRule rule)
    {
        if (!ImGui.TreeNode("Class / Job##jobs")) return;

        string? lastGroup = null;
        var col = 0;
        foreach (var (abbr, group) in AllJobs)
        {
            if (group != lastGroup)
            {
                if (lastGroup != null) ImGui.NewLine();
                ImGui.TextDisabled(group + ":");
                ImGui.SameLine();
                lastGroup = group;
                col = 0;
            }
            var on = rule.ClassJobs.Contains(abbr);
            if (ImGui.Checkbox($"{abbr}##j{abbr}", ref on))
            {
                if (on) rule.ClassJobs.Add(abbr);
                else rule.ClassJobs.Remove(abbr);
                Config.Save();
            }
            ImGui.SameLine();
            col++;
            if (col % 8 == 0) { ImGui.NewLine(); ImGui.Dummy(new Vector2(80, 0)); ImGui.SameLine(); }
        }
        ImGui.NewLine();
        ImGui.TreePop();
    }

    private void DrawFlagsFilter(SortRule rule)
    {
        if (!ImGui.TreeNode("Flags (HQ / Tradeable / Collectable / Stackable / Equippable)##flags")) return;

        var hq = (int)rule.HQ;
        ImGui.SetNextItemWidth(160);
        if (ImGui.Combo("HQ##hq", ref hq, "Any\0HQ only\0NQ only\0")) { rule.HQ = (HQMatch)hq; Config.Save(); }

        DrawBoolMatch("Untradeable", () => rule.Untradeable, x => rule.Untradeable = x);
        DrawBoolMatch("Collectable", () => rule.Collectable, x => rule.Collectable = x);
        DrawBoolMatch("Stackable", () => rule.Stackable, x => rule.Stackable = x);
        DrawBoolMatch("Equippable", () => rule.Equippable, x => rule.Equippable = x);

        ImGui.TreePop();
    }

    private void DrawBoolMatch(string label, Func<BoolMatch> get, Action<BoolMatch> set)
    {
        var v = (int)get();
        ImGui.SetNextItemWidth(160);
        if (ImGui.Combo($"{label}##{label}", ref v, "Any\0Yes\0No\0"))
        {
            set((BoolMatch)v);
            Config.Save();
        }
    }

    private void DrawNameFilter(SortRule rule)
    {
        if (!ImGui.TreeNode("Name##name")) return;
        var s = rule.NameContains;
        ImGui.SetNextItemWidth(280);
        if (ImGui.InputTextWithHint("Name contains##nc", "case-insensitive substring", ref s, 64))
        {
            rule.NameContains = s;
            Config.Save();
        }
        ImGui.TreePop();
    }

    private void DrawPriceFilter(SortRule rule)
    {
        if (!ImGui.TreeNode("Vendor Price##price")) return;
        var min = (int)rule.MinVendorPrice;
        var max = (int)rule.MaxVendorPrice;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("Min gil##minp", ref min)) { rule.MinVendorPrice = (uint)Math.Max(0, min); Config.Save(); }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("Max gil##maxp", ref max)) { rule.MaxVendorPrice = (uint)Math.Max(0, max); Config.Save(); }
        ImGui.TextDisabled("(0 = unlimited / off)");
        ImGui.TreePop();
    }

    private void DrawIdListFilters(SortRule rule)
    {
        if (!ImGui.TreeNode("Item ID Whitelist / Blacklist##ids")) return;
        DrawIdList("Whitelist", rule.ItemIdWhitelist);
        DrawIdList("Blacklist", rule.ItemIdBlacklist);
        ImGui.TextDisabled("Tip: comma-separated item IDs. Whitelist forces only those IDs to match; Blacklist excludes them.");
        ImGui.TreePop();
    }

    private void DrawIdList(string label, List<uint> list)
    {
        var current = string.Join(",", list);
        ImGui.SetNextItemWidth(360);
        if (ImGui.InputText($"{label}##idlist_{label}", ref current, 1024))
        {
            list.Clear();
            foreach (var token in current.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (uint.TryParse(token, out var id)) list.Add(id);
            }
            Config.Save();
        }
    }

    // ─── Tab: Run / Preview ─────────────────────────────────────────────────

    private void DrawRunTab()
    {
        if (ImGui.Button("Preview Matches"))
        {
            previewItems = plugin.InventoryService.ScanBags(GetBagFlags());
            statusMessage = $"Scanned {previewItems.Count} item(s) across selected bags.";
        }

        ImGui.SameLine();
        var canSort = !plugin.IsSortQueueBusy && Config.Rules.Any(r => r.Enabled && r.Target == SortTarget.SpecificBag);
        if (!canSort) ImGui.BeginDisabled();

        if (ImGui.Button(Config.RequireConfirmation && !confirmingSort ? "Sort Now..." : "Confirm Sort Now"))
        {
            if (Config.RequireConfirmation && !confirmingSort)
            {
                confirmingSort = true;
            }
            else
            {
                plugin.RunAllRules();
                confirmingSort = false;
            }
        }
        if (!canSort) ImGui.EndDisabled();

        if (confirmingSort)
        {
            ImGui.SameLine();
            if (ImGui.Button("Cancel##cancelconfirm")) confirmingSort = false;
            ImGui.TextColored(new Vector4(1f, 0.85f, 0.3f, 1f), "Click 'Confirm Sort Now' to begin moving items.");
        }

        if (plugin.IsSortQueueBusy)
        {
            ImGui.SameLine();
            if (ImGui.Button("Stop Sort"))
                plugin.StopSort();
        }

        ImGui.Separator();

        if (previewItems.Count == 0)
        {
            ImGui.TextDisabled("Click 'Preview Matches' to see how each item maps to your rules.");
            return;
        }

        if (ImGui.BeginTable("##preview", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, new Vector2(0, 360)))
        {
            ImGui.TableSetupColumn("Item");
            ImGui.TableSetupColumn("Source");
            ImGui.TableSetupColumn("Rule");
            ImGui.TableSetupColumn("Destination");
            ImGui.TableSetupColumn("Action");
            ImGui.TableHeadersRow();

            foreach (var item in previewItems)
            {
                var matched = Config.Rules.FirstOrDefault(r => RuleMatcher.Matches(r, item));
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(item.Name + (item.IsHQ ? " [HQ]" : string.Empty));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{ContainerLabel(item.Container)}#{item.Slot + 1}");
                ImGui.TableNextColumn();
                if (matched != null)
                {
                    ImGui.TextColored(matched.GetColor(), matched.Name);
                }
                else
                {
                    ImGui.TextDisabled("(none)");
                }
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(matched != null ? DescribeTarget(matched) : "—");
                ImGui.TableNextColumn();
                if (matched != null && matched.Target == SortTarget.SpecificBag)
                {
                    var destBag = InventoryService.PlayerBags[Math.Clamp(matched.TargetBagIndex, 0, 3)];
                    if (item.Container == destBag) ImGui.TextDisabled("in place");
                    else ImGui.TextColored(new Vector4(0.3f, 1f, 0.6f, 1f), "will move");
                }
                else
                {
                    ImGui.TextDisabled("classify");
                }
            }

            ImGui.EndTable();
        }
    }

    private static string ContainerLabel(FFXIVClientStructs.FFXIV.Client.Game.InventoryType t) => t switch
    {
        FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Inventory1 => "Bag1",
        FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Inventory2 => "Bag2",
        FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Inventory3 => "Bag3",
        FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Inventory4 => "Bag4",
        _ => t.ToString(),
    };

    // ─── Tab: Settings ──────────────────────────────────────────────────────

    private void DrawSettingsTab()
    {
        ImGui.TextUnformatted("Bags considered when scanning and as move destinations:");
        DrawBagToggle("Bag 1", c => c.IncludeBag1, (c, v) => c.IncludeBag1 = v);
        DrawBagToggle("Bag 2", c => c.IncludeBag2, (c, v) => c.IncludeBag2 = v);
        DrawBagToggle("Bag 3", c => c.IncludeBag3, (c, v) => c.IncludeBag3 = v);
        DrawBagToggle("Bag 4", c => c.IncludeBag4, (c, v) => c.IncludeBag4 = v);

        ImGui.Separator();
        var confirm = Config.RequireConfirmation;
        if (ImGui.Checkbox("Require confirmation before sorting", ref confirm))
        {
            Config.RequireConfirmation = confirm;
            Config.Save();
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Move pacing (random delay per item, in ms — keeps the game happy):");
        var min = Config.MoveDelayMinMs;
        var max = Config.MoveDelayMaxMs;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("Min ms##mindelay", ref min)) { Config.MoveDelayMinMs = Math.Max(20, min); Config.Save(); }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("Max ms##maxdelay", ref max)) { Config.MoveDelayMaxMs = Math.Max(Config.MoveDelayMinMs + 1, max); Config.Save(); }

        ImGui.Separator();
        ImGui.TextUnformatted("Inventory overlay buttons");
        var showOverlay = Config.ShowInventoryOverlay;
        if (ImGui.Checkbox("Show floating buttons above the inventory window", ref showOverlay))
        {
            Config.ShowInventoryOverlay = showOverlay;
            Config.Save();
        }
        ImGui.TextDisabled("When enabled, a set of sorting buttons appears above the inventory addon while it is open.");

        ImGui.Separator();
        ImGui.TextUnformatted("Inventory Overlay Enhancements");
        
        var showVisualOverlay = Config.ShowVisualZoneOverlay;
        if (ImGui.Checkbox("Draw Layout Zones over the Inventory window", ref showVisualOverlay))
        {
            Config.ShowVisualZoneOverlay = showVisualOverlay;
            Config.Save();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Draws colored boxes directly over the native in-game inventory slots to represent your painted zones.");

        if (Config.ShowVisualZoneOverlay)
        {
            var overlayOpacity = Config.VisualZoneOverlayOpacity;
            ImGui.SetNextItemWidth(200f);
            if (ImGui.SliderFloat("Zone Overlay Opacity", ref overlayOpacity, 0.05f, 1f, "%.2f"))
            {
                Config.VisualZoneOverlayOpacity = overlayOpacity;
                Config.Save();
            }
        }
        
        var showVisualNumbers = Config.ShowVisualZoneNumbers;
        if (ImGui.Checkbox("Show Slot Numbers on overlay", ref showVisualNumbers))
        {
            Config.ShowVisualZoneNumbers = showVisualNumbers;
            Config.Save();
        }

        var applyZonesMerge = Config.ApplyZonesAutoMerge;
        if (ImGui.Checkbox("Enable Auto-Merge for Apply Zones", ref applyZonesMerge))
        {
            Config.ApplyZonesAutoMerge = applyZonesMerge;
            Config.Save();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Automatically runs Merge Stacks before arranging items to your Painted Layout Zones to prevent duplicates occupying multiple painted slots.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextUnformatted("Junk Filtering Logic");
        
                var excGear = Config.ExcludeGearFromJunk;
        if (ImGui.Checkbox("Exclude Gear from Junk", ref excGear))
        {
            Config.ExcludeGearFromJunk = excGear;
            Config.Save();
        }
        
        var excConsumables = Config.ExcludeConsumablesFromJunk;
        if (ImGui.Checkbox("Exclude Potions/Food from Junk", ref excConsumables))
        {
            Config.ExcludeConsumablesFromJunk = excConsumables;
            Config.Save();
        }

        var exclusion = Config.ExcludeCraftingFromJunk;
        if (ImGui.Checkbox("Exclude stackable crafting materials from Junk", ref exclusion))
        {
            Config.ExcludeCraftingFromJunk = exclusion;
            Config.Save();
        }
        
        var junkPrice = Config.MaxJunkVendorPrice;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("Max Vendor Price for Junk##junkPrice", ref junkPrice))
        {
            Config.MaxJunkVendorPrice = Math.Max(0, junkPrice);
            Config.Save();
        }
    }

    private void DrawBagToggle(string label, Func<Configuration, bool> get, Action<Configuration, bool> set)
    {
        var v = get(Config);
        if (ImGui.Checkbox(label + "##" + label, ref v))
        {
            set(Config, v);
            Config.Save();
        }
    }

    // ─── Tab: Advanced ──────────────────────────────────────────────────────

        private List<InventoryItemInfo>? pendingJunkDiscards = null;
    private bool openJunkConfirmPopup = false;

    private string naturalLanguageSearchBox = string.Empty;
    private List<InventoryItemInfo> searchResults = new();

    private void DrawAdvancedTab()
    {
        ImGui.TextWrapped("Advanced tools: Natural language search, layout mirroring, zone management.");
        ImGui.Spacing();

        var queueBusy = plugin.IsSortQueueBusy;

        // ── Natural Language Search ─────────────────────────────────────────
        ImGui.TextUnformatted("Natural Language Search:");
        ImGui.TextDisabled("Type: combat, heal, magic, tank, craft, gather, hq, junk, consumable, materia, crystal, spiritbond, etc.");
        
        ImGui.SetNextItemWidth(250);
        if (ImGui.InputText("##nlsearch", ref naturalLanguageSearchBox, 100))
        {
            if (!string.IsNullOrWhiteSpace(naturalLanguageSearchBox))
                searchResults = plugin.SearchByKeyword(naturalLanguageSearchBox);
            else
                searchResults.Clear();
        }

        if (searchResults.Count > 0)
        {
            ImGui.TextColored(new Vector4(0.4f, 0.8f, 1f, 1f), $"Found {searchResults.Count} item(s):");
            if (ImGui.BeginChild("##searchresults", new Vector2(-1, 150)))
            {
                foreach (var item in searchResults.Take(50))
                {
                    var color = item.Rarity switch
                    {
                        4 => new Vector4(1f, 0.5f, 0f, 1f), // Purple
                        3 => new Vector4(0.2f, 0.6f, 1f, 1f), // Blue
                        2 => new Vector4(0.2f, 1f, 0.2f, 1f), // Green
                        _ => new Vector4(1f, 1f, 1f, 1f)      // White
                    };
                    ImGui.TextColored(color, $"{item.Name} (x{item.StackCount}) - {item.Container}[{item.Slot}]");
                }
                ImGui.EndChild();
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ── Layout Snapshot & Sync ──────────────────────────────────────────
        ImGui.TextUnformatted("Layout Mirror:");
        ImGui.TextDisabled("Capture a bag's layout as a template, then apply it to another bag.");

        var bags = new (string Label, InventoryType Type)[]
        {
            ("Bag 1", InventoryType.Inventory1),
            ("Bag 2", InventoryType.Inventory2),
            ("Bag 3", InventoryType.Inventory3),
            ("Bag 4", InventoryType.Inventory4),
        };

        ImGui.Combo("##sourcebag", ref selectedBag, bags.Select(b => b.Label).ToArray(), bags.Length);

        if (queueBusy) ImGui.BeginDisabled();
        if (ImGui.Button("Snapshot this Bag"))
        {
            var layout = plugin.SnapshotBagLayout(bags[selectedBag].Type);
            ImGui.SetClipboardText(System.Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(string.Join(",", layout))));
            statusMessage = $"Snapshot saved to clipboard for {bags[selectedBag].Label}.";
        }
        if (queueBusy) ImGui.EndDisabled();

        ImGui.TextDisabled("(Snapshot saved to clipboard - paste the code into another bag's 'Apply Layout' field)");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ── Quick Operations ────────────────────────────────────────────────
        ImGui.TextUnformatted("One-Click Operations:");

        if (queueBusy) ImGui.BeginDisabled();
        if (ImGui.Button("Extract Materia##adv_mat", new Vector2(120, 0)))
        {
            plugin.ExtractMateria();
        }
        if (queueBusy) ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.TextDisabled("Move 100% spiritbond gear to Bag 1");

        if (queueBusy) ImGui.BeginDisabled();
        if (ImGui.Button("Merge Stacks##adv_merge", new Vector2(120, 0)))
        {
            plugin.MergeStacks();
        }
        if (queueBusy) ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.TextDisabled("Consolidate duplicate items");

        if (queueBusy) ImGui.BeginDisabled();
        if (ImGui.Button("Group Vendor Trash##adv_trash", new Vector2(120, 0)))
        {
            plugin.GroupVendorTrash();
        }
        if (queueBusy) ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.TextDisabled("Move all grey items to Bag 4");
    }

    // ─── Tab: Help ──────────────────────────────────────────────────────────

        
    private int activeZoneTagIndex = 0;
    private readonly string[] zoneTags = new[] { "None", "Gear", "Materia", "Consumables", "Crafting", "Gathering", "Crystals", "Junk" };
    private readonly System.Numerics.Vector4[] zoneColors = new[] {
        new System.Numerics.Vector4(0.2f, 0.2f, 0.2f, 1f), // None
        new System.Numerics.Vector4(0.2f, 0.6f, 1.0f, 1f), // Gear
        new System.Numerics.Vector4(0.8f, 0.2f, 0.8f, 1f), // Materia
        new System.Numerics.Vector4(0.4f, 0.9f, 0.4f, 1f), // Consumables
        new System.Numerics.Vector4(0.9f, 0.6f, 0.2f, 1f), // Crafting
        new System.Numerics.Vector4(0.9f, 0.8f, 0.2f, 1f), // Gathering
        new System.Numerics.Vector4(0.4f, 0.8f, 0.9f, 1f), // Crystals
        new System.Numerics.Vector4(0.5f, 0.5f, 0.5f, 1f), // Junk
    };

    private void DrawVisualZonesTab()
    {
        ImGui.TextWrapped("Visual Layout Zones let you assign specific categories to specific slots in your bags. Items will be automatically routed and sorted into these designated areas.");
        ImGui.Spacing();

        if (ImGui.Button("Apply Visual Zones Now"))
        {
            plugin.ApplyVisualZones();
        }

        ImGui.Separator();
        
        ImGui.Text("Active Paintbrush:");
        for (int i = 0; i < zoneTags.Length; i++)
        {
            if (i > 0 && i % 4 != 0) ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Button, activeZoneTagIndex == i ? zoneColors[i] : zoneColors[i] * 0.5f);
            if (ImGui.Button($"{zoneTags[i]}##brush{i}"))
            {
                activeZoneTagIndex = i;
            }
            ImGui.PopStyleColor();
            
            if (ImGui.IsItemHovered())
            {
                var tooltip = zoneTags[i] switch
                {
                    "Gear" => "Includes all equippable weapons and armor.",
                    "Materia" => "Includes all combat, crafting, and gathering materia.",
                    "Consumables" => "Includes meals (food) and medicine (potions/tinctures).",
                    "Crafting" => "Includes all stackable materials that aren't gear or consumables.",
                    "Gathering" => "Includes fish, bait, and raw gathering node materials.",
                    "Crystals" => "Includes elemental shards, crystals, and clusters.",
                    "Junk" => "Low-value items that can be safely vendored.",
                    "None" => "Erase layout zone for this slot.",
                    _ => ""
                };
                ImGui.SetTooltip(tooltip);
            }
        }

        ImGui.Spacing();
        ImGui.Separator();

        if (ImGui.BeginTabBar("##ZoneBagsTabBar"))
        {
            for (int b = 0; b < 4; b++)
            {
                if (ImGui.BeginTabItem($"Bag {b + 1}##ZoneBag_{b}"))
                {
                    DrawZoneGrid(b);
                    ImGui.EndTabItem();
                }
            }
            ImGui.EndTabBar();
        }
    }

    private void DrawZoneGrid(int bagIndex)
    {
        int offset = bagIndex * 35;
        ImGui.Text("Click or drag across slots to paint them with the active tag.");
        ImGui.Spacing();
        
        if (ImGui.BeginTable($"##ZoneTable_{bagIndex}", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.SizingFixedFit))
        {
            for (int r = 0; r < 7; r++)
            {
                ImGui.TableNextRow();
                for (int c = 0; c < 5; c++)
                {
                    ImGui.TableNextColumn();
                    int slotIndex = r * 5 + c;
                    int globalIndex = offset + slotIndex;
                    string currentTag = Config.VisualZoneLayout[globalIndex];
                    int tagIdx = Array.IndexOf(zoneTags, currentTag);
                    if (tagIdx < 0) tagIdx = 0;

                    ImGui.PushID($"zoneBtn_{globalIndex}");
                    ImGui.PushStyleColor(ImGuiCol.Button, zoneColors[tagIdx]);
                    
                    var title = string.IsNullOrEmpty(currentTag) || currentTag == "None" ? $"Slot {slotIndex+1}" : currentTag;
                    if (Config.ShowVisualZoneNumbers && !title.StartsWith("Slot")) title += $"\n({slotIndex+1})";
                    ImGui.Button(title, new System.Numerics.Vector2(90, 40));
                    
                    if (ImGui.IsItemHovered() && ImGui.IsMouseDown(ImGuiMouseButton.Left))
                    {
                        var newTag = zoneTags[activeZoneTagIndex];
                        if (Config.VisualZoneLayout[globalIndex] != newTag)
                        {
                            Config.VisualZoneLayout[globalIndex] = newTag;
                            Config.Save();
                        }
                    }

                    ImGui.PopStyleColor();
                    ImGui.PopID();
                }
            }
            ImGui.EndTable();
        }
    }

    private void DrawHelpTab()
    {
        ImGui.TextWrapped("Bag Assistant gives you two ways to organise your inventory.");
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(1f, 0.85f, 0.3f, 1f), "Quick Sort (recommended)");
        ImGui.TextWrapped("One-click presets. 'Smart Sort Everything' covers the common case: gear -> Bag 1, food/medicine -> Bag 2, crafting materials -> Bag 3, crystals/materia -> Bag 4. Or pick a single targeted preset (e.g. 'HQ Gear -> Bag 1').");
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.7f, 0.9f, 1f, 1f), "Custom Rules (advanced)");
        ImGui.TextWrapped("Build your own rule list with detailed filters (category, rarity, level, ilvl, job, HQ, name, vendor price, item ID, etc.). Rules are evaluated top-to-bottom, first match wins.");
        ImGui.Spacing();
        ImGui.TextWrapped("Commands:");
        ImGui.BulletText("/bagassistant or /ba — open this window.");
    }

    private bool[] GetBagFlags() => new[] { Config.IncludeBag1, Config.IncludeBag2, Config.IncludeBag3, Config.IncludeBag4 };

    // ─── Presets ────────────────────────────────────────────────────────────

    private void AddPresetRules()
    {
        Config.Rules.Add(new SortRule
        {
            Name = "HQ Gear -> Bag 1",
            Target = SortTarget.SpecificBag,
            TargetBagIndex = 0,
            HQ = HQMatch.HQOnly,
            Equippable = BoolMatch.Yes,
            DisplayColorR = 1f, DisplayColorG = 0.8f, DisplayColorB = 0.2f,
        });
        Config.Rules.Add(new SortRule
        {
            Name = "Food / Medicine -> Bag 3",
            Target = SortTarget.SpecificBag,
            TargetBagIndex = 2,
            NameContains = string.Empty,
            // ItemUICategory rows: 46 = Meal, 44 = Medicine
            ItemUICategories = new() { 46, 44 },
            DisplayColorR = 0.4f, DisplayColorG = 1f, DisplayColorB = 0.6f,
        });
        Config.Rules.Add(new SortRule
        {
            Name = "Materia / Crystals -> Bag 4",
            Target = SortTarget.SpecificBag,
            TargetBagIndex = 3,
            // 59 = Materia, 63 = Crystal (Lumina rows; close enough — user can tweak)
            ItemUICategories = new() { 59, 63 },
            DisplayColorR = 0.7f, DisplayColorG = 0.5f, DisplayColorB = 1f,
        });
    }
}
