using Google.Protobuf;
using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.Common.Protocol;
using Griffin.GameServer.Extensions;
using Griffin.GameServer.Services;
using Serilog;

namespace Griffin.GameServer.Handlers
{
    /// <summary>
    /// Per-account mailbox stored in <c>AccountStates</c> (key <c>mail.inbox</c>). A new account gets one welcome mail with an
    /// starter-kit attachment (weapon EXP blueprints, class promotion materials and gacha permits, which a new account would otherwise not have). Attachments are delivered once: claiming a mail that is
    /// already claimed never grants anything again. The official mail contents are not available, so none are simulated.
    /// </summary>
    public sealed class MailHandler(IAccountStateService accountState, IRewardService rewardService) : MailHandlerBase
    {
        private const string InboxKey = "mail.inbox";
        private const long MailLifetimeSeconds = 30L * 24 * 3600;

        public sealed class MailRecord
        {
            public uint Id { get; set; }
            public string Title { get; set; } = string.Empty;
            public string Content { get; set; } = string.Empty;
            public Dictionary<uint, uint> Items { get; set; } = [];
            public bool Read { get; set; }
            public bool Attached { get; set; }
            public long ReceiveTime { get; set; }
            public long ExpireTime { get; set; }
        }

        public override void HandleMails(CS_Mails request, Connection connection)
        {
            if (connection.Account is null)
                return;

            SC_Mails response = new() { Active = true };
            foreach (MailRecord mail in Inbox(connection.Account.Uid))
                response.TotalMails.Add(ToProto(mail));

            connection.Send(response);
        }

        public override void HandleMailDetail(CS_MailDetail request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            List<MailRecord> inbox = Inbox(uid);
            MailRecord? mail = inbox.FirstOrDefault(m => m.Id == request.MailId);
            if (mail is null)
            {
                Log.Warning("Mail {MailId} not found for account {Uid}.", request.MailId, uid);
                connection.Refuse(MsgId.MsgCsMailDetail, HNINDAOJCKP.MailNotFound);
                return;
            }

            if (!mail.Read)
            {
                mail.Read = true;
                accountState.Set(uid, InboxKey, inbox);
            }

            connection.Send(new SC_MailDetail { Mail = ToProto(mail), Type = request.Type });
        }

        public override void HandleMailGetAttachment(CS_MailGetAttachment request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            List<MailRecord> inbox = Inbox(uid);
            MailRecord? mail = inbox.FirstOrDefault(m => m.Id == request.Id);
            if (mail is null)
            {
                Log.Warning("Mail attachment {MailId} requested by account {Uid} but the mail does not exist.", request.Id, uid);
                connection.Refuse(MsgId.MsgCsMailGetAttachment, HNINDAOJCKP.MailNotFound);
                return;
            }

            List<IMessage> messages = [];
            Claim(uid, mail, messages);
            accountState.Set(uid, InboxKey, inbox);

            messages.Add(new SC_MailGetAttachment { Id = mail.Id, Type = request.Type, Attached = mail.Attached });
            connection.SendAutoEncrypted(messages.ToArray());
        }

        public override void HandleMailGetAttachments(CS_MailGetAttachments request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            List<MailRecord> inbox = Inbox(uid);
            List<IMessage> messages = [];
            SC_MailGetAttachments response = new();

            foreach (MailRecord mail in inbox.Where(m => !m.Attached && m.Items.Count > 0))
            {
                Claim(uid, mail, messages);
                response.IdList.Add(mail.Id);
            }

            accountState.Set(uid, InboxKey, inbox);
            messages.Add(response);
            connection.SendAutoEncrypted(messages.ToArray());
        }

        public override void HandleMailDel(CS_MailDel request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            List<MailRecord> inbox = Inbox(uid);
            SC_MailDel response = new();

            foreach (uint id in request.IdList)
            {
                MailRecord? mail = inbox.FirstOrDefault(m => m.Id == id);
                if (mail is null)
                    continue;

                // a mail with an unclaimed attachment is kept: deleting it would silently destroy the reward
                if (mail.Items.Count > 0 && !mail.Attached)
                {
                    Log.Information("Mail {MailId} of account {Uid} has an unclaimed attachment; not deleted.", id, uid);
                    continue;
                }

                inbox.Remove(mail);
                response.IdList.Add(id);
            }

            accountState.Set(uid, InboxKey, inbox);
            connection.Send(response);
        }

        private void Claim(uint uid, MailRecord mail, List<IMessage> messages)
        {
            if (mail.Attached || mail.Items.Count == 0)
                return;

            foreach ((uint itemId, uint count) in mail.Items)
                rewardService.Grant(uid, itemId, (int)count, messages);

            mail.Attached = true;
            mail.Read = true;
            Log.Information("Account {Uid} claimed mail {MailId}: {Items}", uid, mail.Id,
                string.Join(",", mail.Items.Select(pair => $"{pair.Key}x{pair.Value}")));
        }

        private List<MailRecord> Inbox(uint uid)
        {
            List<MailRecord>? inbox = accountState.Get<List<MailRecord>>(uid, InboxKey);
            if (inbox is not null)
                return inbox;

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            inbox =
            [
                new MailRecord
                {
                    Id = 1,
                    Title = "Welcome to the private server",
                    Content = "Starter kit: 10 interview permits, 400000 analysis blueprints (weapon EXP) and 100 of each class promotion material so level-ups and promotions can be tried. Claiming it twice does nothing.",
                    Items = new Dictionary<uint, uint> { [45] = 10, [8301] = 400000, [8328] = 100, [8329] = 100, [8330] = 100, [8331] = 100 },
                    ReceiveTime = now,
                    ExpireTime = now + MailLifetimeSeconds,
                },
            ];
            accountState.Set(uid, InboxKey, inbox);
            return inbox;
        }

        private static Mail ToProto(MailRecord mail)
        {
            Mail proto = new()
            {
                Id = mail.Id,
                Type = MailType.Gm,
                Title = mail.Title,
                Content = mail.Content,
                Read = mail.Read,
                Attached = mail.Attached,
                ReceiveTime = mail.ReceiveTime,
                ExpireTime = mail.ExpireTime,
            };

            foreach ((uint itemId, uint count) in mail.Items)
                proto.AttachmentItems[itemId] = count;

            return proto;
        }
    }
}
