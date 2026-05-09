using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UndercutterFFXIV.Models;

namespace UndercutterFFXIV.Services
{
    public sealed class MarketMasterDatabase
    {
        private readonly string connectionString;
        private readonly object dbLock = new();

        public MarketMasterDatabase(string pluginConfigDirectory)
        {
            var dbPath = Path.Combine(pluginConfigDirectory, "market_master_pro.db");
            connectionString = $"Data Source={dbPath}";
            InitializeSchema();
        }

        public IReadOnlyList<WatchedItem> GetWatchedItems()
        {
            lock (dbLock)
            {
                using var connection = new SqliteConnection(connectionString);
                connection.Open();

                using var cmd = connection.CreateCommand();
                cmd.CommandText = @"
SELECT item_id, item_name, added_utc
FROM watched_items
ORDER BY item_name";

                using var reader = cmd.ExecuteReader();
                var list = new List<WatchedItem>();
                while (reader.Read())
                {
                    list.Add(new WatchedItem
                    {
                        ItemId = (uint)reader.GetInt64(0),
                        Name = reader.GetString(1),
                        AddedUtc = DateTime.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
                    });
                }

                return list;
            }
        }

        public void AddOrUpdateWatchedItem(uint itemId, string itemName)
        {
            lock (dbLock)
            {
                using var connection = new SqliteConnection(connectionString);
                connection.Open();

                using var cmd = connection.CreateCommand();
                cmd.CommandText = @"
INSERT INTO watched_items(item_id, item_name, added_utc)
VALUES($id, $name, $added)
ON CONFLICT(item_id)
DO UPDATE SET item_name = excluded.item_name";
                cmd.Parameters.AddWithValue("$id", itemId);
                cmd.Parameters.AddWithValue("$name", itemName);
                cmd.Parameters.AddWithValue("$added", DateTime.UtcNow.ToString("O"));
                cmd.ExecuteNonQuery();
            }
        }

        public void RemoveWatchedItem(uint itemId)
        {
            lock (dbLock)
            {
                using var connection = new SqliteConnection(connectionString);
                connection.Open();

                using var cmd = connection.CreateCommand();
                cmd.CommandText = "DELETE FROM watched_items WHERE item_id = $id";
                cmd.Parameters.AddWithValue("$id", itemId);
                cmd.ExecuteNonQuery();
            }
        }

        public void SaveScanResults(
            string homeWorld,
            string dataCenter,
            IReadOnlyList<ArbitrageOpportunity> opportunities,
            long durationMs)
        {
            lock (dbLock)
            {
                using var connection = new SqliteConnection(connectionString);
                connection.Open();

                using var transaction = connection.BeginTransaction();

                long runId;
                using (var runCmd = connection.CreateCommand())
                {
                    runCmd.Transaction = transaction;
                    runCmd.CommandText = @"
INSERT INTO scan_runs(run_utc, duration_ms, item_count, home_world, data_center)
VALUES($utc, $duration, $count, $home, $dc);
SELECT last_insert_rowid();";
                    runCmd.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
                    runCmd.Parameters.AddWithValue("$duration", durationMs);
                    runCmd.Parameters.AddWithValue("$count", opportunities.Count);
                    runCmd.Parameters.AddWithValue("$home", homeWorld);
                    runCmd.Parameters.AddWithValue("$dc", dataCenter);
                    runId = (long)runCmd.ExecuteScalar()!;
                }

                foreach (var opportunity in opportunities)
                {
                    using var oppCmd = connection.CreateCommand();
                    oppCmd.Transaction = transaction;
                    oppCmd.CommandText = @"
INSERT INTO opportunities(
    run_id,
    item_id,
    item_name,
    home_min_price,
    dc_lowest_price,
    net_profit_per_unit,
    profit_percent,
    sale_velocity_per_day,
    potential_bot
)
VALUES($run, $id, $name, $homeMin, $dcLow, $net, $percent, $velocity, $bot)";

                    oppCmd.Parameters.AddWithValue("$run", runId);
                    oppCmd.Parameters.AddWithValue("$id", opportunity.ItemId);
                    oppCmd.Parameters.AddWithValue("$name", opportunity.ItemName);
                    oppCmd.Parameters.AddWithValue("$homeMin", opportunity.HomeWorldMinPrice);
                    oppCmd.Parameters.AddWithValue("$dcLow", opportunity.DataCenterLowestPrice);
                    oppCmd.Parameters.AddWithValue("$net", opportunity.NetProfitPerUnit);
                    oppCmd.Parameters.AddWithValue("$percent", opportunity.ProfitPercent);
                    oppCmd.Parameters.AddWithValue("$velocity", opportunity.SaleVelocityPerDay);
                    oppCmd.Parameters.AddWithValue("$bot", opportunity.PotentialBotSellerPattern ? 1 : 0);
                    oppCmd.ExecuteNonQuery();
                }

                transaction.Commit();
            }
        }

        public IReadOnlyList<(DateTime DateUtc, double TotalNetProfit)> GetDailyProfitSeries(int days)
        {
            lock (dbLock)
            {
                using var connection = new SqliteConnection(connectionString);
                connection.Open();

                using var cmd = connection.CreateCommand();
                cmd.CommandText = @"
SELECT substr(sr.run_utc, 1, 10) as day,
       SUM(o.net_profit_per_unit) as net
FROM opportunities o
JOIN scan_runs sr ON sr.id = o.run_id
WHERE sr.run_utc >= $cutoff
GROUP BY day
ORDER BY day";
                cmd.Parameters.AddWithValue("$cutoff", DateTime.UtcNow.AddDays(-Math.Max(1, days)).ToString("O"));

                using var reader = cmd.ExecuteReader();
                var points = new List<(DateTime, double)>();
                while (reader.Read())
                {
                    var day = DateTime.Parse(reader.GetString(0), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
                    var net = reader.IsDBNull(1) ? 0 : reader.GetDouble(1);
                    points.Add((day, net));
                }

                return points;
            }
        }

        public void AddTradeHistoryEntry(TradeHistoryEntry entry)
        {
            lock (dbLock)
            {
                using var connection = new SqliteConnection(connectionString);
                connection.Open();

                using var cmd = connection.CreateCommand();
                cmd.CommandText = @"
INSERT INTO trade_history(item_id, item_name, buy_price, sell_price, quantity, traded_utc)
VALUES($itemId, $itemName, $buyPrice, $sellPrice, $quantity, $tradedUtc)";

                cmd.Parameters.AddWithValue("$itemId", entry.ItemId);
                cmd.Parameters.AddWithValue("$itemName", entry.ItemName);
                cmd.Parameters.AddWithValue("$buyPrice", entry.BuyPrice);
                cmd.Parameters.AddWithValue("$sellPrice", entry.SellPrice);
                cmd.Parameters.AddWithValue("$quantity", entry.Quantity);
                cmd.Parameters.AddWithValue("$tradedUtc", entry.TradedUtc.ToString("O"));
                cmd.ExecuteNonQuery();
            }
        }

        public IReadOnlyList<TradeHistoryEntry> GetTradeHistory(int days)
        {
            lock (dbLock)
            {
                using var connection = new SqliteConnection(connectionString);
                connection.Open();

                using var cmd = connection.CreateCommand();
                cmd.CommandText = @"
SELECT id, item_id, item_name, buy_price, sell_price, quantity, traded_utc
FROM trade_history
WHERE traded_utc >= $cutoff
ORDER BY traded_utc DESC";
                cmd.Parameters.AddWithValue("$cutoff", DateTime.UtcNow.AddDays(-Math.Max(1, days)).ToString("O"));

                using var reader = cmd.ExecuteReader();
                var rows = new List<TradeHistoryEntry>();
                while (reader.Read())
                {
                    rows.Add(new TradeHistoryEntry
                    {
                        Id = reader.GetInt64(0),
                        ItemId = (uint)reader.GetInt64(1),
                        ItemName = reader.GetString(2),
                        BuyPrice = (uint)reader.GetInt64(3),
                        SellPrice = (uint)reader.GetInt64(4),
                        Quantity = (uint)reader.GetInt64(5),
                        TradedUtc = DateTime.Parse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
                    });
                }

                return rows;
            }
        }

        private void InitializeSchema()
        {
            lock (dbLock)
            {
                using var connection = new SqliteConnection(connectionString);
                connection.Open();

                using var cmd = connection.CreateCommand();
                cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS watched_items (
    item_id INTEGER PRIMARY KEY,
    item_name TEXT NOT NULL,
    added_utc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS scan_runs (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    run_utc TEXT NOT NULL,
    duration_ms INTEGER NOT NULL,
    item_count INTEGER NOT NULL,
    home_world TEXT NOT NULL,
    data_center TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS opportunities (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    run_id INTEGER NOT NULL,
    item_id INTEGER NOT NULL,
    item_name TEXT NOT NULL,
    home_min_price INTEGER NOT NULL,
    dc_lowest_price INTEGER NOT NULL,
    net_profit_per_unit REAL NOT NULL,
    profit_percent REAL NOT NULL,
    sale_velocity_per_day REAL NOT NULL,
    potential_bot INTEGER NOT NULL,
    FOREIGN KEY(run_id) REFERENCES scan_runs(id)
);

CREATE TABLE IF NOT EXISTS trade_history (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    item_id INTEGER NOT NULL,
    item_name TEXT NOT NULL,
    buy_price INTEGER NOT NULL,
    sell_price INTEGER NOT NULL,
    quantity INTEGER NOT NULL,
    traded_utc TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_opportunities_run ON opportunities(run_id);
CREATE INDEX IF NOT EXISTS idx_opportunities_item ON opportunities(item_id);
CREATE INDEX IF NOT EXISTS idx_trade_history_time ON trade_history(traded_utc);";

                cmd.ExecuteNonQuery();
            }
        }
    }
}
