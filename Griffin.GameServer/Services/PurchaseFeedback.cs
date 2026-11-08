using Google.Protobuf;
using Griffin.Common.Proto;

namespace Griffin.GameServer.Services
{
    /// <summary>
    /// What the client needs after ANY purchase (store, batch, black market, skins, event shops...), as found in client 4.0.5142 (A-200):
    /// <list type="bullet">
    /// <item>SC_Items only stores the new counts; SC_UpdateItem also raises client event 5000, which the open screens (shop currency) listen to.</item>
    /// <item>The "obtained" window is built from SC_DropCollect (ECDDLOFFOAM.GJJGLBKHGJP); it must come before the purchase answer.</item>
    /// </list>
    /// </summary>
    public static class PurchaseFeedback
    {
        /// <summary>Adds the live item updates for every SC_Items in <paramref name="messages"/> and one drop window with <paramref name="rewards"/>.</summary>
        public static void Add(List<IMessage> messages, IEnumerable<(uint ItemId, int Count)> rewards)
        {
            Dictionary<uint, int> latest = [];
            foreach (IMessage message in messages)
                if (message is SC_Items items)
                    foreach (KeyValuePair<uint, int> item in items.Items)
                        latest[item.Key] = item.Value;
            foreach (KeyValuePair<uint, int> item in latest)
                messages.Add(new SC_UpdateItem { ItemId = item.Key, ItemNum = item.Value });

            SC_DropCollect drops = new();
            foreach (IGrouping<uint, (uint ItemId, int Count)> reward in rewards.Where(r => r.ItemId != 0 && r.Count > 0).GroupBy(r => r.ItemId))
                drops.Drops.Add(new UserDropCache { ItemId = reward.Key, ItemNum = (uint)reward.Sum(r => r.Count) });
            if (drops.Drops.Count > 0)
                messages.Add(drops);
        }
    }
}
