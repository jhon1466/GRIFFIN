using System.Numerics;
using Google.Protobuf;
using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.Common.Protocol;
using Griffin.Common.Table;
using Griffin.GameServer.Extensions;
using Griffin.GameServer.Services;

namespace Griffin.GameServer.Handlers
{
    /// <summary>
    /// Expanded drill (扩编实练, SimCombatUnite): a rotating mission of 11 stages whose stars (bits of each stage record's complete_challenges)
    /// unlock the rewards of <c>UniteStarRewardData</c>. The reward id the client sends is the star threshold (col 4) and the claimed ones are kept per
    /// mission in <c>simcombat.unite.claimed</c> (A-156).
    /// </summary>
    public sealed class SimCombatUniteHandler(ITableService tables, IAccountStateService accountState, IRewardService rewards) : SimCombatUniteHandlerBase
    {
        private const string ClaimedKey = "simcombat.unite.claimed";

        public override void HandleSimCombatUniteInfo(CS_SimCombatUniteInfo request, Connection connection)
        {
            if (connection.Account is null)
                return;

            var info = new CLEKMDAGJGD();
            foreach (FILNFOFMAJN mission in OpenMissions(connection))
                info.GOFCFHDHIIO[mission.OHPHNKCEGCM] = mission;
            connection.Send(new SC_SimCombatUniteInfo { Info = info });
        }

        public override void HandleSimCombatUniteEnter(CS_SimCombatUniteEnter request, Connection connection)
        {
            if (connection.Account is null)
                return;

            connection.Send(new SC_SimCombatUniteEnter { MNIFLHHOPHK = { OpenMissions(connection) } });
        }

        public override void HandleSimCombatUniteGetHistory(CS_SimCombatUniteGetHistory request, Connection connection) =>
            connection.Send(new SC_SimCombatUniteGetHistory());

        public override void HandleSimCombatUniteClaimReward(CS_SimCombatUniteClaimReward request, Connection connection)
        {
            if (connection.Account is null)
                return;

            Claim(connection, MsgId.MsgCsSimCombatUniteClaimReward, request.OHPHNKCEGCM, [request.KNAHJGMNKIL], all: false);
        }

        public override void HandleSimCombatUniteClaimAllReward(CS_SimCombatUniteClaimAllReward request, Connection connection)
        {
            if (connection.Account is null)
                return;

            Claim(connection, MsgId.MsgCsSimCombatUniteClaimAllReward, request.OHPHNKCEGCM, [], all: true);
        }

        private void Claim(Connection connection, MsgId requestId, uint missionId, uint[] wanted, bool all)
        {
            uint uid = connection.Account!.Uid;
            if (MissionRow(missionId) is not { } mission || OpenMissions(connection).All(m => m.OHPHNKCEGCM != missionId))
            {
                connection.Refuse(requestId, HNINDAOJCKP.SimCombatUniteMissionIdNotOpen);
                return;
            }

            (uint Threshold, string Rewards)[] tiers = Tiers(TableFields.U(mission, 3));
            int stars = StarsOf(uid, mission);
            List<uint> claimed = Claimed(uid).GetValueOrDefault(missionId.ToString()) ?? [];
            var granted = new List<uint>();
            var messages = new List<IMessage>();
            foreach ((uint threshold, string text) in tiers)
            {
                if (stars < threshold || claimed.Contains(threshold) || (!all && !wanted.Contains(threshold)))
                    continue;

                foreach ((uint item, int count) in rewards.ParseRewards(text))
                    rewards.Grant(uid, item, count, messages);
                granted.Add(threshold);
            }

            if (granted.Count == 0)
            {
                connection.Refuse(requestId, HNINDAOJCKP.SimCombatUniteMissionIdNotOpen);
                return;
            }

            claimed.AddRange(granted);
            var all2 = Claimed(uid);
            all2[missionId.ToString()] = claimed;
            accountState.Set(uid, ClaimedKey, all2);

            IMessage answer = all
                ? new SC_SimCombatUniteClaimAllReward { OHPHNKCEGCM = missionId, LDOMDNMPOKP = { granted } }
                : new SC_SimCombatUniteClaimReward { OHPHNKCEGCM = missionId, LDOMDNMPOKP = { granted } };
            // the client adds the item deltas itself: the answer goes first and the totals after it
            messages.Insert(0, answer);
            connection.SendAutoEncrypted(messages.ToArray());
        }

        /// <summary>The missions whose plan (PlanData of system 9: col 4 start, col 5 end, col 6 the mission) is open on the game clock.</summary>
        private List<FILNFOFMAJN> OpenMissions(Connection connection)
        {
            ulong now = checked((ulong)connection.GameClockSeconds());
            var open = new List<FILNFOFMAJN>();
            foreach (PlanData plan in tables.GetTable<PlanData>().Where(r => TableFields.U(r, 2) == 9 && TableFields.U64(r, 4) <= now && (TableFields.U64(r, 5) == 0 || now <= TableFields.U64(r, 5))))
            {
                uint missionId = TableFields.List(plan, 6).FirstOrDefault();
                if (MissionRow(missionId) is null)
                    continue;

                var mission = new FILNFOFMAJN { OHPHNKCEGCM = missionId, PlanId = TableFields.U(plan, 1) };
                if (Claimed(connection.Account!.Uid).GetValueOrDefault(missionId.ToString()) is { } claimed)
                    mission.LDOMDNMPOKP.AddRange(claimed);
                open.Add(mission);
            }

            return open;
        }

        private Dictionary<string, List<uint>> Claimed(uint uid) => accountState.Get<Dictionary<string, List<uint>>>(uid, ClaimedKey) ?? [];

        private UniteRotateMissionData? MissionRow(uint missionId) => tables.GetTable<UniteRotateMissionData>().FirstOrDefault(r => TableFields.U(r, 1) == missionId);

        /// <summary>The star tiers of a reward group: threshold (col 4) with the reward string of the same position (col 5, tiers split by ';').</summary>
        private (uint, string)[] Tiers(uint group)
        {
            UniteStarRewardData? row = tables.GetTable<UniteStarRewardData>().FirstOrDefault(r => TableFields.U(r, 2) == group);
            if (row is null)
                return [];

            IReadOnlyList<uint> thresholds = TableFields.List(row, 4);
            string[] texts = TableFields.S(row, 5).Trim('\'').Split(';');
            return thresholds.Select((threshold, index) => (threshold, index < texts.Length ? texts[index] : string.Empty)).ToArray();
        }

        /// <summary>Stars of a mission: the cleared challenges (set bits) of the records of its stages.</summary>
        private int StarsOf(uint uid, UniteRotateMissionData mission)
        {
            var records = accountState.Get<Dictionary<string, StageRecord>>(uid, "stage.records") ?? [];
            var stages = tables.GetTable<UniteStageData>().ToDictionary(r => TableFields.U(r, 1), r => TableFields.U(r, 3));
            int stars = 0;
            foreach (uint unite in TableFields.List(mission, 4))
                if (stages.TryGetValue(unite, out uint stage) && records.TryGetValue(stage.ToString(), out StageRecord? record))
                    stars += BitOperations.PopCount(record.CompleteChallenges);
            return stars;
        }
    }
}
