using System.Globalization;
using Griffin.Common.Table;
using Serilog;

namespace Griffin.GameServer.Services
{
    /// <summary>One banner as described by the client table <c>GachaData</c> (nothing here is invented).</summary>
    /// <remarks>
    /// Field meaning, read from the table contents of 1189260 (the generated proto names are from an older build):
    /// <c>GachaStartScene</c> = rates per rank ("5:0.0189,4:0.13,3:0.8511"), <c>GachaStartTimeline</c> = character pool per
    /// rank, <c>GunUpRate</c> = weapon pool per rank, <c>WeaponUpItem</c>/<c>GunUpCharacter</c> = character "up" rate and list,
    /// <c>GunUpCharacterName</c>/<c>GunUpCharacterVideo</c> = weapon "up" rate and list, <c>SsrTimes</c>/<c>SrTimes</c> = pity.
    /// </remarks>
    public sealed record GachaBanner(
        uint Id,
        int Type,
        uint CostItem,
        int SsrPity,
        int SrPity,
        IReadOnlyDictionary<int, double> Rates,
        IReadOnlyDictionary<int, List<uint>> GunPool,
        IReadOnlyDictionary<int, List<uint>> WeaponPool,
        IReadOnlyDictionary<int, double> GunUpRates,
        IReadOnlyDictionary<int, List<uint>> UpGuns,
        IReadOnlyDictionary<int, double> WeaponUpRates,
        IReadOnlyDictionary<int, List<uint>> UpWeapons,
        IReadOnlyList<uint> Selectable,
        uint DefaultSelected);

    public sealed record GachaPull(uint ItemId, int Rank, bool IsUp);

    public sealed class GachaPity
    {
        public int SinceSsr { get; set; }
        public int SinceSr { get; set; }
        public int Total { get; set; }
        public int SsrCount { get; set; }

        /// <summary>The last 5-star was not the "up" unit, so the next one is guaranteed to be.</summary>
        public bool NextSsrGuaranteedUp { get; set; }

        /// <summary>The first 5-star of a selectable banner (the "首次保底必得自选对象 0/1" of the client) already gave the chosen unit (A-155).</summary>
        public bool SelectedGuaranteeDone { get; set; }
    }

    public interface IGachaService : IGameService
    {
        GachaBanner? GetBanner(uint gachaId);

        /// <summary>The unit the account chose in a selectable banner (the table default until it chooses); 0 when the banner is not selectable.</summary>
        uint GetSelection(uint accountUid, GachaBanner banner);

        /// <summary>Chooses the unit of a selectable banner; false when it is not one of its selectable units.</summary>
        bool SetSelection(uint accountUid, GachaBanner banner, uint item);

        /// <summary>Rolls <paramref name="count"/> pulls and updates the pity counters of the account for that banner.</summary>
        IReadOnlyList<GachaPull> Roll(uint accountUid, GachaBanner banner, int count, out GachaPity pity);

        /// <summary>
        /// Rolls the skin gashapon (banner type 9, <c>GachaClothesData</c>: 7 = up item per rank, 8 = rate of each up item, 9 = pool per rank, 10 = rate of
        /// each pool rank). ASSUMPTION A-27: no pity is applied (its column is not identified); the pull counter of the banner is kept for the cumulative rewards.
        /// </summary>
        IReadOnlyList<GachaPull> RollClothes(uint accountUid, uint gachaId, int count, out GachaPity pity);
    }

    /// <summary>
    /// Real banner roll: rank from the table rates, forced 5-star at the SSR pity and at least 4-star at the SR pity,
    /// "up" units at the table rates, otherwise uniform inside the rank pool. Pity counters survive restarts
    /// (stored per account and banner under the key <c>gacha.pity.&lt;id&gt;</c> in <c>AccountStates</c>).
    /// </summary>
    public sealed class GachaService(ITableService tableService, IAccountStateService accountState) : IGachaService
    {

        public GachaBanner? GetBanner(uint gachaId)
        {
            GachaData? row = tableService.GetTable<GachaData>().FirstOrDefault(g => g.Id == gachaId);
            if (row is null)
                return null;

            return new GachaBanner(
                row.Id,
                row.Type,
                row.Cost,
                (int)row.SsrTimes,
                (int)row.SrTimes,
                ParseRates(row.GachaStartScene),
                ParsePool(row.GachaStartTimeline),
                ParsePool(row.GunUpRate),
                ParseRates(row.WeaponUpItem),
                ParsePool(row.GunUpCharacter),
                ParseRates(row.GunUpCharacterName),
                ParsePool(row.GunUpCharacterVideo),
                TableFields.List(row, 36),
                TableFields.U(row, 37));
        }

        private static string SelectionKey(uint gachaId) => $"gacha.select.{gachaId}";

        public uint GetSelection(uint accountUid, GachaBanner banner)
        {
            if (banner.Selectable.Count == 0)
                return 0;

            uint chosen = accountState.Get<uint>(accountUid, SelectionKey(banner.Id));
            return banner.Selectable.Contains(chosen) ? chosen : banner.Selectable.Contains(banner.DefaultSelected) ? banner.DefaultSelected : banner.Selectable[0];
        }

        public bool SetSelection(uint accountUid, GachaBanner banner, uint item)
        {
            if (!banner.Selectable.Contains(item))
                return false;

            accountState.Set(accountUid, SelectionKey(banner.Id), item);
            return true;
        }

        public IReadOnlyList<GachaPull> Roll(uint accountUid, GachaBanner banner, int count, out GachaPity pity)
        {
            string key = $"gacha.pity.{banner.Id}";
            pity = accountState.Get<GachaPity>(accountUid, key) ?? new GachaPity();
            List<GachaPull> pulls = new(count);

            uint selected = GetSelection(accountUid, banner);
            for (int i = 0; i < count; i++)
                pulls.Add(RollOne(banner, pity, selected));

            accountState.Set(accountUid, key, pity);
            return pulls;
        }

        public IReadOnlyList<GachaPull> RollClothes(uint accountUid, uint gachaId, int count, out GachaPity pity)
        {
            string key = $"gacha.pity.{gachaId}";
            pity = accountState.Get<GachaPity>(accountUid, key) ?? new GachaPity();
            GachaClothesData? row = tableService.GetTable<GachaClothesData>().FirstOrDefault(r => TableFields.U(r, 1) == gachaId);
            if (row is null)
                return [];

            Dictionary<int, List<uint>> up = ParsePool(TableFields.S(row, 7));
            Dictionary<int, double> upRates = ParseRates(TableFields.S(row, 8));
            Dictionary<int, List<uint>> pools = ParsePool(TableFields.S(row, 9));
            Dictionary<int, double> poolRates = ParseRates(TableFields.S(row, 10));
            List<GachaPull> pulls = new(count);
            for (int i = 0; i < count; i++)
            {
                pity.Total++;
                pity.SinceSsr++;
                double roll = Random.Shared.NextDouble();
                double cumulative = 0;
                GachaPull? pull = null;
                foreach (int rank in upRates.Keys.OrderByDescending(k => k))
                {
                    cumulative += upRates[rank];
                    if (roll < cumulative && up.TryGetValue(rank, out List<uint>? items) && items.Count > 0)
                    {
                        pull = new GachaPull(items[Random.Shared.Next(items.Count)], rank, true);
                        break;
                    }
                }

                if (pull is null)
                {
                    foreach (int rank in poolRates.Keys.OrderByDescending(k => k))
                    {
                        cumulative += poolRates[rank];
                        if (roll < cumulative && pools.TryGetValue(rank, out List<uint>? items) && items.Count > 0)
                        {
                            pull = new GachaPull(items[Random.Shared.Next(items.Count)], rank, false);
                            break;
                        }
                    }
                }

                // rounding at the end of the rates: the lowest pool
                pull ??= pools.OrderBy(p => p.Key).First(p => p.Value.Count > 0) is var low ? new GachaPull(low.Value[Random.Shared.Next(low.Value.Count)], low.Key, false) : null;
                if (pull!.Rank >= 5)
                    pity.SinceSsr = 0;
                pulls.Add(pull);
            }

            accountState.Set(accountUid, key, pity);
            return pulls;
        }

        private static GachaPull RollOne(GachaBanner banner, GachaPity pity, uint selected)
        {
            pity.Total++;
            pity.SinceSsr++;
            pity.SinceSr++;

            int rank = PickRank(banner, pity);

            // Selectable banner: the FIRST 5-star of the banner is the unit the player chose (the client shows it as "首次保底必得自选对象 0/1"; changing the
            // choice does not reset the count). Before this was honoured the 5-star was a random one and the chosen unit never came (A-155).
            if (rank >= 5 && selected != 0 && !pity.SelectedGuaranteeDone)
            {
                pity.SsrCount++;
                pity.SinceSsr = 0;
                pity.SinceSr = 0;
                pity.SelectedGuaranteeDone = true;
                pity.NextSsrGuaranteedUp = false;
                return new GachaPull(selected, 5, true);
            }
            if (rank >= 5)
            {
                pity.SsrCount++;
                pity.SinceSsr = 0;
                pity.SinceSr = 0;
            }
            else if (rank == 4)
            {
                pity.SinceSr = 0;
            }

            bool wantUp = rank == 5 && pity.NextSsrGuaranteedUp;
            // On a selectable banner the "up" 5-star IS the unit the player chose (the table's own up list is the default one), so the 50/50 and the
            // guaranteed up after a lost 50/50 aim at it too (A-155).
            List<uint> upList = rank == 5 && selected != 0 ? [selected] : UpList(banner, rank);
            double upRate = Math.Max(
                banner.GunUpRates.GetValueOrDefault(rank),
                banner.WeaponUpRates.GetValueOrDefault(rank));

            bool up = upList.Count > 0 && (wantUp || Random.Shared.NextDouble() < upRate);
            uint itemId;
            if (up)
            {
                itemId = upList[Random.Shared.Next(upList.Count)];
            }
            else
            {
                List<uint> pool = Pool(banner, rank);
                if (pool.Count == 0)
                {
                    // a rank without entries in the table: fall back to the nearest lower rank that has some
                    for (int r = rank - 1; r >= 3 && pool.Count == 0; r--)
                        pool = Pool(banner, r);
                }

                if (pool.Count == 0)
                    throw new InvalidOperationException($"Banner {banner.Id} has no pool for rank {rank}.");

                itemId = pool[Random.Shared.Next(pool.Count)];
                up = upList.Contains(itemId);
            }

            if (rank == 5)
                pity.NextSsrGuaranteedUp = upList.Count > 0 && !up;

            return new GachaPull(itemId, rank, up);
        }

        private static int PickRank(GachaBanner banner, GachaPity pity)
        {
            if (banner.SsrPity > 0 && pity.SinceSsr >= banner.SsrPity)
                return 5;

            double roll = Random.Shared.NextDouble();
            double cumulative = 0;
            int rank = 3;
            foreach (int candidate in banner.Rates.Keys.OrderByDescending(k => k))
            {
                cumulative += banner.Rates[candidate];
                if (roll < cumulative)
                {
                    rank = candidate;
                    break;
                }
            }

            if (rank < 4 && banner.SrPity > 0 && pity.SinceSr >= banner.SrPity)
                rank = 4;

            return rank;
        }

        private static List<uint> UpList(GachaBanner banner, int rank)
        {
            List<uint> list = [];
            if (banner.UpGuns.TryGetValue(rank, out List<uint>? guns))
                list.AddRange(guns);
            if (banner.UpWeapons.TryGetValue(rank, out List<uint>? weapons))
                list.AddRange(weapons);
            return list;
        }

        private static List<uint> Pool(GachaBanner banner, int rank)
        {
            List<uint> list = [];
            if (banner.GunPool.TryGetValue(rank, out List<uint>? guns))
                list.AddRange(guns);
            if (banner.WeaponPool.TryGetValue(rank, out List<uint>? weapons))
                list.AddRange(weapons);
            return list;
        }

        /// <summary>The tables contain full-width separators in places (e.g. "11031：11043"); treat them as the ASCII ones.</summary>
        private static string Normalize(string? text) =>
            (text ?? string.Empty).Replace('：', ':').Replace('，', ',').Replace('；', ';');

        /// <summary>"5:0.0189,4:0.13,3:0.8511" → rank → value.</summary>
        internal static Dictionary<int, double> ParseRates(string? text)
        {
            Dictionary<int, double> result = [];
            foreach (string part in Normalize(text).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string[] pair = part.Split(':', StringSplitOptions.TrimEntries);
                if (pair.Length == 2
                    && int.TryParse(pair[0], out int rank)
                    && double.TryParse(pair[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                {
                    result[rank] = value;
                }
            }

            return result;
        }

        /// <summary>"5:1063:1015,4:1022:1026" → rank → ids.</summary>
        internal static Dictionary<int, List<uint>> ParsePool(string? text)
        {
            Dictionary<int, List<uint>> result = [];
            foreach (string part in Normalize(text).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string[] fields = part.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (fields.Length < 2 || !int.TryParse(fields[0], out int rank))
                    continue;

                List<uint> ids = [];
                foreach (string field in fields.Skip(1))
                {
                    if (uint.TryParse(field, out uint id))
                        ids.Add(id);
                    else
                        Log.Warning("Gacha pool entry '{Entry}' is not a number; ignored.", field);
                }

                result[rank] = ids;
            }

            return result;
        }
    }
}
