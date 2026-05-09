using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UndercutterFFXIV.Models;
using Dalamud.Plugin.Services;

namespace UndercutterFFXIV.Services
{
    public enum ScanMode { Watchlist, VelocityThreshold, TopItems, GearOnly, WeaponsOnly, ArmorOnly, AccessoriesOnly, ConsumablesOnly }

    public sealed class ProfitScannerService
    {
        private const int PartialPublishBatchSize = 5;

        private readonly IDataManager dataManager;
        private readonly UniversalisMarketClient universalis;
        private readonly MarketMasterDatabase database;
        private readonly Configuration config;
        private readonly RetainerPriceService retainerPriceService;

        private readonly object cacheLock = new();
        private List<ItemLookup>? itemCache;
        private List<ItemLookup>? marketableItemCache;
        private List<ItemLookup>? gearItemCache;
        private List<ItemLookup>? weaponItemCache;
        private List<ItemLookup>? armorItemCache;
        private List<ItemLookup>? accessoryItemCache;
        private List<ItemLookup>? consumableItemCache;
        private List<ArbitrageOpportunity> lastResults = new();
        private ScanTimingSnapshot lastScanTiming = new();
        private bool scanInProgress;
        private int scanProcessedItems;
        private int scanTotalItems;
        private int scanProgressPercent;

        private ScanMode currentScanMode = ScanMode.TopItems;
        private string? currentScanCategory;
        private int topItemsCount = 250;

        public ProfitScannerService(
            IDataManager dataManager,
            UniversalisMarketClient universalis,
            MarketMasterDatabase database,
            Configuration config,
            RetainerPriceService retainerPriceService)
        {
            this.dataManager = dataManager;
            this.universalis = universalis;
            this.database = database;
            this.config = config;
            this.retainerPriceService = retainerPriceService;
        }

        public IReadOnlyList<ItemLookup> SearchItems(string query, int limit = 100)
        {
            EnsureItemCacheLoaded();
            if (itemCache == null || itemCache.Count == 0)
                return Array.Empty<ItemLookup>();

            var q = (query ?? string.Empty).Trim();
            if (q.Length == 0)
                return itemCache.Take(limit).ToList();

            return itemCache
                .Where(i => i.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                .Take(Math.Max(1, limit))
                .ToList();
        }

        public IReadOnlyList<WatchedItem> GetWatchlist()
        {
            var manualItems = database.GetWatchedItems();
            if (!config.AutoTrackCurrentlySellingItems)
                return manualItems;

            var combined = manualItems.ToDictionary(item => item.ItemId);
            foreach (var liveItem in retainerPriceService.GetCurrentSellingItems())
            {
                if (combined.ContainsKey(liveItem.ItemId))
                    continue;

                combined[liveItem.ItemId] = new WatchedItem
                {
                    ItemId = liveItem.ItemId,
                    Name = liveItem.Name,
                    AddedUtc = DateTime.UtcNow,
                    IsAutoTracked = true
                };
            }

            return combined.Values
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public void AddWatchItem(ItemLookup item) => database.AddOrUpdateWatchedItem(item.ItemId, item.Name);

        public void RemoveWatchItem(uint itemId) => database.RemoveWatchedItem(itemId);

        public IReadOnlyList<ArbitrageOpportunity> GetLastResults()
        {
            lock (cacheLock)
                return lastResults.ToList();
        }

        public ApiHealthSnapshot GetApiHealth() => universalis.GetHealthSnapshot();
        public ScanTimingSnapshot GetLastScanTiming()
        {
            lock (cacheLock)
                return lastScanTiming;
        }

        public IReadOnlyList<(DateTime DateUtc, double TotalNetProfit)> GetProfitSeries(int days) =>
            database.GetDailyProfitSeries(days);

        public IReadOnlyList<TradeHistoryEntry> GetTradeHistory(int days = 30)
            => database.GetTradeHistory(days);

        public void AddTradeHistoryEntry(uint itemId, string itemName, uint buyPrice, uint sellPrice, uint quantity)
        {
            database.AddTradeHistoryEntry(new TradeHistoryEntry
            {
                ItemId = itemId,
                ItemName = itemName,
                BuyPrice = buyPrice,
                SellPrice = sellPrice,
                Quantity = quantity,
                TradedUtc = DateTime.UtcNow
            });
        }

        public (bool IsRunning, int Processed, int Total, int Percent) GetScanProgress()
        {
            lock (cacheLock)
            return (scanInProgress, scanProcessedItems, scanTotalItems, scanProgressPercent);
        }

        public void SetScanMode(ScanMode mode, string? categoryName = null, int topCount = 200)
        {
            currentScanMode = mode;
            currentScanCategory = categoryName;
            topItemsCount = topCount;
        }

        public ScanMode GetCurrentScanMode() => currentScanMode;
        public string? GetCurrentCategory() => currentScanCategory;
        public int GetTopItemsCount() => topItemsCount;

        public IReadOnlyList<string> GetItemCategories()
        {
            // Category scanning simplified - returns watchlist only for now
            return new[] { "Watchlist" };
        }

        private List<ItemLookup> GetItemsForScan()
        {
            return GetItemsForMode(currentScanMode, topItemsCount);
        }

        private List<ItemLookup> GetItemsForMode(ScanMode mode, int topCount)
        {
            EnsureItemCacheLoaded();
            if (itemCache == null || itemCache.Count == 0)
                return new List<ItemLookup>();

            var watchlistItems = GetWatchlist()
                .Select(w => new ItemLookup { ItemId = w.ItemId, Name = w.Name })
                .ToList();

            return mode switch
            {
                ScanMode.Watchlist
                    => watchlistItems,
                ScanMode.VelocityThreshold
                    => MergeScanCandidates(watchlistItems, GetHighVelocityItems(config.MinSaleVelocityPerDay * 2)),
                ScanMode.TopItems
                    => MergeScanCandidates(watchlistItems, GetTopTradedItems(topCount)),
                ScanMode.GearOnly
                    => GetGearItems(),
                ScanMode.WeaponsOnly
                    => GetWeaponItems(),
                ScanMode.ArmorOnly
                    => GetArmorItems(),
                ScanMode.AccessoriesOnly
                    => GetAccessoryItems(),
                ScanMode.ConsumablesOnly
                    => GetConsumableItems(),
                _ => watchlistItems
            };
        }

        private List<ItemLookup> GetGearItems()
        {
            EnsureItemCacheLoaded();
            return gearItemCache?.ToList() ?? new List<ItemLookup>();
        }

        private List<ItemLookup> GetWeaponItems()
        {
            EnsureItemCacheLoaded();
            return weaponItemCache?.ToList() ?? new List<ItemLookup>();
        }

        private List<ItemLookup> GetArmorItems()
        {
            EnsureItemCacheLoaded();
            return armorItemCache?.ToList() ?? new List<ItemLookup>();
        }

        private List<ItemLookup> GetAccessoryItems()
        {
            EnsureItemCacheLoaded();
            return accessoryItemCache?.ToList() ?? new List<ItemLookup>();
        }

        private List<ItemLookup> GetConsumableItems()
        {
            EnsureItemCacheLoaded();
            if (consumableItemCache == null || consumableItemCache.Count == 0)
                return new List<ItemLookup>();

            // Limit to top 250 consumables to avoid excessive API batches (~100 requests for 9000+ items)
            // while still covering potions, food, materials, and common crafting ingredients
            const int consumableLimit = 250;
            if (consumableItemCache.Count <= consumableLimit)
                return consumableItemCache.ToList();

            // Sample the consumables list across its range to get diverse items
            var step = consumableItemCache.Count / (double)consumableLimit;
            var sample = new List<ItemLookup>(consumableLimit);
            var seen = new HashSet<uint>();

            for (var index = 0; index < consumableLimit; index++)
            {
                var sourceIndex = Math.Min(consumableItemCache.Count - 1, (int)Math.Floor(index * step));
                var item = consumableItemCache[sourceIndex];
                if (!seen.Add(item.ItemId))
                    continue;

                sample.Add(item);
            }

            if (sample.Count < consumableLimit)
            {
                foreach (var item in consumableItemCache)
                {
                    if (!seen.Add(item.ItemId))
                        continue;

                    sample.Add(item);
                    if (sample.Count >= consumableLimit)
                        break;
                }
            }

            return sample;
        }

        private List<ItemLookup> GetHighVelocityItems(double minVelocity)
        {
            // Universalis velocity is not cached locally, so use a broad marketable sample.
            // This still produces actionable scan candidates instead of the old placeholder first-row slice.
            var targetCount = minVelocity >= 4 ? 250 : 500;
            return GetMarketableSample(targetCount);
        }

        private List<ItemLookup> GetTopTradedItems(int count)
        {
            return GetMarketableSample(Math.Max(10, count));
        }

        private List<ItemLookup> MergeScanCandidates(
            IReadOnlyList<ItemLookup> primary,
            IReadOnlyList<ItemLookup> secondary)
        {
            var merged = new List<ItemLookup>(primary.Count + secondary.Count);
            var seen = new HashSet<uint>();

            foreach (var item in primary)
            {
                if (!seen.Add(item.ItemId))
                    continue;

                merged.Add(item);
            }

            foreach (var item in secondary)
            {
                if (!seen.Add(item.ItemId))
                    continue;

                merged.Add(item);
            }

            return merged;
        }

        private List<ItemLookup> GetMarketableSample(int desiredCount)
        {
            EnsureItemCacheLoaded();
            if (marketableItemCache == null || marketableItemCache.Count == 0)
                return new List<ItemLookup>();

            var count = Math.Min(Math.Max(1, desiredCount), marketableItemCache.Count);
            if (count >= marketableItemCache.Count)
                return marketableItemCache.ToList();

            var step = marketableItemCache.Count / (double)count;
            var sample = new List<ItemLookup>(count);
            var seen = new HashSet<uint>();

            for (var index = 0; index < count; index++)
            {
                var sourceIndex = Math.Min(marketableItemCache.Count - 1, (int)Math.Floor(index * step));
                var item = marketableItemCache[sourceIndex];
                if (!seen.Add(item.ItemId))
                    continue;

                sample.Add(item);
            }

            if (sample.Count < count)
            {
                foreach (var item in marketableItemCache)
                {
                    if (!seen.Add(item.ItemId))
                        continue;

                    sample.Add(item);
                    if (sample.Count >= count)
                        break;
                }
            }

            return sample;
        }

        public async Task<IReadOnlyList<ArbitrageOpportunity>> ScanWatchlistAsync(CancellationToken cancellationToken)
        {
            return await ScanAsync(GetItemsForScan(), cancellationToken);
        }

        public async Task<IReadOnlyList<ArbitrageOpportunity>> ScanWatchlistOnlyAsync(CancellationToken cancellationToken)
        {
            return await ScanAsync(GetItemsForMode(ScanMode.Watchlist, topItemsCount), cancellationToken);
        }

        private async Task<IReadOnlyList<ArbitrageOpportunity>> ScanAsync(
            List<ItemLookup> itemsToScan,
            CancellationToken cancellationToken)
        {
            var results = new List<ArbitrageOpportunity>();
            var resultsLock = new object();

            lock (cacheLock)
            {
                if (scanInProgress)
                    return lastResults.ToList();

                scanInProgress = true;
                scanProcessedItems = 0;
                scanTotalItems = itemsToScan.Count;
                scanProgressPercent = 0;
                lastResults = new List<ArbitrageOpportunity>();
            }

            if (itemsToScan.Count == 0)
            {
                lock (cacheLock)
                {
                    lastResults = new List<ArbitrageOpportunity>();
                    scanInProgress = false;
                }
                return lastResults;
            }

            try
            {
                var sw = Stopwatch.StartNew();
                var homeFetchSw = Stopwatch.StartNew();
                var scannedSinceLastPublish = 0;
                var publishCounterLock = new object();

                var itemIds = itemsToScan
                    .Select(item => item.ItemId)
                    .Where(id => id > 0)
                    .Distinct()
                    .ToList();

                // Phase 1: batch-fetch home world snapshots for all scan items.
                var homeSnapshots = await universalis.GetMarketSnapshotsAsync(
                    config.WorldName,
                    itemIds,
                    cancellationToken,
                    (processed, total) => UpdateScanProgressByPhase(processed, total, 0, 10));
                homeFetchSw.Stop();

                // Mark fetch completion at 10%
                UpdateScanProgressPercent(10);

                var homeFilterSw = Stopwatch.StartNew();
                var homeCandidatesByItemId = new Dictionary<uint, HomeSnapshotCandidate>();
                var itemsProcessedInHomeFilter = 0;
                var homeFilterIncrementEvery = Math.Max(1, itemsToScan.Count / 30); // Distribute remaining 30% across filter

                foreach (var item in itemsToScan)
                {
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        if (!homeSnapshots.TryGetValue(item.ItemId, out var home))
                            continue;

                        if (!TryBuildHomeSnapshotCandidate(home, currentScanMode, out var candidate))
                            continue;

                        homeCandidatesByItemId[item.ItemId] = candidate;
                    }
                    finally
                    {
                        itemsProcessedInHomeFilter++;
                        if (itemsProcessedInHomeFilter % homeFilterIncrementEvery == 0)
                            UpdateScanProgressByPhase(itemsProcessedInHomeFilter, itemsToScan.Count, 10, 40);
                    }
                }
                homeFilterSw.Stop();

                // Mark filter completion at 40%
                UpdateScanProgressPercent(40);

                // Phase 2: batch-fetch DC snapshots only for home-eligible candidates.
                var candidateIds = homeCandidatesByItemId.Keys.ToList();
                var dcFetchSw = Stopwatch.StartNew();
                var dcSnapshots = candidateIds.Count == 0
                    ? new Dictionary<uint, MarketSnapshot>()
                    : await universalis.GetMarketSnapshotsAsync(
                        config.DataCenterName,
                        candidateIds,
                        cancellationToken,
                        (processed, total) => UpdateScanProgressByPhase(processed, total, 40, 50));
                dcFetchSw.Stop();

                // Mark DC fetch completion at 50%
                UpdateScanProgressPercent(50);

                var evaluationSw = Stopwatch.StartNew();
                var itemsProcessedInEval = 0;
                var evalIncrementEvery = Math.Max(1, itemsToScan.Count / 50); // Distribute remaining 50% across eval

                foreach (var item in itemsToScan)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var publishNow = false;

                    try
                    {
                        if (!homeCandidatesByItemId.TryGetValue(item.ItemId, out var homeCandidate))
                            continue;

                        if (!dcSnapshots.TryGetValue(item.ItemId, out var dcSnapshot))
                            continue;

                        var opportunity = BuildOpportunity(item, homeCandidate, dcSnapshot);
                        if (opportunity != null)
                        {
                            lock (resultsLock)
                                results.Add(opportunity);
                        }
                    }
                    finally
                    {
                        lock (publishCounterLock)
                        {
                            scannedSinceLastPublish++;
                            if (scannedSinceLastPublish >= PartialPublishBatchSize)
                            {
                                scannedSinceLastPublish = 0;
                                publishNow = true;
                            }
                        }

                        itemsProcessedInEval++;
                        if (itemsProcessedInEval % evalIncrementEvery == 0)
                            UpdateScanProgressByPhase(itemsProcessedInEval, itemsToScan.Count, 50, 100);

                        if (publishNow)
                        {
                            lock (resultsLock)
                                PublishPartialResults(results);
                        }
                    }
                }
                evaluationSw.Stop();

                sw.Stop();
                var sorted = results
                    .OrderByDescending(r => r.NetProfitPerUnit)
                    .ThenByDescending(r => r.SaleVelocityPerDay)
                    .ToList();

                database.SaveScanResults(config.WorldName, config.DataCenterName, sorted, sw.ElapsedMilliseconds);

                lock (cacheLock)
                {
                    lastResults = sorted;
                    scanProgressPercent = 100;
                    scanProcessedItems = scanTotalItems;
                    lastScanTiming = new ScanTimingSnapshot
                    {
                        HomeFetchMs = homeFetchSw.ElapsedMilliseconds,
                        HomeFilterMs = homeFilterSw.ElapsedMilliseconds,
                        DcFetchMs = dcFetchSw.ElapsedMilliseconds,
                        EvaluationMs = evaluationSw.ElapsedMilliseconds,
                        TotalMs = sw.ElapsedMilliseconds,
                        CandidateCount = candidateIds.Count
                    };
                }

                return sorted;
            }
            finally
            {
                lock (cacheLock)
                    scanInProgress = false;
            }
        }

        private void UpdateScanProgressByPhase(int phaseProcessed, int phaseTotal, int phaseStartPercent, int phaseEndPercent)
        {
            if (phaseTotal <= 0)
            {
                UpdateScanProgressPercent(phaseEndPercent);
                return;
            }

            var boundedProcessed = Math.Min(Math.Max(phaseProcessed, 0), phaseTotal);
            var phaseRatio = boundedProcessed / (double)phaseTotal;
            var phasePercent = phaseStartPercent + (int)Math.Round((phaseEndPercent - phaseStartPercent) * phaseRatio);
            UpdateScanProgressPercent(phasePercent);
        }

        private void UpdateScanProgressPercent(int percent)
        {
            lock (cacheLock)
            {
                var boundedPercent = Math.Min(Math.Max(percent, 0), 100);
                scanProgressPercent = Math.Max(scanProgressPercent, boundedPercent);

                if (scanTotalItems <= 0)
                {
                    scanProcessedItems = 0;
                    return;
                }

                if (scanProgressPercent >= 100)
                {
                    scanProcessedItems = scanTotalItems;
                    return;
                }

                var estimated = (int)Math.Ceiling((scanProgressPercent / 100.0) * scanTotalItems);
                scanProcessedItems = Math.Min(scanTotalItems, Math.Max(scanProcessedItems, Math.Max(0, estimated)));
            }
        }

        private bool TryBuildHomeSnapshotCandidate(
            MarketSnapshot home,
            ScanMode scanMode,
            out HomeSnapshotCandidate candidate)
        {
            candidate = new HomeSnapshotCandidate();

            // Always use the actual lowest active listing on Home World as the sell-side reference.
            var homeLowestListingPrice = home.Listings
                .Where(l => l.PricePerUnit > 0)
                .Select(l => l.PricePerUnit)
                .DefaultIfEmpty(home.LowestPrice)
                .Min();
            if (homeLowestListingPrice == 0)
                return false;

            var velocity = CalculateSaleVelocity(home.RecentSales, config.ScannerLookbackDays);
            var minVelocity = (scanMode == ScanMode.WeaponsOnly || scanMode == ScanMode.ArmorOnly || scanMode == ScanMode.AccessoriesOnly || scanMode == ScanMode.GearOnly)
                ? config.GearMinVelocityPerDay
                : config.MinSaleVelocityPerDay;

            if (velocity < minVelocity)
                return false;

            var (salesCount24h, unitsSold24h) = CalculateRecentSales24h(home.RecentSales);
            if (unitsSold24h < Math.Max(0, config.MinUnitsSold24h))
                return false;

            candidate = new HomeSnapshotCandidate
            {
                HomeLowestListingPrice = homeLowestListingPrice,
                VelocityPerDay = velocity,
                SalesCount24h = salesCount24h,
                UnitsSold24h = unitsSold24h,
                HomeListings = home.Listings
            };

            return true;
        }

        private ArbitrageOpportunity? BuildOpportunity(
            ItemLookup item,
            HomeSnapshotCandidate homeCandidate,
            MarketSnapshot dc)
        {
            if (dc.LowestPrice == 0)
                return null;

            var sortedDcListings = dc.Listings
                .Where(l => l.PricePerUnit > 0)
                .OrderBy(l => l.PricePerUnit)
                .ToList();
            if (sortedDcListings.Count == 0)
                return null;

            var netSellPerUnit = homeCandidate.HomeLowestListingPrice * (1 - (config.MarketTaxRatePercent / 100.0));
            var buySelection = SelectBuyReference(sortedDcListings, netSellPerUnit);
            if (buySelection == null)
                return null;

            var buyPrice = buySelection.EffectiveBuyPricePerUnit;
            var buyWorld = buySelection.BuyWorld;

            var netProfit = netSellPerUnit - buyPrice;
            var profitPercent = buyPrice == 0 ? 0 : (netProfit / buyPrice) * 100;

            if (netProfit < config.MinNetProfitGil || profitPercent < config.MinNetProfitPercent)
                return null;

            var botPattern = DetectPotentialBotPattern(homeCandidate.HomeListings);
            
            // Calculate total quantity listed at home world's minimum price
            var homeWorldCurrentQty = (uint)homeCandidate.HomeListings
                .Where(l => l.PricePerUnit == homeCandidate.HomeLowestListingPrice)
                .Sum(l => (long)Math.Max(0u, l.Quantity));

            return new ArbitrageOpportunity
            {
                ItemId = item.ItemId,
                ItemName = item.Name,
                HomeWorldMinPrice = homeCandidate.HomeLowestListingPrice,
                DataCenterLowestPrice = (uint)Math.Max(0, Math.Round(buyPrice)),
                BuyFromWorld = buyWorld,
                NetProfitPerUnit = netProfit,
                ProfitPercent = profitPercent,
                SaleVelocityPerDay = homeCandidate.VelocityPerDay,
                SalesCount24h = homeCandidate.SalesCount24h,
                UnitsSold24h = homeCandidate.UnitsSold24h,
                HomeWorldCurrentQtyListing = homeWorldCurrentQty,
                PotentialBotSellerPattern = botPattern,
                SafeBuyQty = ArbitrageOpportunity.ComputeSafeBuyQty(homeCandidate.VelocityPerDay, botPattern),
                ScannedUtc = DateTime.UtcNow
            };
        }

        private BuySelection? SelectBuyReference(IReadOnlyList<ListingRecord> sortedDcListings, double netSellPerUnit)
        {
            var cheapest = sortedDcListings[0];
            var cheapestPrice = cheapest.PricePerUnit;

            var cheapThreshold = Math.Max(1, config.CheapItemPriceThresholdGil);
            var minCheapQty = Math.Max(1, config.CheapItemMinProfitableQuantity);
            var isCheapItem = cheapestPrice <= cheapThreshold;

            if (!isCheapItem)
            {
                return new BuySelection
                {
                    EffectiveBuyPricePerUnit = cheapestPrice,
                    BuyWorld = cheapest.WorldName
                };
            }

            // For cheap items, avoid one-off bait listings by requiring enough profitable units
            // and using the blended buy price for that quantity.
            var maxProfitableBuyPrice = CalculateMaxProfitableBuyPrice(netSellPerUnit);
            if (maxProfitableBuyPrice <= 0)
                return null;

            var profitableListings = sortedDcListings
                .Where(l => l.PricePerUnit <= maxProfitableBuyPrice)
                .ToList();
            if (profitableListings.Count == 0)
                return null;

            var profitableUnits = profitableListings.Sum(l => (int)Math.Max(1u, l.Quantity));
            if (profitableUnits < minCheapQty)
                return null;

            var targetUnits = minCheapQty;
            var consumedUnits = 0;
            double totalCost = 0;
            var buyWorld = profitableListings[0].WorldName;

            foreach (var listing in profitableListings)
            {
                if (consumedUnits >= targetUnits)
                    break;

                var availableUnits = (int)Math.Max(1u, listing.Quantity);
                var takeUnits = Math.Min(availableUnits, targetUnits - consumedUnits);
                totalCost += takeUnits * listing.PricePerUnit;
                consumedUnits += takeUnits;
            }

            if (consumedUnits < targetUnits)
                return null;

            return new BuySelection
            {
                EffectiveBuyPricePerUnit = totalCost / consumedUnits,
                BuyWorld = buyWorld
            };
        }

        private double CalculateMaxProfitableBuyPrice(double netSellPerUnit)
        {
            if (netSellPerUnit <= 0)
                return 0;

            var maxByGil = netSellPerUnit - Math.Max(0, config.MinNetProfitGil);
            var denominator = 1.0 + (Math.Max(0, config.MinNetProfitPercent) / 100.0);
            var maxByPercent = denominator <= 0 ? 0 : netSellPerUnit / denominator;
            return Math.Floor(Math.Min(maxByGil, maxByPercent));
        }

        private sealed class BuySelection
        {
            public double EffectiveBuyPricePerUnit { get; init; }
            public string BuyWorld { get; init; } = string.Empty;
        }

        private sealed class HomeSnapshotCandidate
        {
            public uint HomeLowestListingPrice { get; init; }
            public double VelocityPerDay { get; init; }
            public int SalesCount24h { get; init; }
            public int UnitsSold24h { get; init; }
            public IReadOnlyList<ListingRecord> HomeListings { get; init; } = Array.Empty<ListingRecord>();
        }

        private void PublishPartialResults(List<ArbitrageOpportunity> inProgressResults)
        {
            var partial = inProgressResults
                .OrderByDescending(r => r.NetProfitPerUnit)
                .ThenByDescending(r => r.SaleVelocityPerDay)
                .ToList();

            lock (cacheLock)
                lastResults = partial;
        }

        public async Task<uint> FetchHomeFloorPriceAsync(uint itemId, CancellationToken cancellationToken)
        {
            var home = await universalis.GetMarketSnapshotAsync(config.WorldName, itemId, cancellationToken);
            return home?.LowestPrice ?? 0;
        }

        private void EnsureItemCacheLoaded()
        {
            lock (cacheLock)
            {
                if (itemCache != null)
                    return;

                var loaded = new List<ItemLookup>(32000);
                var marketable = new List<ItemLookup>(24000);
                var gear = new List<ItemLookup>(12000);
                var weapons = new List<ItemLookup>(8000);
                var armor = new List<ItemLookup>(8000);
                var accessories = new List<ItemLookup>(5000);
                var consumables = new List<ItemLookup>(8000);
                var sheet = dataManager.GetExcelSheet<Item>();
                if (sheet != null)
                {
                    foreach (var row in sheet)
                    {
                        if (row.RowId == 0)
                            continue;

                        var name = row.Name.ToString();
                        if (string.IsNullOrWhiteSpace(name))
                            continue;

                        var item = new ItemLookup
                        {
                            ItemId = row.RowId,
                            Name = name,
                            IsGear = row.EquipSlotCategory.RowId > 0,
                            RequiredLevel = (int)row.LevelEquip,
                            ItemLevel = (int)row.LevelItem.RowId
                        };

                        if (item.IsGear)
                        {
                            // Defensive correction in case upstream sheet mapping swaps these in a future API update.
                            if (item.RequiredLevel > 100 && item.ItemLevel > 0 && item.ItemLevel <= 100)
                            {
                                var correctedRequired = item.ItemLevel;
                                var correctedItemLevel = item.RequiredLevel;
                                item = new ItemLookup
                                {
                                    ItemId = item.ItemId,
                                    Name = item.Name,
                                    IsGear = true,
                                    RequiredLevel = correctedRequired,
                                    ItemLevel = correctedItemLevel
                                };
                            }
                        }

                        loaded.Add(item);

                        if (!row.IsUntradable)
                        {
                            marketable.Add(item);

                            if (IsGearItem(row))
                            {
                                gear.Add(item);

                                var equipmentType = ClassifyEquipmentType(row.EquipSlotCategory.RowId);
                                if (equipmentType == EquipmentType.Weapon)
                                    weapons.Add(item);
                                else if (equipmentType == EquipmentType.Armor)
                                    armor.Add(item);
                                else if (equipmentType == EquipmentType.Accessory)
                                    accessories.Add(item);
                            }
                            else
                            {
                                // Not gear = consumable (potions, food, materials, etc.)
                                consumables.Add(item);
                            }
                        }
                    }
                }

                itemCache = loaded;
                marketableItemCache = marketable
                    .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                gearItemCache = gear
                    .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                weaponItemCache = weapons
                    .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                armorItemCache = armor
                    .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                accessoryItemCache = accessories
                    .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                consumableItemCache = consumables
                    .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                LoggingService.LogInfo($"Loaded {loaded.Count} items into search cache");
                LoggingService.LogInfo($"Prepared {marketableItemCache.Count} marketable scan candidates");
                LoggingService.LogInfo($"Prepared {gearItemCache.Count} gear scan candidates");
                LoggingService.LogInfo($"Prepared {weaponItemCache.Count} weapon scan candidates");
                LoggingService.LogInfo($"Prepared {armorItemCache.Count} armor scan candidates");
                LoggingService.LogInfo($"Prepared {accessoryItemCache.Count} accessory scan candidates");
                LoggingService.LogInfo($"Prepared {consumableItemCache.Count} consumable scan candidates");
            }
        }

        private static bool IsGearItem(Item item)
        {
            // EquipSlotCategory > 0 captures equippable items (weapons, armor, accessories).
            return item.EquipSlotCategory.RowId > 0;
        }

        private enum EquipmentType
        {
            Unknown,
            Weapon,
            Armor,
            Accessory
        }

        private static EquipmentType ClassifyEquipmentType(uint equipSlotCategoryId)
        {
            // Equip slot category IDs are stable enough for broad scanner segmentation:
            // 1-2 and 13-14: weapon/offhand sets, 3-8: armor slots, 9-12: accessories.
            return equipSlotCategoryId switch
            {
                1 or 2 or 13 or 14 => EquipmentType.Weapon,
                >= 3 and <= 8 => EquipmentType.Armor,
                >= 9 and <= 12 => EquipmentType.Accessory,
                _ => EquipmentType.Unknown
            };
        }

        private static double CalculateSaleVelocity(IReadOnlyList<SaleRecord> sales, int lookbackDays)
        {
            if (sales.Count == 0)
                return 0;

            var cutoff = DateTime.UtcNow.AddDays(-Math.Max(1, lookbackDays));
            var relevant = sales.Where(s => s.TimestampUtc >= cutoff).ToList();
            if (relevant.Count == 0)
                return 0;

            return relevant.Count / (double)Math.Max(1, lookbackDays);
        }

        private static (int SalesCount, int UnitsSold) CalculateRecentSales24h(IReadOnlyList<SaleRecord> sales)
        {
            if (sales.Count == 0)
                return (0, 0);

            var cutoff = DateTime.UtcNow.AddHours(-24);
            var relevant = sales.Where(s => s.TimestampUtc >= cutoff).ToList();
            if (relevant.Count == 0)
                return (0, 0);

            var units = relevant.Sum(s => (int)Math.Max(1u, s.Quantity));
            return (relevant.Count, units);
        }

        private static bool DetectPotentialBotPattern(IReadOnlyList<ListingRecord> listings)
        {
            if (listings.Count < 6)
                return false;

            var suspiciousGroup = listings
                .Where(l => !string.IsNullOrWhiteSpace(l.SellerName))
                .GroupBy(l => (l.SellerName, l.PricePerUnit))
                .Any(g => g.Count() >= 3);

            return suspiciousGroup;
        }
    }
}
