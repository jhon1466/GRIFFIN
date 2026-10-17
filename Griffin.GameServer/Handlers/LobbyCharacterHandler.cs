using Griffin.Common.Networking;
using Google.Protobuf;
using Griffin.Common.Proto;
using Griffin.Common.Protocol;
using Griffin.Common.Table;
using Griffin.Database.Services;
using Griffin.GameServer.Extensions;
using Griffin.GameServer.Services;

namespace Griffin.GameServer.Handlers
{
    /// <summary>
    /// Character stories of the lobby (<c>LobbyGunStoryData</c>: 1 = story, 3 = character, 5 = story that must be claimed first,
    /// 10 = reward). Assumptions: the first story of every character is claimed as in the info this server has always sent,
    /// the others are claimable once, in order, for characters the account owns; the reward is granted once and stored in
    /// <c>lobby.stories</c>; the unlock by affection level is not evaluated.
    /// </summary>
    public sealed class LobbyCharacterHandler(
        IAccountStateService accountState,
        IAccountService accountService,
        ITableService tableService,
        IRewardService rewardService) : LobbyCharacterHandlerBase
    {
        private const string StateKey = "lobby.stories";
        private const string UnlockedKey = "lobby.stories.unlocked";

        /// <summary>The client announces that an affection story became available: it is stored so the info keeps it as unlocked.</summary>
        public override void HandleLobbyCharacterUnlockStory(CS_LobbyCharacterUnlockStory request, Connection connection)
        {
            if (connection.Account is not null)
            {
                uint uid = connection.Account.Uid;
                List<uint> unlocked = accountState.Get<List<uint>>(uid, UnlockedKey) ?? [];
                if (tableService.GetTable<LobbyGunStoryData>().Any(r => TableFields.U(r, 1) == request.StoryId) && !unlocked.Contains(request.StoryId))
                {
                    unlocked.Add(request.StoryId);
                    accountState.Set(uid, UnlockedKey, unlocked);
                }
            }

            connection.Send(new SC_LobbyCharacterUnlockStory { StoryId = request.StoryId });
        }

        public override void HandleLobbyCharacterClaimReward(CS_LobbyCharacterClaimReward request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            IMessage? story = tableService.GetTable<LobbyGunStoryData>().FirstOrDefault(r => TableFields.U(r, 1) == request.StoryId);
            if (story is null)
            {
                connection.Refuse(MsgId.MsgCsLobbyCharacterClaimReward, HNINDAOJCKP.StoryConfigNotFound);
                return;
            }

            List<uint> claimed = accountState.Get<List<uint>>(uid, StateKey) ?? [];
            uint previous = TableFields.U(story, 5);
            bool PreviousClaimed() => previous == 0 || claimed.Contains(previous) || IsFirstStory(previous);
            if (claimed.Contains(request.StoryId) || IsFirstStory(request.StoryId))
            {
                connection.Refuse(MsgId.MsgCsLobbyCharacterClaimReward, HNINDAOJCKP.LobbyStoryClaimed);
                return;
            }

            if (!PreviousClaimed() || accountService.GetByUid(uid)?.Guns.Any(g => g.GunId == TableFields.U(story, 3)) != true)
            {
                connection.Refuse(MsgId.MsgCsLobbyCharacterClaimReward, HNINDAOJCKP.StoryLocked);
                return;
            }

            List<IMessage> messages = [];
            foreach ((uint item, uint count) in TableFields.Map(story, 10))
                rewardService.Grant(uid, item, (int)count, messages);

            claimed.Add(request.StoryId);
            accountState.Set(uid, StateKey, claimed);
            messages.Add(new SC_LobbyCharacterClaimReward { StoryId = request.StoryId });
            connection.SendAutoEncrypted(messages.ToArray());
        }

        /// <summary>The first story of each character (id ends with 0301) is the one the lobby info announces as already done.</summary>
        private static bool IsFirstStory(uint story) => story % 10000 == 301;


        public override void HandleLobbyCharacterInfo(CS_LobbyCharacterInfo request, Connection connection)
        {
            SC_LobbyCharacterInfo info = new()
            {
                ALPKEDLGCBP =
                {
                    [10010301u] = true,
                    [10080301u] = true,
                    [10090301u] = true,
                    [10130301u] = true,
                    [10150301u] = true,
                    [10170301u] = true,
                    [10210301u] = true,
                    [10220301u] = true,
                    [10230301u] = true,
                    [10240301u] = true,
                    [10250301u] = true,
                    [10260301u] = true,
                    [10270301u] = true,
                    [10280301u] = true,
                    [10290301u] = true,
                    [10320301u] = true,
                    [10330301u] = true,
                    [10340301u] = true,
                    [10350301u] = true,
                    [10360301u] = true,
                    [10370301u] = true,
                    [10380301u] = true,
                    [10390301u] = true,
                    [10400301u] = true,
                    [10410301u] = true,
                    [10420301u] = true,
                    [10430301u] = true,
                    [10440301u] = true,
                    [10450301u] = true,
                    [10470301u] = true,
                    [10480301u] = true,
                    [10490301u] = true,
                    [10500301u] = true,
                    [10510301u] = true,
                    [10520301u] = true,
                    [10530301u] = true,
                    [10540301u] = true,
                    [10550301u] = true,
                    [10560301u] = true,
                    [10570301u] = true,
                    [10580301u] = true,
                    [10590301u] = true,
                    [10600301u] = true,
                    [10610301u] = true,
                    [10620301u] = true,
                    [10630301u] = true,
                    [10640301u] = true,
                    [10650301u] = true,
                    [10660301u] = true,
                    [10670301u] = true,
                    [10680301u] = true,
                    [10690301u] = true,
                    [10700301u] = true,
                    [10710301u] = true,
                    [10720301u] = true,
                    [10730301u] = true,
                    [10740301u] = true,
                    [10750301u] = true,
                    [10760301u] = true,
                    [10770301u] = true,
                    [10780301u] = true,
                },
                NKDBHMMPFOO = { },
            };

            // stories claimed on this server are announced like the ones of the original state; the ones unlocked (LobbyCharacterUnlockStory)
            // and not claimed yet are announced with false (the client reads true = claimed, false = unlocked)
            if (connection.Account is not null)
            {
                foreach (uint story in accountState.Get<List<uint>>(connection.Account.Uid, UnlockedKey) ?? [])
                    info.ALPKEDLGCBP.TryAdd(story, false);
                foreach (uint story in accountState.Get<List<uint>>(connection.Account.Uid, StateKey) ?? [])
                    info.ALPKEDLGCBP[story] = true;
            }

            connection.Send(info);
        }
    }
}
