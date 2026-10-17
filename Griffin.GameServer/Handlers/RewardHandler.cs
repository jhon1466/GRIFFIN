using Google.Protobuf;
using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.Common.Protocol;
using Griffin.GameServer.Extensions;
using Griffin.GameServer.Services;

namespace Griffin.GameServer.Handlers
{
    /// <summary>
    /// Reward claims that the protocol groups in one handler base. Battle pass levels are claimed here (the rules are in
    /// <see cref="IBattlepassService"/>); each claim is granted once and answered with the item updates and then the reply.
    /// </summary>
    public sealed class RewardHandler(IBattlepassService battlepass, IExThemeService exThemes) : RewardHandlerBase
    {
        // The optional reward of a banner needs a counter this server does not keep (GetGachaCounter is announced empty),
        // so it can never be claimed.
        public override void HandleGetGachaOptionalReward(CS_GetGachaOptionalReward request, Connection connection) =>
            connection.Refuse(MsgId.MsgCsGetGachaOptionalReward, HNINDAOJCKP.GachaOptionCannotGet);

        /// <summary>Picks the reward of an EX combat theme (see <see cref="ExHandler"/>), once per theme.</summary>
        public override void HandleExPickThemeReward(CS_ExPickThemeReward request, Connection connection)
        {
            if (connection.Account is null)
                return;

            List<IMessage> messages = [];
            Dictionary<uint, uint>? rewards = exThemes.Pick(connection.Account.Uid, request.ThemeId, messages);
            if (rewards is null)
            {
                connection.Refuse(MsgId.MsgCsExPickThemeReward, HNINDAOJCKP.ItemConfigNotExist);
                return;
            }

            SC_ExPickThemeReward reply = new();
            foreach ((uint item, uint count) in rewards)
                reply.Rewards[item] = count;
            messages.Add(reply);
            connection.SendAutoEncrypted(messages.ToArray());
        }

        public override void HandleGetBattlepassReward(CS_GetBattlepassReward request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            List<IMessage> messages = [];
            bool granted = request.RewardType == BpRewardGetType.GetTypeExtra
                ? battlepass.ClaimExtra(uid, messages) > 0
                : battlepass.Claim(uid, request.RewardLevel, request.Type, messages, out _);
            if (!granted)
            {
                connection.Refuse(MsgId.MsgCsGetBattlepassReward, request.RewardType == BpRewardGetType.GetTypeExtra ? HNINDAOJCKP.BattlepassAllGeted : HNINDAOJCKP.BattlepassGeted);
                return;
            }

            messages.Add(new SC_GetBattlepassReward
            {
                Type = request.Type,
                RewardLevel = request.RewardLevel,
                LeftRewardNum = battlepass.LeftRewards(uid),
                RewardType = request.RewardType,
            });
            connection.SendAutoEncrypted(messages.ToArray());
        }

        // Adjutant reward pick: the server keeps no adjutant reward pool, so the pick is confirmed and nothing is granted.
        public override void HandleAssistantRewardPick(CS_AssistantRewardPick request, Connection connection) =>
            connection.Send(new SC_AssistantRewardPick());
    }
}
