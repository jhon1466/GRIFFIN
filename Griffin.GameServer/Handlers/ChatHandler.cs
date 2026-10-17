using Microsoft.Extensions.Logging;
using Griffin.Command;
using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.GameServer.Services;

namespace Griffin.GameServer.Handlers
{
    public sealed class ChatHandler(ILogger<ChatHandler> logger, CommandDispatcher commandDispatcher, IAccountStateService accountState) : ChatHandlerBase
    {
        private const string HistoryKey = "social.chat.v1";
        private const int MaxHistoryPerConversation = 100;
        private static long _messageSequence;

        public override void HandleChat(CS_Chat request, Connection connection)
        {
            logger.LogInformation("Chat: {Request}", request);
            ChatMessage message = CreateMessage(request.Message ?? string.Empty, request.Emoji, active: true);
            SaveMessage(connection, request.Uid, message);

            // Intercept commands prefixed with '/'
            if (request.Message is { Length: > 1 } text && text.StartsWith('/'))
            {
                connection.Send(new SC_Chat { Uid = request.Uid, Message = message });

                commandDispatcher.Dispatch(
                    text[1..],
                    CommandSource.Client,
                    connection,
                    clientReplyCallback: reply =>
                    {
                        ChatMessage answer = CreateMessage(reply, emoji: 0, active: false);
                        SaveMessage(connection, request.Uid, answer);
                        connection.Send(new SC_Chat { Uid = request.Uid, Message = answer });
                    });
                return;
            }

            connection.Send(new SC_Chat { Uid = request.Uid, Message = message });
        }


        public override void HandleChatRead(CS_ChatRead request, Connection connection)
        {
            connection.Send(new SC_ChatRead
            {
                Uid = request.Uid,
            });
        }

        /// <summary>The built-in contact (UID 1) greets the commander the first time the conversation is opened.</summary>
        private const ulong BuiltInContactUid = 1;

        private const string WelcomeText = "¡Bienvenido a Griffin! Escribe /help para ver los comandos disponibles.";

        public override void HandleChatDetail(CS_ChatDetail request, Connection connection)
        {
            if (request.Uid == BuiltInContactUid)
                EnsureWelcome(connection);

            List<ChatMessage> messages = LoadMessages(connection, request.Uid);
            connection.Send(new SC_ChatDetail
            {
                Uid = request.Uid,
                Chat = new Chat
                {
                    CounterType = JCODPDKNMBI.CounterDefault,
                    LastId = DPKCGBBDMAM.ResetNone,
                    PJBOAGJCMKK = KCCBHFPHCPD.Default,
                    AJNAPNNKIFC = false,
                },
                History = new FNMCGHAOALB
                {
                    KCBKEINNPGK = { messages },
                },
            });
        }

        private static ChatMessage CreateMessage(string text, uint emoji, bool active)
        {
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return new ChatMessage
            {
                Id = nowMs * 1000 + Interlocked.Increment(ref _messageSequence) % 1000,
                Active = active,
                Message = text,
                Emoji = emoji,
                Ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };
        }

        /// <summary>Puts the greeting at the top of the conversation with the built-in contact when it is not there yet (also for accounts that already chatted).</summary>
        private void EnsureWelcome(Connection connection)
        {
            if (connection.Account is null)
                return;

            Dictionary<string, List<StoredChatMessage>> conversations =
                accountState.Get<Dictionary<string, List<StoredChatMessage>>>(connection.Account.Uid, HistoryKey) ?? [];
            string peer = BuiltInContactUid.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!conversations.TryGetValue(peer, out List<StoredChatMessage>? history))
                conversations[peer] = history = [];

            if (history.Any(m => m.Text == WelcomeText))
                return;

            ChatMessage welcome = CreateMessage(WelcomeText, emoji: 0, active: false);
            long first = history.Count > 0 ? history.Min(m => m.Timestamp) : welcome.Ts;
            history.Insert(0, new StoredChatMessage(welcome.Id, false, WelcomeText, 0, Math.Min(first, welcome.Ts) - 1));
            accountState.Set(connection.Account.Uid, HistoryKey, conversations);
        }

        private void SaveMessage(Connection connection, ulong peerUid, ChatMessage message)
        {
            if (connection.Account is null)
                return;

            Dictionary<string, List<StoredChatMessage>> conversations =
                accountState.Get<Dictionary<string, List<StoredChatMessage>>>(connection.Account.Uid, HistoryKey) ?? [];
            string peer = peerUid.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!conversations.TryGetValue(peer, out List<StoredChatMessage>? history))
                conversations[peer] = history = [];

            history.Add(new StoredChatMessage(message.Id, message.Active, message.Message, message.Emoji, message.Ts));
            if (history.Count > MaxHistoryPerConversation)
                history.RemoveRange(0, history.Count - MaxHistoryPerConversation);

            accountState.Set(connection.Account.Uid, HistoryKey, conversations);
        }

        private List<ChatMessage> LoadMessages(Connection connection, ulong peerUid)
        {
            if (connection.Account is null)
                return [];

            string peer = peerUid.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Dictionary<string, List<StoredChatMessage>> conversations =
                accountState.Get<Dictionary<string, List<StoredChatMessage>>>(connection.Account.Uid, HistoryKey) ?? [];
            return conversations.GetValueOrDefault(peer, []).Select(stored => new ChatMessage
            {
                Id = stored.Id,
                Active = stored.Active,
                Message = stored.Text,
                Emoji = stored.Emoji,
                Ts = stored.Timestamp,
            }).ToList();
        }

        private sealed record StoredChatMessage(long Id, bool Active, string Text, uint Emoji, long Timestamp);
    }
}
