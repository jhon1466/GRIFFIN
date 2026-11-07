using Google.Protobuf;
using Griffin.Common.Proto;
using Griffin.Common.Table;

namespace Griffin.GameServer.Services
{
    /// <summary>
    /// Endless tower of the events (OKCEDNEOBDL). Every floor is an ordinary stage: EventEndlessTowerMissionGroupData (2 = tower season,
    /// 3 = floor, 6 = stage, 7 = first-pass reward). Winning it raises the highest floor (FCDLKJNBDGM field 3, the client asks for floor + 1
    /// next), pays the first-pass reward once and pushes SC_EventEndlessTowerRefresh. Field 5 is the claimed target reward.
    /// ASSUMPTION A-194: the season of a tower event is the season of the last floor fought (the event row does not name it).
    /// </summary>
    public interface IEndlessTowerService : IGameService
    {
        FCDLKJNBDGM Info(uint accountUid, uint activityId);

        void OnStageWon(uint accountUid, uint stageId, List<IMessage> push);

        void ClaimTarget(uint accountUid, uint activityId);
    }

    public sealed class EndlessTowerService(ITableService tableService, IAccountStateService accountState, IRewardService rewardService) : IEndlessTowerService
    {
        public sealed class State
        {
            public Dictionary<uint, uint> Floor { get; set; } = [];
            public List<uint> FirstPass { get; set; } = [];
            public uint Season { get; set; }
            public List<uint> TargetClaimed { get; set; } = [];
        }

        private const string Key = "event.endlesstower";

        private State Load(uint uid) => accountState.Get<State>(uid, Key) ?? new State();

        public FCDLKJNBDGM Info(uint uid, uint activityId)
        {
            State state = Load(uid);
            return new FCDLKJNBDGM
            {
                IKDMNOBMIFE = activityId,
                EPCCANNLACO = state.Season,
                FKJJHMGFGOJ = state.Floor.GetValueOrDefault(state.Season),
                IIHFBMLMAKC = state.TargetClaimed.Contains(activityId),
                UpdateTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };
        }

        public void OnStageWon(uint uid, uint stageId, List<IMessage> push)
        {
            EventEndlessTowerMissionGroupData? floor = tableService.GetTable<EventEndlessTowerMissionGroupData>().FirstOrDefault(r => r.StageId == stageId);
            if (floor is null)
                return;

            lock (AccountLock.For(uid))
            {
                State state = Load(uid);
                state.Season = floor.NDMIOPNEEIO;
                state.Floor[floor.NDMIOPNEEIO] = Math.Max(state.Floor.GetValueOrDefault(floor.NDMIOPNEEIO), floor.Num);
                if (!state.FirstPass.Contains(floor.Id))
                {
                    state.FirstPass.Add(floor.Id);
                    if (floor.GEKJLMBKFJB is { } reward)
                        for (int i = 0; i < Math.Min(reward.BAALJFJDGBB.Count, reward.Value.Count); i++)
                            rewardService.Grant(uid, reward.BAALJFJDGBB[i], (int)reward.Value[i], push);
                }

                accountState.Set(uid, Key, state);
            }

            push.Add(new SC_EventEndlessTowerRefresh { IHNCKKDOJLL = Info(uid, 0) });
        }

        public void ClaimTarget(uint uid, uint activityId)
        {
            lock (AccountLock.For(uid))
            {
                State state = Load(uid);
                if (!state.TargetClaimed.Contains(activityId))
                    state.TargetClaimed.Add(activityId);
                accountState.Set(uid, Key, state);
            }
        }
    }
}
