using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using QuestNav.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;

namespace QuestNav.Windows
{
    public sealed class QuestNavWindow : Window, IDisposable
    {
        private readonly QuestService questService;
        private readonly IClientState clientState;
        private readonly IGameGui gameGui;
        private readonly Configuration config;
        private readonly ArrowOverlayWindow arrowOverlay;

        private List<QuestEntry> quests = [];
        private string statusMessage = string.Empty;
        private DateTime lastRefresh = DateTime.MinValue;
        private const float AutoRefreshSeconds = 30f;
        private bool showSettings = false;
        private int activeFlagQuestId = 0;

        // Maps JournalGenre name keywords → badge label + colour
        private static readonly (string Keyword, string Badge, Vector4 Color)[] TypeRules =
        [
            ("Main",  "MSQ",   new Vector4(1.00f, 0.85f, 0.10f, 1f)),
            ("Class", "Class", new Vector4(0.40f, 0.90f, 0.55f, 1f)),
            ("Job",   "Job",   new Vector4(0.40f, 0.90f, 0.55f, 1f)),
            ("Side",  "Side",  new Vector4(0.80f, 0.80f, 0.80f, 1f)),
            ("Clan",  "Clan",  new Vector4(0.70f, 0.55f, 1.00f, 1f)),
            ("Grand", "GC",    new Vector4(0.65f, 0.85f, 1.00f, 1f)),
        ];

        public QuestNavWindow(
            QuestService questService,
            IClientState clientState,
            IGameGui gameGui,
            Configuration config,
            ArrowOverlayWindow arrowOverlay)
            : base("QuestNav##window")
        {
            this.questService   = questService;
            this.clientState    = clientState;
            this.gameGui        = gameGui;
            this.config         = config;
            this.arrowOverlay   = arrowOverlay;

            SizeConstraints = new WindowSizeConstraints
            {
                MinimumSize = new Vector2(280, 110),
                MaximumSize = new Vector2(900, 800),
            };
            Size = new Vector2(360, 160);
            SizeCondition = ImGuiCond.FirstUseEver;
        }

        public void Dispose() { }

        public void TriggerRefresh()
        {
            Refresh();
        }

        public override void Draw()
        {
            if (!clientState.IsLoggedIn)
            {
                ImGui.TextDisabled("Log in to a character to view active quests.");
                return;
            }

            // Auto-refresh
            if (config.AutoRefresh && (DateTime.UtcNow - lastRefresh).TotalSeconds > AutoRefreshSeconds)
                Refresh();

            DrawToolbar();
            if (!showSettings)
                ImGui.Spacing();
            DrawQuestTable();

            if (!string.IsNullOrEmpty(statusMessage))
            {
                ImGui.Spacing();
                ImGui.TextColored(new Vector4(0.6f, 1f, 0.6f, 1f), statusMessage);
            }

            DrawFootnote();
        }

        private void Refresh()
        {
            quests = questService.GetActiveQuests();
            lastRefresh = DateTime.UtcNow;

            // Sort: current zone first, then alphabetically
            var currentTerr = (uint)clientState.TerritoryType;
            quests = [.. quests.OrderByDescending(q => q.TerritoryId == currentTerr)
                               .ThenBy(q => q.Name)];

            // Update arrow target if a nav quest is selected
            SyncArrowTarget();

            if (statusMessage.StartsWith("Auto"))  // don't clobber user-triggered messages
                statusMessage = string.Empty;
        }

        private void SyncArrowTarget()
        {
            if (config.NavQuestId == 0)
            {
                arrowOverlay.SetNavTarget(null);
                return;
            }
            var target = quests.FirstOrDefault(q => q.QuestId == config.NavQuestId);
            if (target != null)
                target = ResolveCurrentObjectiveTarget(target);

            arrowOverlay.SetNavTarget(target);
            if (target == null) config.NavQuestId = 0;  // quest finished/abandoned
        }

        private QuestEntry ResolveCurrentObjectiveTarget(QuestEntry quest)
        {
            if (quest.AllSteps == null || quest.AllSteps.Count == 0)
                return quest;

            // Try to find a step that has valid location data.
            QuestStep? targetStep = null;

            // First try to find a step at or just below the current sequence
            if (quest.Sequence < quest.AllSteps.Count)
            {
                targetStep = quest.AllSteps[(int)quest.Sequence];
                if (targetStep != null && !HasStepLocation(targetStep))
                    targetStep = null;
            }

            // If no exact match, try nearby steps
            if (targetStep == null)
            {
                for (int i = (int)quest.Sequence; i >= 0 && i >= (int)quest.Sequence - 2; i--)
                {
                    if (i >= 0 && i < quest.AllSteps.Count)
                    {
                        var step = quest.AllSteps[i];
                        if (step != null && HasStepLocation(step))
                        {
                            targetStep = step;
                            break;
                        }
                    }
                }
            }

            // If still no match, use any step with location
            if (targetStep == null)
            {
                targetStep = quest.AllSteps.FirstOrDefault(HasStepLocation);
            }

            if (targetStep == null)
                return quest;

            // Now try to resolve the actual objective coordinates
            var resolvedStep = ResolveObjectiveCoordinates(targetStep);

            return quest with
            {
                TerritoryId = resolvedStep.NpcTerritoryId ?? quest.TerritoryId,
                MapId = resolvedStep.NpcMapId ?? quest.MapId,
                WorldX = resolvedStep.NpcWorldX ?? quest.WorldX,
                WorldZ = resolvedStep.NpcWorldZ ?? quest.WorldZ,
                MapX = resolvedStep.NpcMapX ?? quest.MapX,
                MapY = resolvedStep.NpcMapY ?? quest.MapY,
            };
        }

        private QuestStep ResolveObjectiveCoordinates(QuestStep step)
        {
            var objectives = step.Objectives;
            if (string.IsNullOrWhiteSpace(objectives))
                return step;

            // Try NPC resolution first (highest confidence)
            var npcName = ExtractNpcNameFromObjective(objectives);
            if (!string.IsNullOrWhiteSpace(npcName))
            {
                var npcCoords = questService.FindNpcCoordinates(npcName);
                if (npcCoords.HasValue && npcCoords.Value.WorldX.HasValue && npcCoords.Value.WorldZ.HasValue)
                {
                    return step with
                    {
                        NpcWorldX = npcCoords.Value.WorldX,
                        NpcWorldZ = npcCoords.Value.WorldZ,
                    };
                }
            }

            // Try location resolution second (medium confidence)
            var locationName = ExtractLocationNameFromObjective(objectives);
            if (!string.IsNullOrWhiteSpace(locationName))
            {
                var locationCoords = questService.FindLocationCoordinates(locationName);
                if (locationCoords.HasValue && locationCoords.Value.WorldX.HasValue && locationCoords.Value.WorldZ.HasValue)
                {
                    var updatedStep = step with
                    {
                        NpcWorldX = locationCoords.Value.WorldX,
                        NpcWorldZ = locationCoords.Value.WorldZ,
                    };

                    // Update territory/map if available
                    if (locationCoords.Value.TerritoryId.HasValue)
                        updatedStep = updatedStep with { NpcTerritoryId = locationCoords.Value.TerritoryId };
                    if (locationCoords.Value.MapId.HasValue)
                        updatedStep = updatedStep with { NpcMapId = locationCoords.Value.MapId };

                    return updatedStep;
                }
            }

            // Try quest marker resolution last (lowest confidence, depends on game state)
            var markerCoords = questService.GetQuestMarkerCoordinates();
            if (markerCoords.HasValue && markerCoords.Value.WorldX.HasValue && markerCoords.Value.WorldZ.HasValue)
            {
                return step with
                {
                    NpcWorldX = markerCoords.Value.WorldX,
                    NpcWorldZ = markerCoords.Value.WorldZ,
                };
            }

            // Fall back to original step data
            return step;
        }

        private static string? ExtractNpcNameFromObjective(string objective)
        {
            // Look for "Speak with <NPC Name>" or "Talk to <NPC Name>" pattern
            var patterns = new[] { 
                @"[Ss]peak with (.+?)(?:\.|$| in )",
                @"[Tt]alk to (.+?)(?:\.|$| in )",
                @"Meet with (.+?)(?:\.|$| in )",
                @"Return to (.+?)(?:\.|$| in )"
            };

            foreach (var pattern in patterns)
            {
                var match = Regex.Match(objective, pattern);
                if (match.Success && match.Groups.Count > 1)
                {
                    var name = match.Groups[1].Value.Trim();
                    // Filter out common words that aren't names
                    if (!string.IsNullOrWhiteSpace(name) && name.Length > 1 && 
                        !name.Equals("the") && !name.Equals("a"))
                    {
                        return name;
                    }
                }
            }

            return null;
        }

        private static string? ExtractLocationNameFromObjective(string objective)
        {
            // Look for location-based objective patterns
            var patterns = new[] 
            { 
                @"[Gg]o to (?:the )?(.+?)(?:\.|,|$)",
                @"[Hh]ead to (?:the )?(.+?)(?:\.|,|$)",
                @"[Vv]isit (?:the )?(.+?)(?:\.|,|$)",
                @"[Ii]nvestigate (?:the )?(.+?)(?:\.|,|$)",
                @"[Dd]estroy (?:the )?(.+?)(?:\.|,|$)",
                @"[Ee]xamine (?:the )?(.+?)(?:\.|,|$)",
                @"[Gg]ather (?:the )?(.+?)(?:\.|,|$)",
                @"[Ss]ubmit.*?at (?:the )?(.+?)(?:\.|,|$)",
                @"(?:the|near|at) ([A-Z][a-zA-Z\s]+)(?:\.|,|$)"
            };

            foreach (var pattern in patterns)
            {
                var match = Regex.Match(objective, pattern);
                if (match.Success && match.Groups.Count > 1)
                {
                    var location = match.Groups[1].Value.Trim();
                    // Filter out common non-location words
                    if (!string.IsNullOrWhiteSpace(location) && location.Length > 2 && 
                        !location.Equals("a") && !location.Equals("the") && 
                        !location.StartsWith("your") && !location.StartsWith("their"))
                    {
                        return location;
                    }
                }
            }

            return null;
        }

        private static bool HasStepLocation(QuestStep step)
            => step.NpcTerritoryId.HasValue && step.NpcMapId.HasValue &&
               step.NpcWorldX.HasValue && step.NpcWorldZ.HasValue &&
               step.NpcMapX.HasValue && step.NpcMapY.HasValue;

        private void DrawToolbar()
        {
            if (ImGui.Button("Refresh##refresh", new Vector2(70, 0)))
            {
                Refresh();
                statusMessage = quests.Count == 0
                    ? "No active quests."
                    : $"Loaded {quests.Count} quest(s).";
            }
            ImGui.SameLine();
            ImGui.TextDisabled($"({quests.Count})");

            ImGui.SameLine();
            float availWidth = ImGui.GetContentRegionAvail().X - 35;
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + availWidth);

            // Settings gear button (collapsible)
            if (ImGui.SmallButton("⚙##settings"))
                showSettings = !showSettings;

            if (showSettings)
            {
                ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(4f, 2f));
                ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8f, 4f));
                
                bool arrow = config.ShowArrow;
                if (ImGui.Checkbox("Arrow##arrow", ref arrow))
                {
                    config.ShowArrow = arrow;
                    config.Save();
                    SyncArrowTarget();
                }
                ImGui.SameLine();
                ImGui.SetNextItemWidth(80);
                float bgOpacity = config.ArrowBgOpacity;
                if (ImGui.SliderFloat("##opacity", ref bgOpacity, 0f, 1f, "%.0f%%"))
                {
                    config.ArrowBgOpacity = bgOpacity;
                    config.Save();
                }

                bool autoR = config.AutoRefresh;
                if (ImGui.Checkbox("Auto-refresh##autorefresh", ref autoR))
                {
                    config.AutoRefresh = autoR;
                    config.Save();
                }

                bool compact = config.CompactMode;
                if (ImGui.Checkbox("Compact##compact", ref compact))
                {
                    config.CompactMode = compact;
                    config.Save();
                }
                
                ImGui.PopStyleVar(2); // FramePadding, ItemSpacing
            }
        }

        private void DrawQuestTable()
        {
            var tableFlags = ImGuiTableFlags.Borders
                           | ImGuiTableFlags.RowBg
                           | ImGuiTableFlags.ScrollY
                           | ImGuiTableFlags.NoHostExtendX
                           | ImGuiTableFlags.SizingFixedFit;

            float footerHeight = 22f + (string.IsNullOrEmpty(statusMessage) ? 0f : 18f);
            var tableHeight = ImGui.GetContentRegionAvail().Y - footerHeight;
            if (tableHeight < 40f) tableHeight = 40f;

            // 2 columns: Quest Info | Actions
            if (!ImGui.BeginTable("##quests", 2, tableFlags, new Vector2(0, tableHeight)))
                return;

            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("Quest",     ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("##actions", ImGuiTableColumnFlags.WidthFixed, config.CompactMode ? 78f : 138f);
            ImGui.TableHeadersRow();

            if (quests.Count == 0)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextDisabled("Click Refresh to load active quests.");
            }

            var currentTerr = (uint)clientState.TerritoryType;
            var textLineHeight = ImGui.GetTextLineHeight();
            
            foreach (var quest in quests)
            {
                var objectiveTarget = ResolveCurrentObjectiveTarget(quest);

                // More compact row - just one line of content
                ImGui.TableNextRow(ImGuiTableRowFlags.None, textLineHeight + 4f);
                bool inZone = quest.TerritoryId == currentTerr;

                // ── Quest Info (condensed to one line) ────────────────────────
                ImGui.TableSetColumnIndex(0);
                ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetColumnWidth() - 8f);

                // Quest name with zone highlighting
                if (inZone)
                    ImGui.TextColored(new Vector4(0.3f, 1f, 0.4f, 1f), quest.Name);
                else
                    ImGui.TextUnformatted(quest.Name);

                // Zone/aetheryte on same line, small text
                ImGui.SameLine();
                ImGui.TextDisabled(" · ");
                ImGui.SameLine();
                ImGui.TextDisabled(quest.ZoneName);

                if (quest.NearestAetheryteId.HasValue)
                {
                    ImGui.SameLine();
                    ImGui.TextDisabled($" ({(quest.GilCost == 0 ? "Free" : $"{quest.GilCost}g")})");
                }

                ImGui.PopTextWrapPos();

                // Hover tooltip with full details
                if (ImGui.IsItemHovered())
                {
                    ImGui.BeginTooltip();
                    ImGui.TextColored(new Vector4(0.9f, 0.9f, 0.9f, 1f), quest.Name);
                    ImGui.TextDisabled($"Zone: {quest.ZoneName}");
                    if (!config.CompactMode)
                        ImGui.TextDisabled($"Step: {quest.Sequence}");
                    if (quest.NearestAetheryteId.HasValue)
                        ImGui.TextDisabled($"Teleport: {quest.AetheryteName} ({(quest.GilCost == 0 ? "Free" : $"{quest.GilCost}g")})");
                    ImGui.EndTooltip();
                }

                // ── Actions ───────────────────────────────────────────────────
                ImGui.TableSetColumnIndex(1);
                ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(3f, 0f)); // Reduce button spacing

                bool canTeleport = quest.NearestAetheryteId.HasValue;
                bool isNavTarget = config.NavQuestId == quest.QuestId;

                // Nav button
                var navLabel = isNavTarget ? "✓" : "Nav";
                var navWidth = isNavTarget ? 28f : (config.CompactMode ? 36f : 44f);
                if (ImGui.Button($"{navLabel}##{quest.QuestId}_nav", new Vector2(navWidth, 0)))
                {
                    if (isNavTarget)
                    {
                        config.NavQuestId = 0;
                        arrowOverlay.SetNavTarget(null);
                    }
                    else
                    {
                        config.NavQuestId = quest.QuestId;
                        arrowOverlay.SetNavTarget(quest);
                    }
                    config.Save();
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(isNavTarget
                        ? "Clear arrow navigation for this quest"
                        : "Show arrow overlay pointing to this quest");

                if (!config.CompactMode)
                {
                    ImGui.SameLine();
                    // Map flag button — toggles between set and clear
                    bool hasLocation = objectiveTarget.TerritoryId != 0 && objectiveTarget.MapId != 0;
                    bool isFlagged   = activeFlagQuestId == quest.QuestId;
                    if (!hasLocation) ImGui.BeginDisabled();
                    var flagLabel = isFlagged ? "Unflag" : "Flag";
                    if (ImGui.Button($"{flagLabel}##{quest.QuestId}_flag", new Vector2(50f, 0)))
                    {
                        if (isFlagged)
                            ClearMapFlag();
                        else
                            DoSetMapFlag(quest, objectiveTarget);
                    }
                    if (!hasLocation) ImGui.EndDisabled();
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(isFlagged
                            ? "Clear the map flag for this quest"
                            : "Place a map flag at this quest's location");
                }

                // Teleport button
                ImGui.SameLine();
                if (!canTeleport) ImGui.BeginDisabled();
                if (ImGui.Button($"TP##{quest.QuestId}", new Vector2(config.CompactMode ? 36f : 44f, 0)))
                    DoTeleport(quest);
                if (!canTeleport) ImGui.EndDisabled();
                if (ImGui.IsItemHovered())
                {
                    if (canTeleport)
                        ImGui.SetTooltip($"Teleport to {quest.AetheryteName}\n({(quest.GilCost == 0 ? "Free" : $"{quest.GilCost}g")})");
                    else
                        ImGui.SetTooltip("No aetheryte near this quest");
                }

                ImGui.PopStyleVar(); // ItemSpacing
            }

            ImGui.EndTable();
        }

        private void DoTeleport(QuestEntry quest)
        {
            var success = questService.Teleport(quest.NearestAetheryteId!.Value);
            statusMessage = success
                ? $"Teleporting to {quest.AetheryteName} for \"{quest.Name}\"..."
                : "Teleport failed — make sure you are not in combat.";
        }

        private void DoSetMapFlag(QuestEntry quest, QuestEntry objectiveTarget)
        {
            try
            {
                var payload = new MapLinkPayload(objectiveTarget.TerritoryId, objectiveTarget.MapId, objectiveTarget.MapX, objectiveTarget.MapY);
                gameGui.OpenMapWithMapLink(payload);
                activeFlagQuestId = quest.QuestId;
                statusMessage = $"Map flag set in {objectiveTarget.ZoneName}.";
            }
            catch (Exception ex)
            {
                statusMessage = $"Could not set map flag: {ex.Message}";
            }
        }

        private void ClearMapFlag()
        {
            activeFlagQuestId = 0;
            statusMessage = "Map flag cleared.";
        }

        private static void DrawTypeBadge(string questType)
        {
            if (string.IsNullOrEmpty(questType)) return;

            var badge = "?";
            var color = new Vector4(0.6f, 0.6f, 0.6f, 1f);

            foreach (var (kw, lbl, col) in TypeRules)
            {
                if (questType.Contains(kw, StringComparison.OrdinalIgnoreCase))
                {
                    badge = lbl;
                    color = col;
                    break;
                }
            }

            ImGui.TextColored(color, badge);
        }

        private void DrawFootnote()
        {
            ImGui.TextDisabled("Green = current zone");
        }
    }
}

