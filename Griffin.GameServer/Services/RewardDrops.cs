namespace Griffin.GameServer.Services
{
    /// <summary>
    /// Records what <see cref="IRewardService.Grant"/> hands out while a claim is handled, so the handler can show the client's
    /// "obtained" window (SC_DropCollect, see <see cref="PurchaseFeedback"/>) for quests, passes, sign-ins and chests alike (A-206).
    /// Handlers run synchronously on one thread, so the scope is thread-static.
    /// </summary>
    public sealed class RewardDrops : IDisposable
    {
        [ThreadStatic] internal static List<(uint ItemId, int Count)>? current;
        [ThreadStatic] internal static int depth;

        private readonly List<(uint ItemId, int Count)>? previous;
        public List<(uint ItemId, int Count)> Items { get; } = [];

        private RewardDrops()
        {
            previous = current;
            current = Items;
        }

        /// <summary>Starts recording; dispose (or <see cref="Finish"/>) to stop.</summary>
        public static RewardDrops Begin() => new();

        /// <summary>Adds the live item updates and the "obtained" window for what was granted, then stops recording.</summary>
        public void Finish(List<Google.Protobuf.IMessage> messages)
        {
            Dispose();
            PurchaseFeedback.Add(messages, Items);
        }

        public void Dispose()
        {
            if (ReferenceEquals(current, Items))
                current = previous;
        }
    }
}
