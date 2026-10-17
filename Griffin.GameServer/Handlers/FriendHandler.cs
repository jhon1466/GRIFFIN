using Google.Protobuf;
using Google.Protobuf.Collections;
using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.Common.Protocol;
using Griffin.Database.Entities;
using Griffin.Database.Services;
using Griffin.GameServer.Extensions;
using Griffin.GameServer.Services;

namespace Griffin.GameServer.Handlers
{
    public sealed class FriendHandler(
        IAccountStateService accountState,
        IAccountService accounts,
        IProfileService profiles) : FriendHandlerBase
    {
        private const string FriendsKey = "social.friends.v1";
        private const string ApplicationsKey = "social.friend-applications.v1";
        private const string MarksKey = "social.friend-marks.v1";
        private const string SubscriptionsKey = "social.friend-subscriptions.v1";

        public override void HandleFriends(CS_Friends request, Connection connection)
        {
            SC_Friends response = new SC_Friends
            {
                Marks = { Load<Dictionary<ulong, string>>(connection, MarksKey) },
                MIBABEHJCIK = { Load<Dictionary<ulong, bool>>(connection, SubscriptionsKey) },
                BHJLKLNBAFO = { },
            };
            FillFriends(response.Friends, connection);
            connection.SendAutoEncrypted(response);
        }

        public override void HandleFriendApplyList(CS_FriendApplyList request, Connection connection)
        {
            if (connection.Account is null)
                return;

            SC_FriendApplyList response = new();
            foreach (ulong uid in Load<List<ulong>>(connection.Account.Uid, ApplicationsKey))
                if (FindFriend(uid) is { } applicant)
                    response.Apps.Add(applicant.User);
            connection.Send(response);
        }

        public override void HandleRefreshFriends(CS_RefreshFriends request, Connection connection)
        {
            SC_RefreshFriends response = new SC_RefreshFriends
            {
                Marks = { Load<Dictionary<ulong, string>>(connection, MarksKey) },
                MIBABEHJCIK = { Load<Dictionary<ulong, bool>>(connection, SubscriptionsKey) },
            };
            FillFriends(response.Friends, connection);
            connection.SendAutoEncrypted(response);
        }

        public override void HandleFriendSearch(CS_FriendSearch request, Connection connection)
        {
            Friend? friend = FindFriend(request.Uid);
            connection.Send(new SC_FriendSearch { Friend = friend?.User ?? new BinaryUser() });
        }

        public override void HandleFriendRecommend(CS_FriendRecommend request, Connection connection) =>
            connection.Send(new SC_FriendRecommend());

        public override void HandleFriendSetMark(CS_FriendSetMark request, Connection connection)
        {
            Dictionary<ulong, string> marks = Load<Dictionary<ulong, string>>(connection, MarksKey);
            if (!IsFriend(connection, request.Uid))
            {
                connection.Refuse(MsgId.MsgCsFriendSetMark, HNINDAOJCKP.FriendIsNotEstablished);
                return;
            }

            if (string.IsNullOrWhiteSpace(request.Mark)) marks.Remove(request.Uid);
            else marks[request.Uid] = request.Mark;
            Save(connection, MarksKey, marks);
            connection.Send(new SC_FriendSetMark { Uid = request.Uid, Mark = request.Mark });
        }

        public override void HandleFriendSubscribe(CS_FriendSubscribe request, Connection connection)
        {
            Dictionary<ulong, bool> subscriptions = Load<Dictionary<ulong, bool>>(connection, SubscriptionsKey);
            if (!IsFriend(connection, request.Uid))
            {
                connection.Refuse(MsgId.MsgCsFriendSubscribe, HNINDAOJCKP.FriendIsNotEstablished);
                return;
            }

            if (request.Active) subscriptions[request.Uid] = true;
            else subscriptions.Remove(request.Uid);
            Save(connection, SubscriptionsKey, subscriptions);
            connection.Send(new SC_FriendSubscribe());
        }

        public override void HandleFriendDelete(CS_FriendDelete request, Connection connection)
        {
            if (connection.Account is null)
                return;

            if (CreateLocalFriend(request.Uid) is not null)
            {
                // Built-in local contacts include the command-chat identity. They are
                // not real remote accounts, so deleting them would break /inventory.
                connection.Refuse(MsgId.MsgCsFriendDelete, HNINDAOJCKP.FriendDeleteStranger);
                return;
            }

            if (request.Uid > uint.MaxValue || !IsFriend(connection, request.Uid))
            {
                connection.Refuse(MsgId.MsgCsFriendDelete, HNINDAOJCKP.FriendIsNotEstablished);
                return;
            }

            uint ownerUid = connection.Account.Uid;
            uint friendUid = (uint)request.Uid;
            List<ulong> ownFriends = Load<List<ulong>>(ownerUid, FriendsKey);
            ownFriends.Remove(friendUid);
            Save(ownerUid, FriendsKey, ownFriends);
            if (accounts.GetByUid(friendUid) is not null)
            {
                List<ulong> otherFriends = Load<List<ulong>>(friendUid, FriendsKey);
                otherFriends.Remove(ownerUid);
                Save(friendUid, FriendsKey, otherFriends);
            }
            connection.Send(new SC_FriendDelete { Uid = request.Uid });
        }

        public override void HandleFriendApply(CS_FriendApply request, Connection connection) =>
            ApplyToAccount(request, connection);

        public override void HandleFriendApproveApplication(CS_FriendApproveApplication request, Connection connection) =>
            DecideApplication(request, connection);

        private void ApplyToAccount(CS_FriendApply request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint requesterUid = connection.Account.Uid;
            if (request.Uid == requesterUid)
            {
                connection.Refuse(MsgId.MsgCsFriendApply, HNINDAOJCKP.ClientInputInvalid);
                return;
            }

            if (FindFriend(request.Uid) is null)
            {
                connection.Refuse(MsgId.MsgCsFriendApply, HNINDAOJCKP.FriendSearchFirst);
                return;
            }

            if (IsFriend(connection, request.Uid))
            {
                connection.Refuse(MsgId.MsgCsFriendApply, HNINDAOJCKP.FriendIsEstablished);
                return;
            }

            if (request.Uid > uint.MaxValue)
            {
                connection.Refuse(MsgId.MsgCsFriendApply, HNINDAOJCKP.ClientInputInvalid);
                return;
            }

            uint targetUid = (uint)request.Uid;
            if (accounts.GetByUid(targetUid) is null)
            {
                connection.Refuse(MsgId.MsgCsFriendApply, HNINDAOJCKP.FriendSearchFirst);
                return;
            }

            List<ulong> inbound = Load<List<ulong>>(targetUid, ApplicationsKey);
            if (!inbound.Contains(requesterUid))
            {
                inbound.Add(requesterUid);
                Save(targetUid, ApplicationsKey, inbound);
            }

            connection.Send(new SC_FriendApply { TargetUid = request.Uid });
        }

        private void DecideApplication(CS_FriendApproveApplication request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint targetUid = connection.Account.Uid;
            if (request.Uid > uint.MaxValue)
            {
                connection.Refuse(MsgId.MsgCsFriendApproveApplication, HNINDAOJCKP.FriendApplicationNotFound);
                return;
            }

            uint applicantUid = (uint)request.Uid;
            List<ulong> inbound = Load<List<ulong>>(targetUid, ApplicationsKey);
            if (!inbound.Remove(applicantUid))
            {
                connection.Refuse(MsgId.MsgCsFriendApproveApplication, HNINDAOJCKP.FriendApplicationNotFound);
                return;
            }

            Save(targetUid, ApplicationsKey, inbound);
            FriendDealResult result = FriendDealResult.Reject;
            if (request.Deal && accounts.GetByUid(applicantUid) is not null)
            {
                AddFriend(targetUid, applicantUid);
                AddFriend(applicantUid, targetUid);
                result = FriendDealResult.Pass;
            }

            connection.Send(new SC_FriendApproveApplication { Uid = request.Uid, Result = result });
        }

        private void AddFriend(uint ownerUid, uint friendUid)
        {
            List<ulong> friends = Load<List<ulong>>(ownerUid, FriendsKey);
            if (!friends.Contains(friendUid))
            {
                friends.Add(friendUid);
                Save(ownerUid, FriendsKey, friends);
            }
        }

        private T Load<T>(Connection connection, string key) where T : new() =>
            connection.Account is null ? new T() : Load<T>(connection.Account.Uid, key);

        private T Load<T>(uint uid, string key) where T : new()
        {
            return accountState.Get<T>(uid, key) ?? new T();
        }

        private void Save<T>(Connection connection, string key, T value)
        {
            if (connection.Account is not null)
                accountState.Set(connection.Account.Uid, key, value);
        }

        private void Save<T>(uint uid, string key, T value) => accountState.Set(uid, key, value);

        private bool IsFriend(Connection connection, ulong uid) =>
            CreateLocalFriend(uid) is not null
            || (uid <= uint.MaxValue && connection.Account is not null
                && Load<List<ulong>>(connection.Account.Uid, FriendsKey).Contains(uid));

        private Friend? FindFriend(ulong uid)
        {
            Friend? local = CreateLocalFriend(uid);
            if (local is not null)
                return local;
            if (uid > uint.MaxValue)
                return null;

            AccountEntity? account = accounts.GetByUid((uint)uid);
            if (account is null)
                return null;

            ProfileState profile = profiles.Get(account.Uid);
            User user = new()
            {
                Uid = account.Uid,
                Name = string.IsNullOrWhiteSpace(profile.Name) ? "griffin" : profile.Name,
                Level = 60,
                Sex = Sex.Female,
                Birthday = 101,
                Portrait = 21089,
                PortraitFrame = 24164,
                GunNum = (uint)account.Guns.Count,
                GuildId = 123123,
                GuildName = "Griffin",
            };
            profiles.ApplyTo(account.Uid, user);
            return new Friend
            {
                Id = uid,
                User = new BinaryUser
                {
                    Data = user.ToByteString(),
                    IHNCKKDOJLL = new MNCIHOEMEMP { IKDMNOBMIFE = 1 },
                    KAEDHCMMJKP = new MPHCOCKNKGA(),
                },
            };
        }

        private static Friend? CreateLocalFriend(ulong id)
        {
            return id switch
            {
                1 => CreateFriendBrief(1, "Nikketa (妮基塔)", 21089, 24023),
                _ => null,
            };
        }

        private void FillFriends(MapField<ulong, Friend> friends, Connection connection)
        {
            friends[1UL] = CreateLocalFriend(1)!;
            if (connection.Account is null)
                return;

            foreach (ulong uid in Load<List<ulong>>(connection.Account.Uid, FriendsKey))
                if (FindFriend(uid) is { } friend)
                    friends[uid] = friend;
        }

        private static Friend CreateFriendBrief(ulong id, string name, uint avatarId, uint avatarFrameId)
        {
            User user = new User
            {
                Uid = id,
                Name = name,
                Level = 60,
                Sex = Sex.Female,
                Birthday = 709,
                Portrait = avatarId,
                PortraitFrame = avatarFrameId,
                Status = new User.Types.LoginStatus
                {
                    Online = true,
                    LoginTime = 1,
                    LogoutTime = 2,
                    SyncTime = 3,
                },
                Title = 23087,
                Medal = 22004,
                MaxStage = 30465,
                AchievementLevel = 1,
                CreatTime = 1,
                GunNum = 56,
                Assistants =
                {
                    CreateAssistant(0, 1047, 60, 1104700, 1),
                    CreateAssistant(1, 1032, 60, 1103200, 2),
                    CreateAssistant(2, 1025, 60, 1102500, 3, grade: 2),
                },
                IDMOCOHNLDO = new User.Types.JMMLGEDCIGB
                {
                    JBMNHBBGAHP = false,
                    OHPHNKCEGCM = 1,
                    GPJEDMDBIIJ = 1,
                },
                MPDCKNHELFH = (POAMOPPDEJC)19,
                HEEDJKIDCLI = new User.Types.KEKFMLHLAMN(),
                PMJNEPPFAKF = new User.Types.IBGENNDHELL
                {
                    IKDMNOBMIFE = 1,
                },
            };

            return new Friend
            {
                Id = id,
                User = new BinaryUser
                {
                    Data = user.ToByteString(),
                    IHNCKKDOJLL = new MNCIHOEMEMP
                    {
                        IKDMNOBMIFE = 1,
                    },
                    KAEDHCMMJKP = new MPHCOCKNKGA(),
                },
            };
        }

        private static BJMBKFICIAC CreateAssistant(int idx, uint gunId, uint level, uint costumeId, uint gpjl, uint grade = 0)
        {
            return new BJMBKFICIAC
            {
                Idx = idx,
                BKGHHPKAKBL = new GunAvatar
                {
                    Id = gunId,
                    Level = level,
                    Grade = grade,
                    CostumeId = costumeId,
                    EFKCPDGOHPA = gpjl,
                },
            };
        }
    }
}
