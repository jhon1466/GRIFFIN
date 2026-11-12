using System.Text.RegularExpressions;
using Google.Protobuf;
using Griffin.Common.Networking;
using Griffin.Common.Packets;
using Griffin.Common.Proto;

namespace Griffin.GameServer.Services
{
    /// <summary>
    /// Every claim of the game (daily sign-in, battle pass, chapter/achievement/collection/event rewards, gift boxes...) shows the client's
    /// "obtained" window, built from SC_DropCollect, which must come before the claim answer. Handlers that already build it (store, quests,
    /// mail) keep their own; for the rest the dispatcher records what <see cref="IRewardService.Grant"/> handed out and adds the window and
    /// the live item counts to the first message sent after the grant (A-209).
    /// </summary>
    public static class AutoRewardWindow
    {
        private static readonly Regex Claims = new(
            @"Reward|Claim|CheckIn|Checkin|Bonus|^Take|GainSupply|Collect(One|All)$|DailyFood|^UseItems$|^UseGiftPick$|^UseCustomizedGift$|^GroupMailReceive$|^Darkov(Receive|GetLastSeason)",
            RegexOptions.Compiled);

        private static readonly Regex NotClaims = new(@"Info$|Message|Words|Options|Slots|Over$|^Get(Activity)?CheckIn|Settle|Gacha(?!Cumulative|GetPhase)", RegexOptions.Compiled);

        public static bool IsClaim(string name) => Claims.IsMatch(name) && !NotClaims.IsMatch(name);

        public static void Install() => PacketDispatcher.RequestScope = (connection, name) => IsClaim(name) ? new Scope(connection, name) : null;

        private sealed class Scope : IDisposable
        {
            private readonly Connection connection;
            private readonly string reply;
            private readonly RewardDrops drops = RewardDrops.Begin();
            private readonly Func<Connection, IMessage[], IMessage[]>? previous = Connection.OutgoingFilter;

            public Scope(Connection connection, string name)
            {
                this.connection = connection;
                reply = "SC_" + name;
                Connection.OutgoingFilter = Filter;
            }

            private IMessage[] Filter(Connection target, IMessage[] batch)
            {
                if (previous is { } outer)
                    batch = outer(target, batch);
                if (!ReferenceEquals(target, connection) || drops.Items.Count == 0)
                    return batch;
                if (batch.Any(message => message is SC_DropCollect))
                {
                    drops.Items.Clear();
                    return batch;
                }

                List<IMessage> withFeedback = [.. batch];
                PurchaseFeedback.Add(withFeedback, drops.Items);
                drops.Items.Clear();
                List<IMessage> feedback = withFeedback.GetRange(batch.Length, withFeedback.Count - batch.Length);
                int replyAt = Array.FindIndex(batch, message => message.Descriptor.Name == reply);
                List<IMessage> result = [.. batch];
                result.InsertRange(replyAt < 0 ? result.Count : replyAt, feedback);
                return [.. result];
            }

            public void Dispose()
            {
                Connection.OutgoingFilter = previous;
                drops.Dispose();
                if (drops.Items.Count == 0)
                    return;
                List<IMessage> feedback = [];
                PurchaseFeedback.Add(feedback, drops.Items);
                drops.Items.Clear();
                if (feedback.Count > 0)
                    connection.SendAutoEncrypted(feedback.ToArray());
            }
        }
    }
}
