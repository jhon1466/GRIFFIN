using System.Text.RegularExpressions;
using Griffin.Common.Table;
using Griffin.Database.Entities;
using Griffin.Database.Services;

namespace Griffin.GameServer.Services
{
    /// <summary>
    /// Event tickets (ItemData type 130, e.g. 179203 准入密钥 that opens the supply stages of the event). Their column 18 is
    /// "3:3:6" on every one of them: read as 3 more each day, up to 6 held (the item window shows "storage limit 6"). Column 19 is
    /// the expiry of the event. The server never handed them out, so the supply mode could not be played. ASSUMPTION A-180:
    /// the refill happens on the first item list of each game day (05:00 UTC+8), adding 3 per day elapsed, capped at 6.
    /// </summary>
    public interface IEventTicketService : IGameService
    {
        void Refill(uint accountUid);
    }

    public sealed partial class EventTicketService(
        ITableService tableService,
        IAccountStateService accountState,
        IInventoryService inventoryService,
        IItemService itemService) : IEventTicketService
    {
        private const int TicketType = 130;
        private const string StateKey = "eventtickets.refill";

        [GeneratedRegex(@"^\s*(\d+):(\d+):(\d+)\s*$")]
        private static partial Regex Rule();

        /// <summary>Game day (resets at 05:00 China time).</summary>
        private static DateOnly Today() => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8 - 5));

        public void Refill(uint accountUid)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            DateOnly today = Today();
            lock (AccountLock.For(accountUid))
            {
                Dictionary<uint, string> last = accountState.Get<Dictionary<uint, string>>(accountUid, StateKey) ?? [];
                bool changed = false;
                foreach (ItemData row in tableService.GetTable<ItemData>().Where(i => i.Type == TicketType))
                {
                    Match m = Rule().Match(TableFields.S(row, 18));
                    if (!m.Success)
                        continue;
                    ulong expiry = TableFields.U64(row, 19);
                    if (expiry != 0 && (ulong)now > expiry)
                        continue;

                    int daily = int.Parse(m.Groups[2].Value);
                    int cap = int.Parse(m.Groups[3].Value);
                    int days = last.TryGetValue(row.Id, out string? seen) && DateOnly.TryParse(seen, out DateOnly day)
                        ? today.DayNumber - day.DayNumber
                        : 1;
                    if (days <= 0)
                        continue;

                    int owned = (int)(inventoryService.GetPlayerInventory<ItemEntity>(accountUid).FirstOrDefault(i => i.ItemId == row.Id)?.Count ?? 0);
                    int add = Math.Min(cap - owned, daily * days);
                    if (add > 0)
                        itemService.Grant(accountUid, row.Id, TicketType, add);
                    last[row.Id] = today.ToString("yyyy-MM-dd");
                    changed = true;
                }

                if (changed)
                    accountState.Set(accountUid, StateKey, last);
            }
        }
    }
}
